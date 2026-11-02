using Android.Widget;

namespace ArrisControl;

public sealed partial class MainActivity
{
    void AddWobaCompletion(RouterApiEndpoint endpoint, WobaInspection inspection)
    {
        var card = Card("Finish onboarding over Bluetooth",
            "After your local admin login is saved and verified, send the setup-complete command to this router over Bluetooth. Keep your phone nearby. Wi-Fi may briefly disconnect. This experimental step has not been verified on physical hardware.");
        string? blocked = null;
        try { WobaSession.CheckWritable(inspection); }
        catch (RouterException ex) { blocked = ex.Message; }
        if (blocked is not null)
        {
            card.AddView(Label("Completion unavailable: " + blocked, 14));
            return;
        }
        Action(card, "Finish onboarding…", () => StartWobaCompletion(endpoint, inspection), Tone.Primary);
        card.AddView(Label("The app checks the saved normal login, matches the Bluetooth unit automatically, and sends SETOBCOMPLETE once after confirmation. A Bluetooth acknowledgement alone does not establish completion.", 13, color: Palette.Muted));
    }

    async Task<CredentialRecord> VerifyWobaNormalAdmin(WobaInspection inspection, CancellationToken token)
        => (await CheckWobaNormalAdmin(inspection, token)).Record;

    async Task<(CredentialRecord Record, string Onboarding)> CheckWobaNormalAdmin(WobaInspection inspection, CancellationToken token)
    {
        var record = CredentialStore.Find().FirstOrDefault(r =>
            r.Serial == inspection.Device.Serial && r.Mac == inspection.Device.Mac)
            ?? throw new RouterException("Save and verify a normal local admin login for this router before finishing onboarding. Use the WOBA admin setup form or verify an existing local login first.");
        var credential = CredentialStore.Load(record);
        if (credential.CertificatePin != inspection.Pin)
            throw new RouterException("The saved login belongs to a different certificate key. Verify this router's local login first. Nothing was changed.");
        status.Text = "Checking that the saved normal admin login works…";
        using var normal = new RouterClient(record with { Address = inspection.Address });
        await normal.ConnectAsync(token);
        return (normal.Record, normal.OnboardingStatus);
    }

    async Task StartWobaCompletion(RouterApiEndpoint endpoint, WobaInspection inspection)
    {
        WobaSession.CheckWritable(inspection);
        await VerifyWobaNormalAdmin(inspection, lifetime.Token);
        if (!await EnsureBlePermissions()) throw new RouterException("Bluetooth permission is needed to finish onboarding.");
        if (!MaxBle.Supported(this)) throw new RouterException("Turn on Bluetooth to finish onboarding, then try again.");
        ble?.Dispose(); ble = new BleManager(this);
        try
        {
            status.Text = "Finding the router over Bluetooth…";
            var devices = await ble.ScanAsync(TimeSpan.FromSeconds(7), lifetime.Token);
            if (devices.Count == 0)
            {
                await Message("Router not found over Bluetooth", "No Bluetooth devices answered. Stay near the router and check Bluetooth is enabled. WOBA access alone does not guarantee its Bluetooth onboarding service is running. Nothing was changed.");
                return;
            }
            var choice = await Choose("Select the nearby router · identity will be checked", devices.Select(d => d.ToString()).ToArray());
            if (choice < 0) return;
            var device = devices[choice];
            status.Text = "Connecting to the selected Bluetooth unit…";
            await ble.ConnectAsync(device.Device, lifetime.Token);
            var required = new[] { MaxBle.AppName, MaxBle.DeviceMode, MaxBle.Serial, MaxBle.WanMac, MaxBle.ObaComplete };
            var map = ble.Map();
            foreach (var uuid in required)
            {
                var matches = map.Where(m => m.Characteristic.Equals(uuid, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 || !matches[0].Service.Equals(MaxBle.ServiceOba, StringComparison.OrdinalIgnoreCase))
                    throw new RouterException("The selected Bluetooth unit does not expose the expected onboarding interface. Nothing was changed.");
            }
            // Confirm the physical target before presenting the final action;
            // WobaBleCompletion checks these fields again immediately before writing.
            var serial = await BleReadText(MaxBle.Serial);
            var mac = BleSetupProtocol.NormalizeMac(await BleReadText(MaxBle.WanMac));
            if (serial.Trim().ToUpperInvariant() != inspection.Device.Serial || mac != inspection.Device.Mac)
                throw new RouterException("This Bluetooth unit does not match the router inspected through WOBA. No completion command was sent.");
            if (!await Confirm("Finish onboarding on this router?",
                "Router: " + inspection.Device + "\nLocal address: " + inspection.Address +
                "\n\nThe saved normal admin login was verified and the Bluetooth identity matches. Send SETOBCOMPLETE once? Completing onboarding should close WOBA access. Wi-Fi or Bluetooth may disconnect. Normal local administration will still need verification afterward. No cloud registration is performed.", "Finish onboarding once")) return;
            BleManualResult? result = null;
            string? error = null;
            try
            {
                var attempt = new WobaBleCompletion(ble, inspection);
                result = await attempt.SendAsync(async token =>
                {
                    using var session = new WobaSession(endpoint);
                    return await session.InspectAsync(token);
                }, async token => { await VerifyWobaNormalAdmin(inspection, token); }, lifetime.Token);
            }
            catch (Exception ex) when (ex is RouterException or System.Net.Http.HttpRequestException or OperationCanceledException or IOException)
            { error = ex.Message; }
            ble.Dispose(); ble = null;
            ShowWobaCompletionResult(endpoint, inspection, result, error);
        }
        finally { ble?.Dispose(); ble = null; }
    }

    void ShowWobaCompletionResult(RouterApiEndpoint endpoint, WobaInspection inspection, BleManualResult? result, string? error)
    {
        Shell("Check onboarding completion"); backAction = BackToStart;
        var card = Card(result is null ? "Completion is unverified" : "Completion command acknowledged",
            error ?? "One Bluetooth completion command was acknowledged. Its effect still needs verification. No command was retried.");
        if (result?.ReadBackAvailable == true)
            card.AddView(Label("Bluetooth setup state: " + result.ReadBack, 14));
        var outcome = Label("Wait for Wi-Fi to return if it disconnected, then check completion. This check sends no settings writes.", 14);
        card.AddView(outcome);
        Action(card, "Check completion and local admin login", async () =>
        {
            var verified = await CheckWobaNormalAdmin(inspection, lifetime.Token);
            bool complete;
            var evidence = "Normal local login · OBASTATUS=" + verified.Onboarding;
            if (verified.Onboarding is "0" or "1") complete = verified.Onboarding == "0";
            else
            {
                using var session = new WobaSession(endpoint with { Address = verified.Record.Address });
                try
                {
                    var fresh = await session.InspectAsync(lifetime.Token);
                    if (fresh.Device != inspection.Device || fresh.Pin != inspection.Pin)
                        throw new RouterException("The returned router identity changed. Completion is unverified.");
                    if (fresh.OnboardingStatus is not ("0" or "1"))
                        throw new RouterException("Normal admin login works, but the router did not report a recognized onboarding state. Completion remains unverified.");
                    lastWobaStatus = fresh;
                    complete = fresh.OnboardingStatus == "0";
                    evidence = "WOBA · OBASTATUS=" + fresh.OnboardingStatus;
                }
                catch (RouterException ex) when (ex.Code == "LoginNotApplicable")
                { complete = true; evidence = "WOBA · LoginNotApplicable"; }
            }
            outcome.Text = complete
                ? "Onboarding is complete and the saved normal admin login works.\n" + evidence
                : "The saved normal admin login works, but the router still reports incomplete onboarding. The completion command was not repeated.\n" + evidence;
            status.Text = complete ? "Onboarding complete · local admin verified." : "Onboarding remains incomplete.";
        }, Tone.Primary);
        Action(card, "Open normal router settings", async () =>
        {
            var record = await VerifyWobaNormalAdmin(inspection, lifetime.Token);
            await Connect(record);
        });
        Action(card, "Back to connections", BackToStart);
    }
}
