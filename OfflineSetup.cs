using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArrisControl;

// Observed in SURFboard Central 4.3.5: these are fragments of one value,
// not radio/profile numbers. Slot 1 is written last and commits the value.
internal interface IBleSetupWire
{
    bool CanWrite(string uuid);
    Task<byte[]> ReadAsync(string uuid, CancellationToken token);
    Task WriteAsync(string uuid, byte[] value, CancellationToken token);
}
internal sealed record BleSetupWrite(string Uuid, string Value)
{
    public override string ToString() => $"Bluetooth setup write {Uuid[4..8]} (value withheld)";
}
internal sealed record OfflineSetupDraft(CloudDevice Device, RouterCredential Credential, string Ssid)
{
    public override string ToString() => "Pending offline setup (values withheld)";
}

internal static class BleSetupProtocol
{
    public static string Uuid(int id) => $"0000{id:x4}-0000-1000-8000-00805f9b34fb";
    public static readonly string Serial = Uuid(0xaa04), Mac = Uuid(0xaa05), Selector = Uuid(0xab1c), Complete = Uuid(0xaa1a);
    public const string CompleteValue = "SETOBCOMPLETE";
    public static readonly HashSet<string> WriteUuids = new(
        Enumerable.Range(0xab01, 8).Concat(Enumerable.Range(0xab1d, 4)).Concat([0xab1c, 0xaa1a]).Select(Uuid), StringComparer.OrdinalIgnoreCase);

    public static string NormalizeMac(string value) => Regex.Replace(value.Trim().ToUpperInvariant(), "[:-]", "");
    public static void ValidateIdentity(CloudDevice device)
    {
        if (!Regex.IsMatch(device.Serial, "^[A-Z0-9]{12,24}$") || !Regex.IsMatch(device.Mac, "^[A-F0-9]{12}$"))
            throw new RouterException("A complete router serial and WAN MAC must be read before offline setup.");
    }
    public static void Validate(OfflineSetupDraft draft, string wifiPassword)
    {
        ValidateIdentity(draft.Device);
        Ascii(draft.Credential.UserName, 1, 64, "Local username");
        Ascii(draft.Credential.Password, 12, 64, "Local admin password");
        Ascii(draft.Ssid, 1, 32, "Wi-Fi name");
        Ascii(wifiPassword, 8, 63, "Wi-Fi password");
    }
    static void Ascii(string value, int min, int max, string label)
    {
        // Restrict this experimental path to printable ASCII: the observed protocol
        // splits Java characters, while GATT limits bytes. Do not guess for Unicode.
        if (value.Length < min || value.Length > max || value.Any(c => c < 32 || c > 126))
            throw new RouterException($"{label} must contain {min}–{max} printable ASCII characters for offline setup.");
    }
    public static IReadOnlyList<BleSetupWrite> Fragments(int firstSlot, string value)
    {
        if (value.Length > 64 || value.Any(c => c < 32 || c > 126)) throw new RouterException("Invalid Bluetooth fragment value.");
        if (firstSlot is not (0xab01 or 0xab05 or 0xab1d)) throw new RouterException("Unsupported Bluetooth fragment slots.");
        return Enumerable.Range(0, 4).Reverse().Select(i => new BleSetupWrite(Uuid(firstSlot + i),
            value.Length <= i * 16 ? "" : value.Substring(i * 16, Math.Min(16, value.Length - i * 16)))).ToArray();
    }
    public static IReadOnlyList<BleSetupWrite> Credentials(OfflineSetupDraft draft) =>
        new[] { new BleSetupWrite(Selector, "SET:1") }.Concat(Fragments(0xab1d, draft.Credential.UserName))
        .Concat([new BleSetupWrite(Selector, "SET:2")]).Concat(Fragments(0xab1d, draft.Credential.Password)).ToArray();
    public static IReadOnlyList<BleSetupWrite> Wifi(OfflineSetupDraft draft, string password) =>
        Fragments(0xab01, draft.Ssid).Concat(Fragments(0xab05, password)).ToArray();
}

// A single confirmed attempt, bound to one BLE connection and identity. An error,
// cancellation or uncertain acknowledgement permanently stops this session.
// There is no cloud API, encryption-key read, secret read, retry or reset here.
internal sealed class BleSetupSession(IBleSetupWire wire, OfflineSetupDraft draft, string wifiPassword)
{
    int stage;
    int inFlight;
    public bool Faulted { get; private set; }
    public Task WriteCredentialsAsync(CancellationToken token) => Run(0, BleSetupProtocol.Credentials(draft), token);
    public Task WriteWifiAsync(CancellationToken token) => Run(1, BleSetupProtocol.Wifi(draft, wifiPassword), token);
    public Task CompleteAsync(CancellationToken token) => Run(2, [new(BleSetupProtocol.Complete, BleSetupProtocol.CompleteValue)], token);
    async Task Run(int expectedStage, IReadOnlyList<BleSetupWrite> writes, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0) throw new RouterException("Another setup step is still running.");
        if (Faulted || stage != expectedStage)
        {
            Volatile.Write(ref inFlight, 0);
            throw new RouterException("This setup step is locked. Reconnect and inspect the router before another attempt.");
        }
        var attempted = 0;
        try
        {
            BleSetupProtocol.Validate(draft, wifiPassword);
            // Preflight the entire flow before the first write, not just the next slot.
            if (BleSetupProtocol.WriteUuids.Any(uuid => !wire.CanWrite(uuid)))
                throw new RouterException("The router does not expose the complete writable mAX setup interface. Nothing was written in this step.");
            token.ThrowIfCancellationRequested();
            var serial = Encoding.UTF8.GetString(await wire.ReadAsync(BleSetupProtocol.Serial, token)).Trim('\0', ' ', '\r', '\n');
            var mac = BleSetupProtocol.NormalizeMac(Encoding.UTF8.GetString(await wire.ReadAsync(BleSetupProtocol.Mac, token)).Trim('\0', ' ', '\r', '\n'));
            if (serial != draft.Device.Serial || mac != draft.Device.Mac)
                throw new RouterException("The connected router identity changed. Nothing was written in this step.");
            foreach (var write in writes)
            {
                token.ThrowIfCancellationRequested();
                var bytes = Encoding.UTF8.GetBytes(write.Value);
                try { attempted++; await wire.WriteAsync(write.Uuid, bytes, token); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            stage++;
        }
        catch
        {
            Faulted = true;
            if (attempted > 0) throw new RouterException($"Offline setup stopped after sending {attempted} write(s) in this step. The last write may have applied. No writes were retried and setup success is unknown. Keep the pending login, reconnect to inspect the unit, and export the Bluetooth log.");
            throw;
        }
        finally { Volatile.Write(ref inFlight, 0); }
    }
}

// Preserve the proposed local login BEFORE provisioning. It is pending until an
// authenticated HTTPS login verifies the exact serial and pins the router key.
// No Wi-Fi password, ARRIS password or cloud token is stored here.
internal static class OfflineSetupStore
{
    public static readonly string Folder = Path.Combine(CredentialStore.UserDirectory, "OfflineSetup");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ArrisControl.OfflineSetup.v1");
    public static string Save(OfflineSetupDraft draft, string? directory = null)
    {
        BleSetupProtocol.ValidateIdentity(draft.Device);
        var plain = JsonSerializer.SerializeToUtf8Bytes(draft); byte[]? check = null;
        string? temporary = null;
        try
        {
            var cipher = CredentialProtection.Protect(plain, Entropy);
            check = CredentialProtection.Unprotect(cipher, Entropy);
            if (!CryptographicOperations.FixedTimeEquals(plain, check)) throw new RouterException("Pending login encryption verification failed. Nothing was written to the router.");
            var folder = directory ?? Folder; Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"pending-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.json");
            temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { Format = "ARRIS-OfflineSetup", Version = 1, Ciphertext = Convert.ToBase64String(cipher) }));
            File.Move(temporary, path); temporary = null; return path;
        }
        finally { CryptographicOperations.ZeroMemory(plain); if (check is not null) CryptographicOperations.ZeroMemory(check); if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
    }
    public static OfflineSetupDraft Load(string path)
    {
        byte[]? plain = null;
        try
        {
            if (new FileInfo(path).Length > 32768) throw new RouterException("Pending setup record is too large.");
            using var document = JsonDocument.Parse(File.ReadAllText(path)); var root = document.RootElement;
            if (root.GetProperty("Format").GetString() != "ARRIS-OfflineSetup" || root.GetProperty("Version").GetInt32() != 1) throw new RouterException("Unsupported pending setup record.");
            plain = CredentialProtection.Unprotect(Convert.FromBase64String(root.GetProperty("Ciphertext").GetString()!), Entropy);
            var draft = JsonSerializer.Deserialize<OfflineSetupDraft>(plain) ?? throw new RouterException("Empty pending setup record.");
            BleSetupProtocol.Validate(draft, "validation-only"); return draft;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or KeyNotFoundException)
        { throw new RouterException("This installation could not open the pending offline login."); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    public static IReadOnlyList<(string Path, OfflineSetupDraft Draft)> Find(string? directory = null)
    {
        var folder = directory ?? Folder; var found = new List<(string Path, OfflineSetupDraft Draft)>();
        if (Directory.Exists(folder)) foreach (var path in Directory.EnumerateFiles(folder, "pending-*.json").OrderDescending())
            try { found.Add((path, Load(path))); } catch (Exception ex) when (ex is IOException or RouterException) { }
        return found;
    }
}
