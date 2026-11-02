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
        await WobaCompletionChecksAsync();
        Console.WriteLine("PASS: one-shot manual MAINAP/EXTENDER/SETOBCOMPLETE bytes, incomplete identity/UPGRADING, stale-device refusal, cancellation and no retry after lost acknowledgement.");
    }

    static async Task WobaCompletionChecksAsync()
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var before = new WobaInspection(new("W30", "TESTSERIAL123", "001122334455"), "192.168.7.1",
            Convert.ToBase64String(new byte[32]), "MAINAP", "AXR.0814.240522.10", true,
            new Dictionary<string, System.Text.Json.Nodes.JsonObject>
            {
                ["OB_SWUPDATESTATUS_ALL"] = System.Text.Json.Nodes.JsonNode.Parse("""{"OB_SWUPDATESTATUS":{"UPGRADE_STATUS":"LatestSoftwareInstalled","PROGRESS_PERCENTAGE":"0"}}""")!.AsObject()
            }, [], DateTimeOffset.UtcNow, "1");
        var wire = new Wire { Mode = "MAINAP" };
        var refreshed = 0; var verified = 0;
        var attempt = new WobaBleCompletion(wire, before);
        Task<WobaInspection> Refresh(CancellationToken token) { refreshed++; return Task.FromResult(before); }
        Task Verify(CancellationToken token) { verified++; Check(wire.Writes.Count == 0, "Completion ran before checking normal admin access."); return Task.CompletedTask; }
        await attempt.SendAsync(Refresh, Verify, CancellationToken.None);
        Check(refreshed == 1 && verified == 1 && wire.Writes.SequenceEqual(new[] { (BleSetupProtocol.Complete, BleSetupProtocol.CompleteValue) }), "WOBA completion did not send exactly the observed command after both checks.");
        try { await attempt.SendAsync(Refresh, Verify, CancellationToken.None); throw new InvalidOperationException("WOBA completion repeated."); }
        catch (RouterException) { }
        Check(wire.Writes.Count == 1 && refreshed == 1, "The closed completion attempt was reused.");

        foreach (var scenario in new[] { "normal-login", "changed-pin", "changed-identity", "changed-role", "changed-address", "busy", "complete", "bluetooth-serial", "bluetooth-mac", "cancel", "lost" })
        {
            var target = new Wire { Mode = "MAINAP" };
            var fresh = before;
            if (scenario == "changed-pin") fresh = fresh with { Pin = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()) };
            if (scenario == "changed-identity") fresh = fresh with { Device = before.Device with { Serial = "OTHERSERIAL123" } };
            if (scenario == "changed-role") fresh = fresh with { Role = "EXTENDER" };
            if (scenario == "changed-address") fresh = fresh with { Address = "192.168.7.2" };
            if (scenario == "complete") fresh = fresh with { Incomplete = false, OnboardingStatus = "0" };
            if (scenario == "busy") fresh = fresh with { Data = new Dictionary<string, System.Text.Json.Nodes.JsonObject>() };
            if (scenario == "bluetooth-serial") target.Serial = "OTHERSERIAL123";
            if (scenario == "bluetooth-mac") target.Mac = "001122334499";
            if (scenario == "lost") target.FailWrite = true;
            using var stop = new CancellationTokenSource();
            if (scenario == "cancel") stop.Cancel();
            var single = new WobaBleCompletion(target, before);
            Task VerifyNormal(CancellationToken token) => scenario == "normal-login"
                ? Task.FromException(new RouterException("Fictional failed admin login.")) : Task.CompletedTask;
            for (var i = 0; i < 2; i++)
            {
                try
                {
                    await single.SendAsync(_ => Task.FromResult(fresh), VerifyNormal, stop.Token);
                    throw new InvalidOperationException("Invalid WOBA completion allowed: " + scenario);
                }
                catch (RouterException) { }
                catch (OperationCanceledException) { }
            }
            Check(target.Writes.Count == (scenario == "lost" ? 1 : 0), "Unexpected or repeated completion write: " + scenario);
        }
        Console.WriteLine("PASS: WOBA-to-Bluetooth completion requires fresh pinned identity, idle updates, verified normal admin login and matching Bluetooth identity; one command, cancellation and no uncertain-write retry.");
    }

    sealed class Wire : IBleSetupWire
    {
        public string App = "maxrouter", Mode = "OB", Serial = "TESTSERIAL123", Mac = "001122334455";
        public bool Writable = true, FailWrite;
        public List<(string, string)> Writes = [];
        public bool CanWrite(string uuid) => Writable;
        public Task<byte[]> ReadAsync(string uuid, CancellationToken token) => Task.FromResult(Encoding.UTF8.GetBytes(
            uuid == BlePrimaryInitialization.AppName ? App : uuid == BlePrimaryInitialization.Mode ? Mode :
            uuid == BleSetupProtocol.State ? "UPGRADING" : uuid == BleSetupProtocol.Mac ? Mac : Serial));
        public Task WriteAsync(string uuid, byte[] value, CancellationToken token)
        {
            Writes.Add((uuid, Encoding.UTF8.GetString(value)));
            return FailWrite ? Task.FromException(new IOException("Fictional lost reply.")) : Task.CompletedTask;
        }
    }
}
