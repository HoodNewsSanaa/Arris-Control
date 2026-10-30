using System.Text;

namespace ArrisControl;

internal static class BleManualCommandSelfTests
{
    public static async Task RunAsync()
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static async Task Refuse(Func<Task> action)
        {
            try { await action(); } catch (RouterException) { return; } catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("An invalid manual command was allowed.");
        }
        foreach (var value in new[] { "MAINAP", "EXTENDER", BleSetupProtocol.CompleteValue })
        {
            var wire = new Wire();
            var attempt = new BleManualCommand(wire, new Dictionary<string, string> { [BlePrimaryInitialization.Mode] = "OB" });
            var result = await attempt.SendAsync(value, CancellationToken.None);
            var expectedUuid = value == BleSetupProtocol.CompleteValue ? BleSetupProtocol.Complete : BlePrimaryInitialization.Mode;
            Check(wire.Writes.Count == 1 && wire.Writes[0] == (expectedUuid, value), "Incorrect manual payload or repeat write.");
            Check(result.ReadBackAvailable, "Manual read-back was not returned.");
            await Refuse(() => attempt.SendAsync(value, CancellationToken.None));
            Check(wire.Writes.Count == 1, "Manual attempt was repeated.");
        }
        var missing = new Wire();
        await new BleManualCommand(missing, new Dictionary<string, string>()).SendAsync("MAINAP", CancellationToken.None);
        Check(missing.Writes.Count == 1, "Manual role selection incorrectly required missing identity.");
        var stale = new Wire();
        await Refuse(() => new BleManualCommand(stale, new Dictionary<string, string> { [BlePrimaryInitialization.Mode] = "MAINAP" }).SendAsync("EXTENDER", CancellationToken.None));
        Check(stale.Writes.Count == 0, "A stale role was ignored.");
        var staleSerial = new Wire();
        await Refuse(() => new BleManualCommand(staleSerial, new Dictionary<string, string> { [BleSetupProtocol.Serial] = "OTHER1234567" }).SendAsync("MAINAP", CancellationToken.None));
        Check(staleSerial.Writes.Count == 0, "A stale identity was ignored.");
        var wrong = new Wire { App = "other-device" };
        await Refuse(() => new BleManualCommand(wrong, new Dictionary<string, string>()).SendAsync("MAINAP", CancellationToken.None));
        Check(wrong.Writes.Count == 0, "A different device application was allowed.");
        var locked = new Wire { Writable = false };
        await Refuse(() => new BleManualCommand(locked, new Dictionary<string, string>()).SendAsync("MAINAP", CancellationToken.None));
        Check(locked.Writes.Count == 0, "Missing write permission was ignored.");
        var uncertain = new Wire { FailWrite = true };
        var failed = new BleManualCommand(uncertain, new Dictionary<string, string>());
        await Refuse(() => failed.SendAsync("MAINAP", CancellationToken.None));
        await Refuse(() => failed.SendAsync("MAINAP", CancellationToken.None));
        Check(uncertain.Writes.Count == 1, "An uncertain write was retried.");
        var cancelled = new Wire();
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Refuse(() => new BleManualCommand(cancelled, new Dictionary<string, string>()).SendAsync("MAINAP", stop.Token));
        await Refuse(() => new BleManualCommand(cancelled, new Dictionary<string, string>()).SendAsync("INVALID", CancellationToken.None));
        Check(cancelled.Writes.Count == 0, "Cancellation/invalid command sent a write.");
        Console.WriteLine("PASS: one-shot manual MAINAP/EXTENDER/SETOBCOMPLETE bytes, incomplete identity/UPGRADING, stale-device refusal, cancellation and no retry after lost acknowledgement.");
    }
    sealed class Wire : IBleSetupWire
    {
        public string App = "maxrouter";
        public bool Writable = true, FailWrite;
        public List<(string, string)> Writes = [];
        public bool CanWrite(string uuid) => Writable;
        public Task<byte[]> ReadAsync(string uuid, CancellationToken token) => Task.FromResult(Encoding.UTF8.GetBytes(
            uuid == BlePrimaryInitialization.AppName ? App : uuid == BlePrimaryInitialization.Mode ? "OB" :
            uuid == BleSetupProtocol.State ? "UPGRADING" : "TESTSERIAL123"));
        public Task WriteAsync(string uuid, byte[] value, CancellationToken token)
        {
            Writes.Add((uuid, Encoding.UTF8.GetString(value)));
            return FailWrite ? Task.FromException(new IOException("Fictional lost reply.")) : Task.CompletedTask;
        }
    }
}
