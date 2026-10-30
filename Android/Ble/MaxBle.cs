using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Java.Util;

namespace ArrisControl;

/// <summary>
/// Bluetooth GATT identifiers for SURFboard mAX onboarding, and a minimal async
/// wrapper around Android's GATT API. Reads are always safe; writes are used only
/// by the guided setup flow, one at a time, after explicit user confirmation.
/// See docs/ble-onboarding.md for the protocol notes these come from.
/// </summary>
internal static class MaxBle
{
    public const string ServiceOba = "0000aa00-0000-1000-8000-00805f9b34fb";
    public const string ServiceLca = "0000ab00-0000-1000-8000-00805f9b34fb";

    // Read during inspection / setup.
    public const string Serial       = "0000aa04-0000-1000-8000-00805f9b34fb";
    public const string WanMac       = "0000aa05-0000-1000-8000-00805f9b34fb";
    public const string ModelNumber  = "0000aa06-0000-1000-8000-00805f9b34fb";
    public const string DeviceMode   = "0000aa07-0000-1000-8000-00805f9b34fb";
    public const string WanStatus    = "0000aa11-0000-1000-8000-00805f9b34fb";
    public const string ObaState     = "0000aa08-0000-1000-8000-00805f9b34fb";
    public const string EncryptionKey= "0000ab0a-0000-1000-8000-00805f9b34fb";

    // Written during guided setup (each one individually confirmed by the user).
    public const string FriendlyName = "0000aa14-0000-1000-8000-00805f9b34fb";
    public const string SetType      = "0000ab1c-0000-1000-8000-00805f9b34fb";
    public const string P11Ssid      = "0000ab01-0000-1000-8000-00805f9b34fb";
    public const string P11Passphrase= "0000ab05-0000-1000-8000-00805f9b34fb";
    public const string P11UserPwd   = "0000ab1d-0000-1000-8000-00805f9b34fb";
    public const string ObaComplete  = "0000aa1a-0000-1000-8000-00805f9b34fb";

    public static UUID Uuid(string s) => UUID.FromString(s)!;

    // Characteristics whose contents diagnostics must never read: the write-encryption key,
    // credential slots, router password and Wi-Fi passphrases. Their presence is still reported.
    public static readonly HashSet<string> NeverRead = new(StringComparer.OrdinalIgnoreCase)
    {
        EncryptionKey, "0000aa18-0000-1000-8000-00805f9b34fb",
        "0000ab1d-0000-1000-8000-00805f9b34fb", "0000ab1e-0000-1000-8000-00805f9b34fb", "0000ab1f-0000-1000-8000-00805f9b34fb", "0000ab20-0000-1000-8000-00805f9b34fb",
        "0000ab05-0000-1000-8000-00805f9b34fb", "0000ab06-0000-1000-8000-00805f9b34fb", "0000ab07-0000-1000-8000-00805f9b34fb", "0000ab08-0000-1000-8000-00805f9b34fb",
        "0000ab17-0000-1000-8000-00805f9b34fb", "0000ab18-0000-1000-8000-00805f9b34fb", "0000ab19-0000-1000-8000-00805f9b34fb", "0000ab1a-0000-1000-8000-00805f9b34fb",
    };
    // Identity/state characteristics that are safe to read for diagnostics (serial is redacted in the report).
    public static readonly (string Uuid, string Label)[] SafeReads =
    [
        (ModelNumber, "Model"), (Serial, "Serial"), (DeviceMode, "Device mode"), (ObaState, "Setup state"), (WanStatus, "WAN status"),
        ("0000aa09-0000-1000-8000-00805f9b34fb", "Firmware 1"), ("0000aa0a-0000-1000-8000-00805f9b34fb", "Firmware 2"),
        ("0000aa0b-0000-1000-8000-00805f9b34fb", "Firmware 3"), ("0000aa0c-0000-1000-8000-00805f9b34fb", "Firmware 4"),
        ("0000aa13-0000-1000-8000-00805f9b34fb", "Upgrade status"),
    ];
    public static string Short(string uuid) =>
        uuid.Length == 36 && uuid.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) ? uuid[4..8].ToLowerInvariant() : uuid.ToLowerInvariant();

    public static bool Supported(Context context)
    {
        if (!context.PackageManager!.HasSystemFeature(PackageManager.FeatureBluetoothLe)) return false;
        var manager = (BluetoothManager?)context.GetSystemService(Context.BluetoothService);
        return manager?.Adapter is { IsEnabled: true };
    }
}

/// <summary>A single discovered router advertisement.</summary>
internal sealed record BleDevice(string Name, string Address, int Rssi, BluetoothDevice Device, string[]? ServiceUuids = null, int[]? ManufacturerIds = null)
{
    public override string ToString() => $"{(string.IsNullOrEmpty(Name) ? "(unnamed)" : Name)}\n{Address} · {Rssi} dBm";
}

/// <summary>
/// Minimal, strictly-serial GATT client: one scan, one connection, one in-flight
/// read/write at a time. Every operation has a timeout and never retries on its own.
/// </summary>
internal sealed class BleManager : Java.Lang.Object
{
    readonly Context context;
    readonly BluetoothAdapter adapter;
    BluetoothGatt? gatt;
    GattBridge? bridge;

    public BleManager(Context context)
    {
        this.context = context;
        var manager = (BluetoothManager?)context.GetSystemService(Context.BluetoothService)
            ?? throw new RouterException("Bluetooth is not available on this phone.");
        adapter = manager.Adapter ?? throw new RouterException("Bluetooth is not available on this phone.");
        if (!adapter.IsEnabled) throw new RouterException("Turn on Bluetooth, then try again.");
    }

    /// <summary>Scans for the given duration and returns the devices seen, strongest signal first.</summary>
    public async Task<IReadOnlyList<BleDevice>> ScanAsync(TimeSpan duration, CancellationToken token)
    {
        var scanner = adapter.BluetoothLeScanner ?? throw new RouterException("Bluetooth scanning is unavailable.");
        var found = new Dictionary<string, BleDevice>(StringComparer.OrdinalIgnoreCase);
        var callback = new ScanBridge((result) =>
        {
            var device = result.Device;
            if (device?.Address is null) return;
            var name = result.ScanRecord?.DeviceName ?? device.Name ?? "";
            var services = result.ScanRecord?.ServiceUuids?.Select(u => u.Uuid?.ToString() ?? "").Where(u => u.Length > 0).ToArray() ?? [];
            var makers = new List<int>();
            if (result.ScanRecord?.ManufacturerSpecificData is { } data)
                for (var i = 0; i < data.Size(); i++) makers.Add(data.KeyAt(i));
            lock (found) found[device.Address] = new BleDevice(name, device.Address, result.Rssi, device, services, makers.ToArray());
        });
        var settings = new ScanSettings.Builder()!.SetScanMode(Android.Bluetooth.LE.ScanMode.LowLatency)!.Build();
        scanner.StartScan(null, settings, callback);
        try { await Task.Delay(duration, token); }
        finally { scanner.StopScan(callback); }
        lock (found) return found.Values.OrderByDescending(d => d.Rssi).ToList();
    }

    public async Task ConnectAsync(BluetoothDevice device, CancellationToken token)
    {
        bridge = new GattBridge();
        gatt = device.ConnectGatt(context, false, bridge, BluetoothTransports.Le)
            ?? throw new RouterException("Could not open a Bluetooth connection to the router.");
        await bridge.Connected.WaitAsync(TimeSpan.FromSeconds(20), token);
        if (!gatt.DiscoverServices()) throw new RouterException("Could not read the router's Bluetooth services.");
        await bridge.ServicesDiscovered.WaitAsync(TimeSpan.FromSeconds(20), token);
    }

    BluetoothGattCharacteristic Find(string uuid)
    {
        foreach (var service in gatt?.Services ?? new List<BluetoothGattService>())
        {
            var ch = service.GetCharacteristic(MaxBle.Uuid(uuid));
            if (ch is not null) return ch;
        }
        throw new RouterException($"This router does not expose characteristic {uuid[4..8]} over Bluetooth.");
    }

    /// <summary>Reads a characteristic and returns its raw bytes (empty if the router returned none).</summary>
    /// <summary>Every service and characteristic the connected device exposes, with its allowed operations.</summary>
    public IReadOnlyList<(string Service, string Characteristic, string Properties)> Map()
    {
        var map = new List<(string, string, string)>();
        foreach (var service in gatt?.Services ?? new List<BluetoothGattService>())
            foreach (var ch in service.Characteristics ?? new List<BluetoothGattCharacteristic>())
                map.Add((service.Uuid?.ToString() ?? "", ch.Uuid?.ToString() ?? "", ch.Properties.ToString()));
        return map;
    }
    public bool Has(string uuid) => Map().Any(m => string.Equals(m.Characteristic, uuid, StringComparison.OrdinalIgnoreCase));

    public async Task<byte[]> ReadAsync(string uuid, CancellationToken token)
    {
        if (gatt is null || bridge is null) throw new RouterException("Not connected over Bluetooth.");
        if (MaxBle.NeverRead.Contains(uuid) && uuid != MaxBle.EncryptionKey) throw new RouterException("This characteristic holds a secret and is never read.");
        var ch = Find(uuid);
        bridge.BeginRead();
        if (!gatt.ReadCharacteristic(ch)) throw new RouterException("The Bluetooth read request was rejected.");
        return await bridge.Read.WaitAsync(TimeSpan.FromSeconds(15), token) ?? [];
    }

    /// <summary>Writes bytes to a characteristic and waits for the router to acknowledge.</summary>
    public async Task WriteAsync(string uuid, byte[] value, CancellationToken token)
    {
        if (gatt is null || bridge is null) throw new RouterException("Not connected over Bluetooth.");
        var ch = Find(uuid);
        ch.WriteType = GattWriteType.Default;
#pragma warning disable CA1422
        ch.SetValue(value);
        bridge.BeginWrite();
        if (!gatt.WriteCharacteristic(ch)) throw new RouterException("The Bluetooth write request was rejected.");
#pragma warning restore CA1422
        await bridge.Written.WaitAsync(TimeSpan.FromSeconds(15), token);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { gatt?.Disconnect(); gatt?.Close(); } catch (Java.Lang.Exception) { }
            gatt?.Dispose(); bridge?.Dispose(); gatt = null; bridge = null;
        }
        base.Dispose(disposing);
    }

    sealed class ScanBridge(Action<ScanResult> onResult) : ScanCallback
    {
        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        { if (result is not null) onResult(result); }
        public override void OnBatchScanResults(IList<ScanResult>? results)
        { foreach (var r in results ?? new List<ScanResult>()) onResult(r); }
    }

    // Bridges the asynchronous GATT callbacks to awaitable gates. One op in flight at a time.
    sealed class GattBridge : BluetoothGattCallback
    {
        public readonly Gate Connected = new();
        public readonly Gate ServicesDiscovered = new();
        public readonly Gate<byte[]> Read = new();
        public readonly Gate Written = new();
        public void BeginRead() => Read.Reset();
        public void BeginWrite() => Written.Reset();

        public override void OnConnectionStateChange(BluetoothGatt? g, GattStatus status, ProfileState newState)
        {
            if (newState == ProfileState.Connected && status == GattStatus.Success) Connected.Set();
            else if (newState == ProfileState.Disconnected)
            {
                var error = new RouterException("The Bluetooth connection to the router dropped.");
                Connected.Fail(error); ServicesDiscovered.Fail(error); Read.Fail(error); Written.Fail(error);
            }
        }
        public override void OnServicesDiscovered(BluetoothGatt? g, GattStatus status)
        {
            if (status == GattStatus.Success) ServicesDiscovered.Set();
            else ServicesDiscovered.Fail(new RouterException("Could not read the router's Bluetooth services."));
        }
#pragma warning disable CA1422
        public override void OnCharacteristicRead(BluetoothGatt? g, BluetoothGattCharacteristic? ch, GattStatus status)
        {
            if (status == GattStatus.Success) Read.Set(ch?.GetValue() ?? []);
            else Read.Fail(new RouterException("The router rejected the Bluetooth read."));
        }
#pragma warning restore CA1422
        public override void OnCharacteristicWrite(BluetoothGatt? g, BluetoothGattCharacteristic? ch, GattStatus status)
        {
            if (status == GattStatus.Success) Written.Set();
            else Written.Fail(new RouterException("The router rejected the Bluetooth write."));
        }
    }

    // A one-shot awaitable gate that can be reset for a new operation.
    internal sealed class Gate
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() => tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Set() => tcs.TrySetResult();
        public void Fail(Exception ex) => tcs.TrySetException(ex);
        public async Task WaitAsync(TimeSpan timeout, CancellationToken token)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            using (cts.Token.Register(() => tcs.TrySetException(new RouterException("The Bluetooth operation timed out."))))
                await tcs.Task;
        }
    }
    internal sealed class Gate<T>
    {
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() => tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Set(T value) => tcs.TrySetResult(value);
        public void Fail(Exception ex) => tcs.TrySetException(ex);
        public async Task<T> WaitAsync(TimeSpan timeout, CancellationToken token)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            using (cts.Token.Register(() => tcs.TrySetException(new RouterException("The Bluetooth operation timed out."))))
                return await tcs.Task;
        }
    }
}
