using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using Path = System.IO.Path;

namespace ArrisControl;

public sealed partial class MainActivity
{
    CancellationTokenSource? bleCaptureStop;
    string BleCapturePath => Path.Combine(FilesDir!.AbsolutePath, "last-ble-capture.txt");
    void SaveBleCapture(string report)
    {
        var temporary = BleCapturePath + ".tmp";
        File.WriteAllText(temporary, report);
        File.Move(temporary, BleCapturePath, true);
    }
    void AddSavedBleCapture(LinearLayout parent)
    {
        if (File.Exists(BleCapturePath))
            Action(parent, "Open saved Bluetooth capture", () =>
            {
                ble?.Dispose(); ble = null;
                ShowReport("Bluetooth state capture", "The last capture is saved privately on this phone. Serial and MAC bytes are withheld.", File.ReadAllText(BleCapturePath));
                return Task.CompletedTask;
            });
    }
    async Task CaptureBleState(BleDevice device)
    {
        ble?.Dispose(); ble = new BleManager(this);
        status.Text = "Connecting for a read-only recording…";
        await ble.ConnectAsync(device.Device, lifetime.Token);
        var wire = ble;
        var map = wire.Map().Select(m => new BleCaptureCharacteristic(m.Service, m.Characteristic, m.Properties,
            m.Properties.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Contains("Read"))).ToArray();
        var version = PackageManager!.GetPackageInfo(PackageName!, 0)?.VersionName ?? "?";
        var recorder = new BleCapture(map, wire.ReadAsync, $"Android {Build.VERSION.Release}; app {version}; build {AndroidUpdater.InstalledVersion(this)}", device.Address);
        Shell("Record router state"); backAction = BackToStart;
        var card = Card("Read-only · up to 3 minutes",
            "Keep this screen open and the phone near the router. Nothing is written to the router. State, upgrade status, download progress, WAN status and role are sampled sequentially every 5 seconds after each completed sample. A rejected field is recorded; an incomplete operation stops recording. A partial report is saved privately as it runs.");
        var latest = Label("Starting…", 14); card.AddView(latest);
        bleCaptureStop?.Dispose();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        bleCaptureStop = stop;
        // This button deliberately stays outside the busy-action list so Stop
        // remains available while the serial GATT read task is in flight.
        var cancel = new Button(this) { Text = "Stop and save capture", TextSize = 16 };
        cancel.SetTextColor(Palette.Text); cancel.Background = Surface(); cancel.SetMinHeight(Dp(64));
        cancel.Click += (_, _) => { cancel.Enabled = false; stop.Cancel(); };
        card.AddView(cancel);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        try
        {
            await recorder.RunAsync(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(5), stop.Token, report =>
            {
                SaveBleCapture(report);
                if (!IsDestroyed)
                {
                    status.Text = "Recording… use Stop and save capture when ready.";
                    latest.Text = string.Join("\n", report.Split('\n').Where(s => s.Contains(" · ")).TakeLast(5));
                }
            });
        }
        finally
        {
            wire.Dispose(); if (ReferenceEquals(ble, wire)) ble = null;
            bleCaptureStop = null; Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
        }
        if (!IsDestroyed)
            ShowReport("Bluetooth state capture", "Saved privately on this phone. Identity values are withheld. Exact status bytes and timestamps help compare this with an external network capture.", recorder.Report);
    }
    protected override void OnStop()
    {
        // No background service: stop and preserve the report when leaving the
        // app rather than quietly retaining the router's Bluetooth connection.
        bleCaptureStop?.Cancel();
        base.OnStop();
    }
}
