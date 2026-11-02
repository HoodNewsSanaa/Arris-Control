namespace ArrisControl;

public sealed partial class MainActivity
{
    void AddWobaCompletion(RouterApiEndpoint endpoint, WobaInspection inspection)
    {
        var card = Card("Check or finish onboarding locally",
            "Use the saved normal admin login to check the completion flag and the router's onboarding process. The phone does not need Bluetooth for this check or the local completion request.");
        Action(card, "Check local completion options", async () =>
        {
            var record = CredentialStore.Find().FirstOrDefault(r => r.Serial == inspection.Device.Serial && r.Mac == inspection.Device.Mac)
                ?? throw new RouterException("Establish and verify the local admin login first, then check completion. This does not require the router to keep advertising over Bluetooth.");
            await ShowLocalOnboarding(record, endpoint.Pin);
        }, Tone.Primary);
    }

    async Task ShowLocalOnboarding(CredentialRecord record, string? expectedPin = null)
    {
        if (expectedPin is not null && CredentialStore.Load(record).CertificatePin != expectedPin)
            throw new RouterException("The saved local login does not match the inspected router's certificate. Nothing was changed.");
        ble?.Dispose(); ble = null;
        client?.Dispose(); client = new RouterClient(record);
        client.Progress = message => RunOnUiThread(() => { if (!IsDestroyed) status.Text = message; });
        Shell("Local onboarding status"); backAction = BackToStart;
        var loading = Card("Checking the saved local login", "Reading the router's setup state through the normal admin API.");
        Action(loading, "Back to connections", BackToStart);
        var state = await client.ReadLocalOnboardingStateAsync(lifetime.Token);
        ShowLocalOnboardingState(state);
    }

    void ShowLocalOnboardingState(LocalOnboardingState state)
    {
        Shell("Local onboarding status"); backAction = BackToStart;
        var source = "Normal login · " + state.Time.ToString("HH:mm:ss");
        var card = Card("Saved admin access works", state.Router.Address + " · " + state.Router.Serial);
        StatusRow(card, "Onboarding", RouterStatus.Onboarding(state.Onboarding, source));
        StatusRow(card, "Router onboarding service", RouterStatus.BluetoothService(state.BluetoothService, source));
        StatusRow(card, "Routing mode", RouterStatus.Routing(state.Mode, source));
        card.AddView(Label("Normal settings and mesh inspection remain available regardless of this onboarding flag. Completing setup does not require a phone Bluetooth connection.", 14));
        var reason = LocalCompletionPlan.Unavailable(state);
        if (reason is not null) card.AddView(Label(reason, 14, color: state.Onboarding == "0" ? Palette.Text : Palette.Warning));
        else
        {
            Action(card, "Review finishing onboarding", async () =>
            {
                var plan = await client!.PrepareLocalCompletionAsync(lifetime.Token);
                if (!await Confirm("Finish onboarding over the local network?", plan.Preview, "Send completion once")) return;
                status.Text = "Sending one local completion request, then checking the normal login…";
                var result = await client.ApplyLocalCompletionAsync(plan, lifetime.Token);
                ShowLocalCompletionResult(result, client.Record);
            }, Tone.Primary);
            card.AddView(Label("Experimental · the command is mapped from the inspected firmware. Success requires a fresh normal login reporting onboarding complete. An acknowledgement alone is not success.", 12, color: Palette.Muted));
        }
        Action(card, "Recheck flags and local login", () => ShowLocalOnboarding(client!.Record));
        Action(card, "Open normal router settings", () => Connect(client!.Record));
        Action(card, "Back to connections", BackToStart);
    }

    void ShowLocalCompletionResult(LocalCompletionResult result, CredentialRecord record)
    {
        Shell("Onboarding completion result"); backAction = BackToStart;
        var card = Card(result.Complete ? "Completion verified" : "Completion not verified", result.Message);
        if (result.State is { } state)
        {
            var source = "Fresh normal login · " + state.Time.ToString("HH:mm:ss");
            StatusRow(card, "Onboarding", RouterStatus.Onboarding(state.Onboarding, source));
            StatusRow(card, "Router onboarding service", RouterStatus.BluetoothService(state.BluetoothService, source));
        }
        Action(card, "Recheck status without sending a command", () => ShowLocalOnboarding(record), Tone.Primary);
        Action(card, "Open normal router settings", () => Connect(record));
        Action(card, "Back to connections", BackToStart);
    }
}
