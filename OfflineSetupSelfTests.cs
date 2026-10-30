using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ArrisControl;

internal static class OfflineSetupSelfTests
{
    static readonly CloudDevice Device = new("W31", "TESTSERIAL123", "001122334455");
    static OfflineSetupDraft Draft => new(Device, new("admin@router.local", "Fictional-Admin-42!"), "Offline Test");
    const string Wifi = "Fictional-Wifi-42!";
    static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is RouterException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Invalid offline setup operation was accepted.");
    }
    public static async Task RunAsync()
    {
        var wire = new FakeWire(); var session = new BleSetupSession(wire, Draft, Wifi);
        await session.WriteCredentialsAsync(default); await session.WriteWifiAsync(default); await session.CompleteAsync(default);
        var expected = new (int Id, string Value)[]
        {
            (0xab1c, "SET:1"), (0xab20, ""), (0xab1f, ""), (0xab1e, "al"), (0xab1d, "admin@router.loc"),
            (0xab1c, "SET:2"), (0xab20, ""), (0xab1f, ""), (0xab1e, "42!"), (0xab1d, "Fictional-Admin-"),
            (0xab04, ""), (0xab03, ""), (0xab02, ""), (0xab01, "Offline Test"),
            (0xab08, ""), (0xab07, ""), (0xab06, "2!"), (0xab05, "Fictional-Wifi-4"),
            (0xaa1a, "SETOBCOMPLETE")
        };
        Assert(wire.Writes.Count == expected.Length, "Unexpected provisioning write count.");
        for (var i = 0; i < expected.Length; i++)
            Assert(wire.Writes[i] == (BleSetupProtocol.Uuid(expected[i].Id), expected[i].Value), $"Wrong setup bytes/order at write {i}.");
        Assert(wire.Reads.SequenceEqual(Enumerable.Repeat(new[] { BleSetupProtocol.Serial, BleSetupProtocol.Mac, BleSetupProtocol.State }, 3).SelectMany(x => x)),
            "Setup read something other than the expected identity.");
        await Reject(() => session.CompleteAsync(default));
        Assert(wire.Writes.Count == expected.Length, "Setup-complete was repeated.");

        // Every possible lost/failed acknowledgement must stop before the next write.
        for (var failure = 1; failure <= expected.Length; failure++)
        {
            var broken = new FakeWire { FailWrite = failure }; var stopped = new BleSetupSession(broken, Draft, Wifi);
            await Reject(async () =>
            {
                await stopped.WriteCredentialsAsync(default); await stopped.WriteWifiAsync(default); await stopped.CompleteAsync(default);
            });
            Assert(stopped.Faulted && broken.Writes.Count == failure, "Setup continued after an uncertain write.");
            await Reject(() => stopped.WriteCredentialsAsync(default)); await Reject(() => stopped.WriteWifiAsync(default)); await Reject(() => stopped.CompleteAsync(default));
            Assert(broken.Writes.Count == failure, "A failed setup write was retried.");
        }
        var absent = new FakeWire { Missing = BleSetupProtocol.Uuid(0xab08) };
        await Reject(() => new BleSetupSession(absent, Draft, Wifi).WriteCredentialsAsync(default));
        Assert(absent.Writes.Count == 0, "Preflight allowed a partial interface.");
        var impostor = new FakeWire { Serial = "OTHERROUTER123" };
        await Reject(() => new BleSetupSession(impostor, Draft, Wifi).WriteCredentialsAsync(default));
        Assert(impostor.Writes.Count == 0, "Setup ignored a changed serial.");
        var changed = new FakeWire(); var changedSession = new BleSetupSession(changed, Draft, Wifi);
        await changedSession.WriteCredentialsAsync(default); changed.Mac = "AABBCCDDEEFF";
        await Reject(() => changedSession.WriteWifiAsync(default));
        Assert(changed.Writes.Count == 10, "Setup failed to recheck identity between phases.");
        var updating = new FakeWire { State = "Upgrading" };
        await Reject(() => new BleSetupSession(updating, Draft, Wifi).WriteCredentialsAsync(default));
        Assert(updating.Writes.Count == 0, "Setup wrote while the router reported an upgrade.");
        Assert(!BleSetupProtocol.IsUpgrading("Upgrade complete"), "A completed upgrade was treated as active.");
        var updateBetween = new FakeWire(); var updateSession = new BleSetupSession(updateBetween, Draft, Wifi);
        await updateSession.WriteCredentialsAsync(default); updateBetween.State = "UPGRADING";
        await Reject(() => updateSession.WriteWifiAsync(default));
        Assert(updateBetween.Writes.Count == 10, "Upgrade state was not rechecked between phases.");
        var unknownState = new FakeWire { State = "" };
        await Reject(() => new BleSetupSession(unknownState, Draft, Wifi).WriteCredentialsAsync(default));
        Assert(unknownState.Writes.Count == 0, "Setup accepted an empty onboarding state.");
        Assert(BleSetupProtocol.DecodeMac(Encoding.UTF8.GetBytes("00:11:22:33:44:55\0")) == Device.Mac, "Bluetooth MAC text was not normalized.");
        var cancelled = new FakeWire();
        await Reject(() => new BleSetupSession(cancelled, Draft, Wifi).WriteCredentialsAsync(new CancellationToken(true)));
        Assert(cancelled.Writes.Count == 0, "Cancelled setup sent a write.");
        var order = new FakeWire();
        await Reject(() => new BleSetupSession(order, Draft, Wifi).CompleteAsync(default));
        Assert(order.Writes.Count == 0, "Setup-complete bypassed prerequisite phases.");
        foreach (var invalid in new[] { "", new string('x', 33), "Unicode-\u00e9", "newline\nname" })
            await Reject(() => new BleSetupSession(new FakeWire(), Draft with { Ssid = invalid }, Wifi).WriteCredentialsAsync(default));
        foreach (var invalid in new[] { "short", new string('x', 64) })
            await Reject(() => new BleSetupSession(new FakeWire(), Draft, invalid).WriteCredentialsAsync(default));
        var max = BleSetupProtocol.Fragments(0xab1d, new string('x', 64));
        Assert(max.Count == 4 && max.All(x => x.Value.Length == 16), "Maximum fragment boundary changed.");

        var folder = Path.Combine(Path.GetTempPath(), "offline-setup-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = OfflineSetupStore.Save(Draft, folder); var json = File.ReadAllText(path);
            Assert(!json.Contains(Draft.Credential.Password) && !json.Contains(Draft.Credential.UserName) && !json.Contains(Draft.Ssid),
                "Pending setup was stored in plaintext.");
            Assert(OfflineSetupStore.Load(path) == Draft && OfflineSetupStore.Find(folder).Count == 1, "Pending setup was not recoverable.");
            var root = JsonNode.Parse(json)!; var bytes = Convert.FromBase64String(root["Ciphertext"]!.GetValue<string>()); bytes[^1] ^= 1;
            root["Ciphertext"] = Convert.ToBase64String(bytes); File.WriteAllText(path, root.ToJsonString());
            await Reject(() => { _ = OfflineSetupStore.Load(path); return Task.CompletedTask; });
            Assert(OfflineSetupStore.Find(folder).Count == 0, "Tampered pending setup was accepted.");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        Console.WriteLine("PASS: offline BLE setup bytes/order, all 19 failure boundaries, identity checks, cancellation and encrypted pending-login recovery.");
    }
    sealed class FakeWire : IBleSetupWire
    {
        public readonly List<(string Uuid, string Value)> Writes = new();
        public readonly List<string> Reads = new();
        public int FailWrite;
        public string? Missing;
        public string Serial = Device.Serial, Mac = Device.Mac;
        public string State = "Ready";
        public bool CanWrite(string uuid) => uuid != Missing;
        public Task<byte[]> ReadAsync(string uuid, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads.Add(uuid);
            return Task.FromResult(Encoding.UTF8.GetBytes(uuid == BleSetupProtocol.Serial ? Serial :
                uuid == BleSetupProtocol.Mac ? Mac : uuid == BleSetupProtocol.State ? State : throw new InvalidOperationException("Unexpected setup read.")));
        }
        public Task WriteAsync(string uuid, byte[] value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Writes.Add((uuid, Encoding.UTF8.GetString(value)));
            if (Writes.Count == FailWrite) throw new RouterException("Fictional lost acknowledgement.");
            return Task.CompletedTask;
        }
    }
}
