using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Java.Util;

namespace ArrisControl;

/// <summary>
/// Bluetooth GATT identifiers for SURFboard mAX onboarding, and a minimal async
/// wrapper around Android's GATT API. Secret reads are refused; writes are used
/// only by the confirmed offline setup flow, one acknowledged fragment at a time.
/// See docs/offline-first-setup.md for the inspected protocol and limitations.
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

    // Provisioning slots hold fragments of one value, not individual radios.
    public const string FriendlyName = "0000aa14-0000-1000-8000-00805f9b34fb";
    public const string SetType      = "0000ab1c-0000-1000-8000-00805f9b34fb";
    public const string P11Ssid      = "0000ab01-0000-1000-8000-00805f9b34fb";
    public const string P11Passphrase= "0000ab05-0000-1000-8000-00805f9b34fb";
    public const string P11UserPwd   = "0000ab1d-0000-1000-8000-00805f9b34fb";
    public const string ObaComplete  = "0000aa1a-0000-1000-8000-00805f9b34fb";

    public static UUID Uuid(string s) => UUID.FromString(s)!;

    // Characteristics whose contents diagnostics must never read: the vendor SSO key,
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
internal sealed class BleManager : Java.Lang.Object, IBleSetupWire
{
    readonly Context context;
    readonly BluetoothAdapter adapter;
    BluetoothGatt? gatt;
    GattBridge? bridge;
    bool operationFailed;

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

    /// <summary>Connects and maps services. Clears Android's cached service map first (as the vendor
    /// app does) and retries the connection once on a connection-level failure. Writes are never retried.</summary>
    public async Task ConnectAsync(BluetoothDevice device, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await ConnectOnceAsync(device, token); return; }
            catch (RouterException) when (attempt == 1 && !token.IsCancellationRequested)
            {
                Close();
                await Task.Delay(TimeSpan.FromSeconds(1.5), token);
            }
        }
    }
    async Task ConnectOnceAsync(BluetoothDevice device, CancellationToken token)
    {
        BleLog.Add("connect: selected device (transport LE)");
        bridge = new GattBridge();
        operationFailed = false;
        gatt = device.ConnectGatt(context, false, bridge, BluetoothTransports.Le)
            ?? throw new RouterException("Could not open a Bluetooth connection to the router.");
        await bridge.Connected.WaitAsync(TimeSpan.FromSeconds(20), token);
        BleLog.Add("connected; refreshing cached service map");
        RefreshCache(gatt);
        await Task.Delay(TimeSpan.FromMilliseconds(600), token);
        if (!gatt.DiscoverServices()) throw new RouterException("Could not read the router's Bluetooth services.");
        await bridge.ServicesDiscovered.WaitAsync(TimeSpan.FromSeconds(20), token);
        BleLog.Add($"services discovered: {Map().Count} characteristics");
    }
    // Android caches each device's service map; after a router reset or state change the stale copy
    // causes GATT_ERROR (133). BluetoothGatt.refresh() is hidden, so it is called by reflection, best effort.
    static void RefreshCache(BluetoothGatt gatt)
    {
        try { using var method = gatt.Class.GetMethod("refresh"); method?.Invoke(gatt); }
        catch (Java.Lang.Exception) { }
        catch (InvalidOperationException) { }
    }
    void Close()
    {
        try { gatt?.Disconnect(); gatt?.Close(); } catch (Java.Lang.Exception) { }
        gatt?.Dispose(); bridge?.Dispose(); gatt = null; bridge = null;
    }
    /// <summary>Human-readable meaning for common GATT status codes.</summary>
    public static string Explain(int code) => code switch
    {
        0 => "success",
        2 => "reading not permitted",
        3 => "writing not permitted",
        5 => "needs a paired (authenticated) link",
        6 => "request not supported",
        7 => "invalid offset",
        8 => "insufficient authorization",
        13 => "value has the wrong length",
        15 => "needs an encrypted link",
        133 => "GATT_ERROR: a connection-level failure (often a stale Bluetooth cache, the link dropping, or the router busy). Not a refusal of the value",
        257 => "generic failure",
        _ => "unrecognized code"
    };

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
        if (operationFailed) throw new RouterException("Reconnect Bluetooth after the previous incomplete operation, then refresh identity.");
        if (MaxBle.NeverRead.Contains(uuid)) throw new RouterException("This characteristic holds a secret and is never read.");
        var ch = Find(uuid);
        bridge.BeginRead(uuid);
        BleLog.Add($"read  {MaxBle.Short(uuid)} …");
        try
        {
            if (!gatt.ReadCharacteristic(ch)) throw new RouterException("Android rejected the Bluetooth read request. Reconnect and refresh identity.");
            var result = await bridge.Read.WaitAsync(TimeSpan.FromSeconds(15), token) ?? [];
            BleLog.Add($"read  {MaxBle.Short(uuid)} ok: {BleLog.Describe(uuid, result)}");
            return result;
        }
        catch { operationFailed = true; throw; }
    }

    /// <summary>Setup requires acknowledged writes, not write-without-response.</summary>
    public bool CanWrite(string uuid) => !operationFailed && Has(uuid) && (Find(uuid).Properties & GattProperty.Write) != 0;

    /// <summary>Writes an allowlisted fragment and waits for acknowledgement.</summary>
    public async Task WriteAsync(string uuid, byte[] value, CancellationToken token)
    {
        if (!BleSetupProtocol.WriteUuids.Contains(uuid)) throw new RouterException("This Bluetooth write is outside the offline setup protocol.");
        if (gatt is null || bridge is null) throw new RouterException("Not connected over Bluetooth.");
        if (operationFailed) throw new RouterException("Reconnect Bluetooth after the previous incomplete operation.");
        var ch = Find(uuid);
        ch.WriteType = GattWriteType.Default;
#pragma warning disable CA1422
        ch.SetValue(value);
        BleLog.Add($"write {MaxBle.Short(uuid)} ({ch.Properties}): {BleLog.Describe(uuid, value)}");
        bridge.BeginWrite(uuid);
        if (!gatt.WriteCharacteristic(ch)) throw new RouterException($"Android refused to send the write to {MaxBle.Short(uuid)} (characteristic allows: {ch.Properties}).");
#pragma warning restore CA1422
        try { await bridge.Written.WaitAsync(TimeSpan.FromSeconds(15), token); }
        catch { operationFailed = true; throw; }
        BleLog.Add($"write {MaxBle.Short(uuid)} acknowledged");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }

    // Explicit CI intent only: exercise Android's actual callback objects without
    // requiring a physical Bluetooth adapter or sending any radio operation.
    internal static async Task RunCallbackSelfTestsAsync(CancellationToken token)
    {
        using var callback = new GattBridge();
        using var serial = new BluetoothGattCharacteristic(MaxBle.Uuid(MaxBle.Serial), GattProperty.Read, GattPermission.Read);
        using var mac = new BluetoothGattCharacteristic(MaxBle.Uuid(MaxBle.WanMac), GattProperty.Read, GattPermission.Read);
#pragma warning disable CA1422
        serial.SetValue("stale-cache");
#pragma warning restore CA1422
        callback.BeginRead(MaxBle.Serial);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            callback.OnCharacteristicRead(null, mac, "wrong-field"u8.ToArray(), GattStatus.Success);
            if (callback.Read.IsCompleted) throw new InvalidOperationException("An unrelated BLE read completed the identity request.");
            callback.OnCharacteristicRead(null, serial, "TESTSERIAL123"u8.ToArray(), GattStatus.Success);
            var value = await callback.Read.WaitAsync(TimeSpan.FromSeconds(1), token);
            if (!value.SequenceEqual("TESTSERIAL123"u8.ToArray())) throw new InvalidOperationException("BLE read used the stale characteristic cache instead of the callback value.");
        }
        callback.BeginRead(MaxBle.Serial);
#pragma warning disable CA1422
        serial.SetValue("TESTSERIAL123");
        callback.OnCharacteristicRead(null, serial, GattStatus.Success);
#pragma warning restore CA1422
        var legacy = await callback.Read.WaitAsync(TimeSpan.FromSeconds(1), token);
        if (!legacy.SequenceEqual("TESTSERIAL123"u8.ToArray())) throw new InvalidOperationException("Legacy BLE read callback lost its value.");
        callback.BeginWrite(MaxBle.P11Ssid);
        callback.OnCharacteristicWrite(null, mac, GattStatus.Success);
        if (callback.Written.IsCompleted) throw new InvalidOperationException("An unrelated BLE write acknowledgement was accepted.");
        using var slot = new BluetoothGattCharacteristic(MaxBle.Uuid(MaxBle.P11Ssid), GattProperty.Write, GattPermission.Write);
        callback.OnCharacteristicWrite(null, slot, GattStatus.Success);
        await callback.Written.WaitAsync(TimeSpan.FromSeconds(1), token);
        Console.WriteLine("PASS: modern/legacy Android BLE callback values and rejection of unrelated read/write callbacks.");
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
        string? expectedRead, expectedWrite;
        public void BeginRead(string uuid) { expectedRead = uuid; Read.Reset(); }
        public void BeginWrite(string uuid) { expectedWrite = uuid; Written.Reset(); }

        public override void OnConnectionStateChange(BluetoothGatt? g, GattStatus status, ProfileState newState)
        {
            if (newState == ProfileState.Connected && status == GattStatus.Success) Connected.Set();
            else if (newState == ProfileState.Disconnected)
            {
                BleLog.Add($"disconnected (code {(int)status}: {Explain((int)status)})");
                var error = new RouterException($"The Bluetooth connection to the router dropped (code {(int)status}: {Explain((int)status)}).");
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
            => CompleteRead(ch, ch?.GetValue() ?? [], status);
#pragma warning restore CA1422
        // Android 13+ supplies a snapshot of the value in this callback. Read
        // that snapshot directly instead of depending on the mutable cache.
        public override void OnCharacteristicRead(BluetoothGatt? g, BluetoothGattCharacteristic? ch, byte[]? value, GattStatus status)
            => CompleteRead(ch, value ?? [], status);
        void CompleteRead(BluetoothGattCharacteristic? ch, byte[] value, GattStatus status)
        {
            var uuid = ch?.Uuid?.ToString();
            if (expectedRead is null || !string.Equals(uuid, expectedRead, StringComparison.OrdinalIgnoreCase))
            { BleLog.Add("ignored unrelated/late read callback: " + MaxBle.Short(uuid ?? "")); return; }
            expectedRead = null;
            if (status == GattStatus.Success) Read.Set(value.ToArray());
            else { BleLog.Add($"read {MaxBle.Short(uuid!)} FAILED code {(int)status} ({Explain((int)status)})"); Read.Fail(new RouterException($"The read of {MaxBle.Short(uuid!)} failed: code {(int)status} ({Explain((int)status)}).")); }
        }
        public override void OnCharacteristicWrite(BluetoothGatt? g, BluetoothGattCharacteristic? ch, GattStatus status)
        {
            var uuid = ch?.Uuid?.ToString();
            if (expectedWrite is null || !string.Equals(uuid, expectedWrite, StringComparison.OrdinalIgnoreCase))
            { BleLog.Add("ignored unrelated/late write callback: " + MaxBle.Short(uuid ?? "")); return; }
            expectedWrite = null;
            if (status == GattStatus.Success) Written.Set();
            else { BleLog.Add($"write {MaxBle.Short(ch?.Uuid?.ToString() ?? "")} FAILED code {(int)status} ({Explain((int)status)})"); Written.Fail(new RouterException($"The write to {MaxBle.Short(ch?.Uuid?.ToString() ?? "")} failed: code {(int)status} ({Explain((int)status)}).")); }
        }
    }

    // A one-shot awaitable gate that can be reset for a new operation.
    internal sealed class Gate
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsCompleted => tcs.Task.IsCompleted;
        public void Reset() => tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Set() => tcs.TrySetResult();
        public void Fail(Exception ex) => tcs.TrySetException(ex);
        public async Task WaitAsync(TimeSpan timeout, CancellationToken token)
        {
            var pending = tcs;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            using (cts.Token.Register(() => pending.TrySetException(new RouterException("The Bluetooth operation timed out."))))
                await pending.Task;
        }
    }
    internal sealed class Gate<T>
    {
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsCompleted => tcs.Task.IsCompleted;
        public void Reset() => tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Set(T value) => tcs.TrySetResult(value);
        public void Fail(Exception ex) => tcs.TrySetException(ex);
        public async Task<T> WaitAsync(TimeSpan timeout, CancellationToken token)
        {
            var pending = tcs;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            using (cts.Token.Register(() => pending.TrySetException(new RouterException("The Bluetooth operation timed out."))))
                return await pending.Task;
        }
    }
}
