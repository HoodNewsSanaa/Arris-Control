using System.Text;

namespace ArrisControl;

internal static class BleCaptureSelfTests
{
    public static async Task RunAsync()
    {
        static void Check(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
        var map = BleCapture.Fields.Select(f => new BleCaptureCharacteristic(BleCapture.Service, f.Uuid, "Read", true)).ToList();
        var calls = new List<string>();
        var secret = BleSetupProtocol.Uuid(0xab0a);
        map.Add(new(BleSetupProtocol.Uuid(0xab00), secret, "Read, Write", true));
        const string serial = "REALPRIVATE12345", mac = "A1B2C3D4E5F6", address = "01:23:45:67:89:AB";
        var recorder = new BleCapture(map, (uuid, _) =>
        {
            calls.Add(uuid);
            if (uuid == BleSetupProtocol.Serial) return Task.FromResult(Encoding.UTF8.GetBytes(serial));
            if (uuid == BleSetupProtocol.Mac) return Task.FromResult(Encoding.UTF8.GetBytes(mac));
            if (uuid == BleSetupProtocol.Uuid(0xaa07)) throw new BleReadRejectedException(115);
            return Task.FromResult("UPGRADING\0\r\n"u8.ToArray());
        }, "Fixture", address);
        await recorder.SnapshotAsync(CancellationToken.None);
        await recorder.SnapshotAsync(CancellationToken.None);
        var report = recorder.Report;
        Check(!report.Contains(serial) && !report.Contains(mac) && !report.Contains(address) &&
            !report.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes(serial))) &&
            !report.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes(mac))), "Capture leaked identity bytes.");
        Check(!calls.Contains(secret) && calls.All(u => BleCapture.Fields.Any(f => f.Uuid == u)), "Capture read outside its allowlist.");
        Check(report.Contains("GATT status=115") && report.Contains("555047524144494E47000D0A") &&
            report.Contains("UPGRADING\\u0000\\r\\n"), "Capture lost exact bytes or rejected-read status.");
        Check(calls.Count(c => c == BleSetupProtocol.Serial) == 1 &&
            calls.Count(c => c == BleSetupProtocol.State) == 2, "Capture did not separate baseline and repeated state reads.");
        var ambiguous = map.Concat([new BleCaptureCharacteristic(BleSetupProtocol.Uuid(0xab00),
            BleSetupProtocol.State, "Read", true)]).ToList();
        calls.Clear();
        var blocked = new BleCapture(ambiguous, (u, _) => { calls.Add(u); return Task.FromResult(Array.Empty<byte>()); }, "", "");
        await blocked.SnapshotAsync(CancellationToken.None);
        Check(!calls.Contains(BleSetupProtocol.State), "An ambiguous characteristic was read.");
        var count = 0;
        var failed = new BleCapture(map, (_, _) => { count++; throw new IOException("private adapter address " + address); }, "", "");
        await failed.SnapshotAsync(CancellationToken.None);
        Check(failed.Interrupted && count == 1 && !failed.Report.Contains(address), "An incomplete read did not stop capture privately.");
        var cancelled = new BleCapture(map, (_, _) => throw new InvalidOperationException("Must not read."), "", "");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await cancelled.RunAsync(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(5), stop.Token);
        Check(cancelled.Report.Contains("stopped by user/app"), "Cancelled capture did not produce a partial report.");
        Console.WriteLine("PASS: read-only BLE capture allowlist, exact bytes, GATT 115 continuation, private identities, ambiguous-map refusal and partial reports.");
    }
}
