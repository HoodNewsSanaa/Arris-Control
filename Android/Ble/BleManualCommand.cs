using System.Text;

namespace ArrisControl;

internal sealed record BleManualResult(string ReadBack, bool ReadBackAvailable);

// WOBA supplies the pinned identity and update state; completion itself uses
// the observed Bluetooth command. A normal saved login must work first.
internal sealed class WobaBleCompletion(IBleSetupWire wire, WobaInspection expected)
{
    int used;
    public async Task<BleManualResult> SendAsync(
        Func<CancellationToken, Task<WobaInspection>> refresh,
        Func<CancellationToken, Task> verifyNormalAdmin, CancellationToken token)
    {
        if (Interlocked.Exchange(ref used, 1) != 0)
            throw new RouterException("This completion attempt is closed. Check its result before starting another attempt.");
        token.ThrowIfCancellationRequested();
        WobaSession.CheckWritable(expected);
        var fresh = await refresh(token);
        WobaSession.CheckWritable(fresh);
        if (fresh.Device != expected.Device || fresh.Role != expected.Role || fresh.Firmware != expected.Firmware ||
            fresh.Address != expected.Address || fresh.Pin != expected.Pin)
            throw new RouterException("The selected router changed since inspection. No completion command was sent.");
        await verifyNormalAdmin(token);
        token.ThrowIfCancellationRequested();
        var identity = new Dictionary<string, string>
        {
            [BlePrimaryInitialization.AppName] = "maxrouter",
            [BlePrimaryInitialization.Mode] = expected.Role,
            [BleSetupProtocol.Serial] = expected.Device.Serial,
            [BleSetupProtocol.Mac] = expected.Device.Mac
        };
        return await new BleManualCommand(wire, identity).SendAsync(BleSetupProtocol.CompleteValue, token);
    }
}

// A manual experiment, deliberately separate from guided provisioning. It
// sends exactly one observed command and never claims onboarding succeeded.
internal sealed class BleManualCommand(IBleSetupWire wire, IReadOnlyDictionary<string, string> expected)
{
    int used;
    static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).Trim('\0', ' ', '\r', '\n', '\t');
    static string Canonical(string uuid, string value) => uuid == BleSetupProtocol.Mac
        ? BleSetupProtocol.NormalizeMac(value) : uuid == BleSetupProtocol.Serial ? value.Trim().ToUpperInvariant() : value;
    public async Task<BleManualResult> SendAsync(string value, CancellationToken token)
    {
        if (Interlocked.Exchange(ref used, 1) != 0)
            throw new RouterException("This manual attempt is closed. Inspect the unit again before another attempt.");
        var mode = BleSetupProtocol.Uuid(0xaa07);
        var app = BleSetupProtocol.Uuid(0xaa02);
        var target = value switch
        {
            "MAINAP" or "EXTENDER" => mode,
            BleSetupProtocol.CompleteValue => BleSetupProtocol.Complete,
            _ => throw new RouterException("Unsupported manual Bluetooth command.")
        };
        var knownFields = new[] { app, mode, BleSetupProtocol.Serial, BleSetupProtocol.Mac };
        if (expected.Keys.Any(k => !knownFields.Contains(k)))
            throw new RouterException("Unsupported identity field in manual attempt.");
        if (!wire.CanWrite(target)) throw new RouterException("The selected unit does not permit this acknowledged write.");
        token.ThrowIfCancellationRequested();
        if (!Text(await wire.ReadAsync(app, token)).Equals("maxrouter", StringComparison.OrdinalIgnoreCase))
            throw new RouterException("The connected unit did not identify itself as maxrouter. Nothing was written.");
        foreach (var field in expected.Where(f => f.Value.Length > 0))
        {
            var current = Text(await wire.ReadAsync(field.Key, token));
            if (Canonical(field.Key, current) != Canonical(field.Key, field.Value))
                throw new RouterException("The unit's previously read identity or role changed. Nothing was written. Inspect it again.");
        }
        token.ThrowIfCancellationRequested();
        try { await wire.WriteAsync(target, Encoding.UTF8.GetBytes(value), token); }
        catch
        {
            throw new RouterException("One manual write was attempted, but its acknowledgement is unknown. It may have applied. No command was retried. Inspect the unit again.");
        }
        // Completion itself can be write-only; inspect the nonsecret state.
        try
        {
            var result = Text(await wire.ReadAsync(target == mode ? mode : BleSetupProtocol.State, token));
            return new(result, true);
        }
        catch { return new("", false); }
    }
}
