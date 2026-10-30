using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using Path = System.IO.Path;

namespace ArrisControl;

public sealed partial class MainActivity
{
    CancellationTokenSource? bleCaptureStop;
    string BleCaptureFolder => Path.Combine(FilesDir!.AbsolutePath, "ble-captures");
    string LegacyBleCapturePath => Path.Combine(FilesDir!.AbsolutePath, "last-ble-capture.txt");
    string[] SavedBleCaptures() =>
        (Directory.Exists(BleCaptureFolder) ? Directory.GetFiles(BleCaptureFolder, "*.txt") : Array.Empty<string>())
        .OrderByDescending(p => p, StringComparer.Ordinal)
        .Concat(File.Exists(LegacyBleCapturePath) ? new[] { LegacyBleCapturePath } : []).ToArray();
    static void SaveBleCapture(string path, string report)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, report);
        File.Move(temporary, path, true);
    }
    void AddSavedBleCapture(LinearLayout parent)
    {
        if (SavedBleCaptures().Length > 0)
            Action(parent, "Open saved Bluetooth captures", async () =>
            {
                var paths = SavedBleCaptures();
                var choice = await Choose("Saved captures · newest first (UTC)",
                    paths.Select(p => Path.GetFileNameWithoutExtension(p).Replace('_', ' ')).ToArray());
                if (choice < 0) return;
                ble?.Dispose(); ble = null;
                ShowReport("Bluetooth state capture", "Saved privately on this phone. Previous captures are retained for comparison. Serial and MAC bytes are withheld.", File.ReadAllText(paths[choice]));
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
        Directory.CreateDirectory(BleCaptureFolder);
        var capturePath = Path.Combine(BleCaptureFolder, DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N")[..6] + ".txt");
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
                SaveBleCapture(capturePath, report);
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
