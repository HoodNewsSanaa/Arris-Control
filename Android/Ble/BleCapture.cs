using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArrisControl;

internal sealed record BleCaptureCharacteristic(string Service, string Uuid, string Properties, bool Readable);
internal sealed record BleCaptureField(string Uuid, string Label, bool Private = false, bool Poll = false);

// An acknowledged negative reply ends that read, so another read is permitted.
// A timeout, cancellation or dropped link has an uncertain outcome and ends capture.
internal sealed class BleReadRejectedException(int status)
    : RouterException("Bluetooth read rejected: code " + status + ".", "BLE_READ_REJECTED")
{
    public int Status { get; } = status;
}

// No write/notification API is supplied to this recorder. Unknown fields and
// credential/key slots can be listed by UUID but can never be read by it.
internal sealed class BleCapture
{
    static string Uuid(int id) => $"0000{id:x4}-0000-1000-8000-00805f9b34fb";
    internal static readonly string Service = Uuid(0xaa00);
    internal static readonly IReadOnlyList<BleCaptureField> Fields = Array.AsReadOnly(new BleCaptureField[]
    {
        new(Uuid(0xaa02), "Device application"), new(Uuid(0xaa07), "Role", Poll: true),
        new(Uuid(0xaa04), "Serial", Private: true), new(Uuid(0xaa05), "WAN MAC", Private: true),
        new(Uuid(0xaa06), "Model"), new(Uuid(0xaa08), "Onboarding state", Poll: true),
        new(Uuid(0xaa11), "WAN status", Poll: true), new(Uuid(0xaa12), "Download progress", Poll: true),
        new(Uuid(0xaa13), "Upgrade status", Poll: true),
        new(Uuid(0xaa09), "Firmware 1"), new(Uuid(0xaa0a), "Firmware 2"),
        new(Uuid(0xaa0b), "Firmware 3"), new(Uuid(0xaa0c), "Firmware 4")
    });
    readonly Func<string, CancellationToken, Task<byte[]>> read;
    readonly IReadOnlyList<BleCaptureCharacteristic> map;
    readonly StringBuilder lines = new();
    readonly byte[] maskKey = RandomNumberGenerator.GetBytes(32);
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    readonly DateTimeOffset start = DateTimeOffset.UtcNow;
    int samples;
    bool ended;
    public bool Interrupted { get; private set; }

    public BleCapture(IReadOnlyList<BleCaptureCharacteristic> map,
        Func<string, CancellationToken, Task<byte[]>> read, string environment, string deviceAddress)
    {
        this.map = map.ToArray(); this.read = read;
        lines.AppendLine("ARRIS Bluetooth state capture v1");
        lines.AppendLine("Started UTC: " + start.ToString("O"));
        lines.AppendLine("Environment: " + JsonSerializer.Serialize(environment));
        lines.AppendLine("Selected device: " + Mask(Encoding.UTF8.GetBytes(deviceAddress)));
        lines.AppendLine("Read-only. No writes, subscriptions, cloud requests or firmware downloads.");
        lines.AppendLine("Identity bytes are withheld. Tokens are consistent only within this capture.");
        lines.AppendLine("Other allowlisted values retain exact UTF-8 (JSON escaped) and hex, including NULs.");
        lines.AppendLine("UPGRADING alone does not prove an active download or a cloud requirement.");
        lines.AppendLine("Service map (all other values left unread):");
        foreach (var item in this.map.Take(256))
            lines.AppendLine($"  {GuidText(item.Service)} / {GuidText(item.Uuid)} · {JsonSerializer.Serialize(item.Properties)}");
        if (this.map.Count > 256) lines.AppendLine("  (map truncated to 256 characteristics)");
        lines.AppendLine("Timeline: UTC · elapsed ms · field · result");
    }
    static string GuidText(string value) => Guid.TryParse(value, out var uuid) ? uuid.ToString() : "(invalid UUID)";
    string Mask(byte[] value) => "<id:" + Convert.ToHexString(HMACSHA256.HashData(maskKey, value))[..12].ToLowerInvariant() + ">";
    void Event(string field, string result) =>
        lines.AppendLine($"{DateTimeOffset.UtcNow:O} · +{elapsed.ElapsedMilliseconds}ms · {field} · {result}");
    internal async Task SnapshotAsync(CancellationToken token)
    {
        if (ended || Interrupted) throw new InvalidOperationException("This capture has ended.");
        var first = samples++ == 0;
        foreach (var field in Fields.Where(f => first || f.Poll))
        {
            token.ThrowIfCancellationRequested();
            var matches = map.Where(m => m.Uuid.Equals(field.Uuid, StringComparison.OrdinalIgnoreCase)).ToArray();
            var label = field.Uuid[4..8] + " " + field.Label;
            if (matches.Length != 1 || !matches[0].Service.Equals(Service, StringComparison.OrdinalIgnoreCase))
            { Event(label, matches.Length == 0 ? "not exposed" : "ambiguous/wrong-service characteristic; not read"); continue; }
            if (!matches[0].Readable) { Event(label, "not readable; not read"); continue; }
            try
            {
                var value = await read(field.Uuid, token);
                if (field.Private)
                    Event(label, value.Length + " bytes; identity withheld " + Mask(value));
                else if (value.Length > 128)
                    Event(label, value.Length + " bytes; unexpectedly long response withheld");
                else
                {
                    string text;
                    try { text = JsonSerializer.Serialize(new UTF8Encoding(false, true).GetString(value)); }
                    catch (DecoderFallbackException) { text = "(invalid UTF-8)"; }
                    Event(label, value.Length + " bytes; utf8=" + text + "; hex=" + Convert.ToHexString(value));
                }
            }
            catch (BleReadRejectedException ex)
            { Event(label, "read rejected; GATT status=" + ex.Status + "; meaning not inferred"); }
            catch (OperationCanceledException) { Interrupted = true; Event(label, "cancelled; connection must close"); throw; }
            catch (Exception)
            {
                // Do not export arbitrary exception messages: platform messages
                // may contain Bluetooth addresses or other private values.
                Interrupted = true; Event(label, "read incomplete or connection lost; capture stopped"); return;
            }
        }
    }
    public async Task RunAsync(TimeSpan duration, TimeSpan interval, CancellationToken token, Action<string>? progress = null)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(3) || interval < TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(duration));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(duration);
        try
        {
            do
            {
                await SnapshotAsync(stop.Token);
                progress?.Invoke(Report);
                if (Interrupted) break;
                await Task.Delay(interval, stop.Token);
            } while (!stop.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            ended = true;
            Event("capture", token.IsCancellationRequested ? "stopped by user/app" :
                Interrupted ? "stopped after incomplete operation" : "recording window ended");
            progress?.Invoke(Report);
            CryptographicOperations.ZeroMemory(maskKey);
        }
    }
    public string Report => lines.ToString();
}
