using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArrisControl;

// A URL-only experiment. CHECKNOW, VERSION and firmware actions are deliberately
// absent. A failed or uncertain write is never repeated automatically.
internal sealed record FirmwareLookupPlan(string Serial, string Mac, string Original, string Recorder)
{
    public const string Vendor = "https://plt.surfboardconnect.com/manifest";
    public override string ToString() => "Firmware lookup recording (private addresses withheld)";
    internal static bool AllowedSource(string value)
    {
        if (value == Vendor) return true;
        return Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == "http" &&
            u.Port == 8765 && CredentialStore.IsPrivate(u.Host) && u.UserInfo.Length == 0 &&
            u.Query.Length == 0 && u.Fragment.Length == 0 &&
            Regex.IsMatch(u.AbsolutePath, "^/arris-lookup/[a-f0-9]{32}/manifest$") && u.AbsoluteUri == value;
    }
    internal static string Body(string url)
    {
        if (!AllowedSource(url)) throw new RouterException("Unsupported firmware lookup address.");
        return new JsonObject { ["REQUEST"] = new JsonObject {
            ["OPERTYPE"] = "MODIFY", ["UPDATE"] = new JsonObject { ["CONTAINER"] = "Update", ["URL"] = url }
        }}.ToJsonString();
    }
}

internal sealed partial class RouterClient
{
    internal async Task<string> ReadFirmwareLookupSourceAsync(CancellationToken token, bool preparing = true)
    {
        await ConnectAsync(token); await VerifyIdentityAsync(token);
        var info = (await ReadAsync("DEVICE_INFO_ALL", token))["DEVICE_INFO"];
        if (Catalog.Text(info?["MODEL_NAME"]) != "W30" || Catalog.Text(info?["HARDWARE_VERSION"]) != "WR11AXER1" ||
            Catalog.Text(info?["SOFTWARE_VERSION"]) != "AXR.0814.240522.10")
            throw new RouterException("Lookup recording is limited to the inspected W30 hardware and firmware.");
        var state = (await ReadAsync("DEVICESTATUS_ALL", token))["DEVICESTATUS"];
        if (Catalog.Text(state?["BOOTING"]) != "0" || Catalog.Text(state?["SOFTWAREUPDATEACTIVE"]) != "0")
            throw new RouterException("Startup or a software update is active or could not be ruled out. Nothing was sent.");
        var fw = (await ReadAsync("FIRMWARE_ALL", token))["FIRMWARE"];
        if (Catalog.Text(fw?["UPGRADE_SW"]) != "0") throw new RouterException("The firmware upgrade flag must be inactive.");
        var update = (await ReadAsync("UPDATE_ALL", token))["UPDATE"];
        if (Catalog.Text(update?["CURRENT_VERSION"]) != "AXR.0814.240522.10" ||
            preparing && (Catalog.Text(update?["UPGRADE_VERSION"]) is not ("" or "AXR.0814.240522.10") ||
            Catalog.Text(update?["STATUS"]) is not ("Unknown" or "Idle") || Catalog.Text(update?["PERCENTAGE"]) != "0"))
            throw new RouterException("The update state is not the inspected idle state. Nothing was sent.");
        return Catalog.Text(update?["URL"]);
    }
    public async Task<FirmwareLookupPlan> PrepareFirmwareLookupAsync(string recorder, CancellationToken token)
    {
        if (!FirmwareLookupPlan.AllowedSource(recorder) || recorder == FirmwareLookupPlan.Vendor)
            throw new RouterException("Start the local recorder first.");
        var before = await ReadFirmwareLookupSourceAsync(token);
        if (before != FirmwareLookupPlan.Vendor)
            throw new RouterException("The source differs from the verified ARRIS address. Restore any pending recording first.");
        return new(record.Serial, record.Mac, before, recorder);
    }
    internal void CheckLookupIdentity(FirmwareLookupPlan plan)
    {
        if (plan.Serial != record.Serial || plan.Mac != record.Mac || plan.Original != FirmwareLookupPlan.Vendor ||
            !FirmwareLookupPlan.AllowedSource(plan.Recorder) || plan.Recorder == plan.Original)
            throw new RouterException("This recording belongs to another router or has invalid addresses.");
    }
    public async Task StartFirmwareLookupAsync(FirmwareLookupPlan plan, CancellationToken token)
    {
        CheckLookupIdentity(plan);
        if (await ReadFirmwareLookupSourceAsync(token) != plan.Original)
            throw new RouterException("The update source changed after review. Nothing was sent.");
        await wire.SendAsync("/setData", FirmwareLookupPlan.Body(plan.Recorder), token);
        if (Catalog.Text((await ReadAsync("UPDATE_ALL", token))["UPDATE"]?["URL"]) != plan.Recorder)
            throw new RouterException("The recorder address was not verified. No update command will be tried.");
    }
    public async Task RestoreFirmwareLookupAsync(FirmwareLookupPlan plan, CancellationToken token)
    {
        CheckLookupIdentity(plan);
        // A failed lookup may change STATUS. Inactive boot/update flags and the
        // unchanged installed firmware still gate restoration; don't strand a
        // temporary URL merely because the recorder's intentional 503 was seen.
        var current = await ReadFirmwareLookupSourceAsync(token, preparing: false);
        if (current == plan.Original) return;
        if (current != plan.Recorder)
            throw new RouterException("The source changed to an unexpected address. Automatic restoration stopped.");
        await wire.SendAsync("/setData", FirmwareLookupPlan.Body(plan.Original), token);
        if (Catalog.Text((await ReadAsync("UPDATE_ALL", token))["UPDATE"]?["URL"]) != plan.Original)
            throw new RouterException("Restoration was not verified. Keep the recovery record and restore again after checking router status.");
    }
}

// Separate encrypted recovery record survives process death. It never stores
// router login passwords. Captured requests may identify the device: encrypted
// locally, never included in diagnostics or sent to a vendor automatically.
internal static class FirmwareLookupFiles
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ArrisControl.FirmwareLookup.v1");
    internal static string PathFor(string folder, string serial, string mac, string kind) =>
        Path.Combine(folder, "lookup-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial + "|" + mac))) + "." + kind);
    internal static void Save(string path, JsonObject value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
        try
        {
            var cipher = CredentialProtection.Protect(bytes, Entropy);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp"; File.WriteAllBytes(temp, cipher); File.Move(temp, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static JsonObject Load(string path)
    {
        var plain = CredentialProtection.Unprotect(File.ReadAllBytes(path), Entropy);
        try { return JsonNode.Parse(plain)!.AsObject(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static void SavePlan(string path, FirmwareLookupPlan plan) => Save(path, new JsonObject {
        ["Serial"] = plan.Serial, ["Mac"] = plan.Mac, ["Original"] = plan.Original, ["Recorder"] = plan.Recorder });
    internal static FirmwareLookupPlan LoadPlan(string path)
    {
        var o = Load(path);
        return new(Catalog.Text(o["Serial"]), Catalog.Text(o["Mac"]), Catalog.Text(o["Original"]), Catalog.Text(o["Recorder"]));
    }
}

internal sealed class FirmwareLookupRecorder : IDisposable
{
    readonly TcpListener listener;
    readonly IPAddress expected;
    readonly string path;
    public string Address { get; }
    public FirmwareLookupRecorder(IPAddress local, IPAddress router, int port = 8765)
    {
        if (!CredentialStore.IsPrivate(local.ToString()) || !CredentialStore.IsPrivate(router.ToString()))
            throw new RouterException("A separate phone address on the router's local network is required.");
        expected = router;
        path = "/arris-lookup/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() + "/manifest";
        listener = new TcpListener(local, port); listener.Start(4);
        Address = $"http://{local}:{((IPEndPoint)listener.LocalEndpoint).Port}{path}";
    }
    public async Task<JsonObject?> RecordAsync(Action<JsonObject> persist, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var peer = await listener.AcceptTcpClientAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            if (peer.Client.RemoteEndPoint is not IPEndPoint remote || !remote.Address.Equals(expected)) continue;
            await using var stream = peer.GetStream();
            try
            {
                // Bound every header line and total input without waiting for EOF.
                var buffer = new StringBuilder(); var block = new byte[1];
                while (buffer.Length < 16384)
                {
                    if (await stream.ReadAsync(block, timeout.Token) == 0) break;
                    if (block[0] is > 127 or 0) throw new IOException("Invalid request header.");
                    buffer.Append((char)block[0]);
                    var n = buffer.Length;
                    if (n >= 4 && buffer[n-4] == '\r' && buffer[n-3] == '\n' && buffer[n-2] == '\r' && buffer[n-1] == '\n') break;
                }
                var raw = buffer.ToString();
                var capture = ParseRequest(raw, path);
                if (capture is not null) persist(capture); // Save before responding; never echo request data.
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token);
                if (capture is not null) return capture;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { if (token.IsCancellationRequested) throw; }
        }
        return null;
    }
    internal static JsonObject? ParseRequest(string raw, string path)
    {
        var first = raw.Split("\r\n", StringSplitOptions.None)[0].Split(' ');
        if (raw.Length > 16384 || !raw.EndsWith("\r\n\r\n", StringComparison.Ordinal) || first.Length != 3 ||
            first[0] is not ("GET" or "HEAD") || first[2] is not ("HTTP/1.0" or "HTTP/1.1") ||
            !(first[1] == path || first[1].StartsWith(path + "?", StringComparison.Ordinal))) return null;
        return new JsonObject { ["UTC"] = DateTimeOffset.UtcNow.ToString("O"), ["Method"] = first[0],
            ["Target"] = first[1], ["Headers"] = raw[(raw.IndexOf("\r\n", StringComparison.Ordinal) + 2)..] };
    }
    internal static string Summary(JsonObject capture)
    {
        var target = Catalog.Text(capture["Target"]); var index = target.IndexOf('?');
        var names = index < 0 ? [] : target[(index + 1)..].Split('&').Take(64).Select(p => p.Split('=')[0])
            .Select(p => Regex.IsMatch(p, "^[A-Za-z0-9_.-]{1,80}$") ? p : "[encoded name]").ToArray();
        return "Recorded " + Catalog.Text(capture["Method"]) + ". Query fields: " +
            (names.Length == 0 ? "none" : string.Join(", ", names)) +
            ". Values and headers are encrypted on this phone. The recorder returned HTTP 503 with no body.";
    }
    public void Dispose() => listener.Stop();
}
