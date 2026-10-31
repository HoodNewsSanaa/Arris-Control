using Android.Widget;

namespace ArrisControl;

public sealed partial class MainActivity
{
    void AddManualBleCommands(BleDevice device, string appName, string mode, string serial, string mac, string state, string upgrade)
    {
        var card = Card("Manual commands · experimental",
            "Record the current state first. These send individual commands observed in the official app, even when identity is incomplete or onboarding says UPGRADING. They do not bypass firmware verification or prove that setup finished. Guided setup keeps its identity and upgrade checks.");
        var expected = new Dictionary<string, string>
        {
            [MaxBle.AppName] = appName, [MaxBle.DeviceMode] = mode,
            [MaxBle.Serial] = serial, [MaxBle.WanMac] = mac
        };
        Action(card, "Set role…", async () =>
        {
            var choice = await Choose("Set the selected unit's role", ["MAINAP · primary router", "EXTENDER · satellite role only"]);
            if (choice < 0) return;
            await SendManualBleCommand(device, choice == 0 ? "MAINAP" : "EXTENDER", expected, state, upgrade);
        });
        Action(card, "Send SETOBCOMPLETE…", () =>
            SendManualBleCommand(device, BleSetupProtocol.CompleteValue, expected, state, upgrade));
    }
    async Task SendManualBleCommand(BleDevice device, string value, IReadOnlyDictionary<string, string> expected, string state, string upgrade)
    {
        if (!await Confirm("Send " + value + " once?",
            "Selected Bluetooth unit: " + (string.IsNullOrEmpty(device.Name) ? "(unnamed)" : device.Name) + "\n" + device.Address +
            "\nLast onboarding state: " + (state.Length == 0 ? "(unavailable)" : state) +
            "\nLast upgrade status: " + (upgrade.Length == 0 ? "(unavailable)" : upgrade) +
            "\n\nThis may change the unit's role, onboarding or connectivity. If a real update is running, changing onboarding may disrupt it. Use a spare unit. EXTENDER does not perform mesh pairing. SETOBCOMPLETE does not supply credentials, install firmware or guarantee local login.\n\nThe app reconnects, rechecks known identity/role values, sends one acknowledged write and disconnects. No automatic retry.", "Send once")) return;
        ble?.Dispose(); ble = new BleManager(this);
        try
        {
            status.Text = "Reconnecting to verify the selected unit…";
            await ble.ConnectAsync(device.Device, lifetime.Token);
            var target = value == BleSetupProtocol.CompleteValue ? MaxBle.ObaComplete : MaxBle.DeviceMode;
            var map = ble.Map();
            foreach (var uuid in expected.Where(p => p.Value.Length > 0).Select(p => p.Key).Append(MaxBle.AppName).Append(target).Distinct())
            {
                var matches = map.Where(m => m.Characteristic.Equals(uuid, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 || !matches[0].Service.Equals(MaxBle.ServiceOba, StringComparison.OrdinalIgnoreCase))
                    throw new RouterException("The expected onboarding interface is missing or ambiguous. Nothing was written.");
            }
            var result = await new BleManualCommand(ble, expected).SendAsync(value, lifetime.Token);
            Shell("Manual command acknowledged"); backAction = BackToStart;
            var card = Card(value + " · acknowledged",
                "The router acknowledged one write. This does not establish that onboarding or an update requirement completed.");
            card.AddView(Label(result.ReadBackAvailable
                ? "Read-back " + (target == MaxBle.DeviceMode ? "role" : "onboarding state") + ": " + System.Text.Json.JsonSerializer.Serialize(result.ReadBack)
                : "Read-back was unavailable. Inspect the unit again.", 14));
            Action(card, "Inspect the unit again", async () =>
            {
                ble?.Dispose(); ble = new BleManager(this); await BleConnectAndInspect(device);
            }, Tone.Primary);
            Action(card, "Back to connections", BackToStart);
        }
        catch
        {
            // A new attempt must begin with inspection, including after a
            // preflight failure. Never leave a stale one-tap command behind.
            ShowConnect(); throw;
        }
        finally { ble?.Dispose(); ble = null; }
    }
}
