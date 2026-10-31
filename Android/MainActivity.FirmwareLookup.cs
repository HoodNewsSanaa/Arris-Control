using Android.Views;
using System.Net;
using System.Text.Json.Nodes;

namespace ArrisControl;

public sealed partial class MainActivity
{
    CancellationTokenSource? lookupRecording;
    string LookupPath(string kind) => FirmwareLookupFiles.PathFor(CredentialStore.UserDirectory, client!.Record.Serial, client.Record.Mac, kind);

    void AddFirmwareLookupActions(Android.Widget.LinearLayout card)
    {
        var journal = LookupPath("recovery");
        if (File.Exists(journal))
        {
            card.AddView(Label("A previous lookup recording needs restoration. Restore the original ARRIS source before another recording.", 13));
            Action(card, "Restore ARRIS firmware lookup", async () =>
            {
                var plan = FirmwareLookupFiles.LoadPlan(journal);
                if (!await Confirm("Restore firmware lookup?", "Restore the original ARRIS address saved before recording. No update check or installation command is sent.", "Restore")) return;
                await Task.Run(() => client!.RestoreFirmwareLookupAsync(plan, lifetime.Token), lifetime.Token);
                File.Delete(journal);
                await Message("Lookup restored", "The original ARRIS source was read back successfully.");
                ShowRouter(); await Read();
            }, Tone.Primary);
        }
        else Action(card, "Record firmware lookup", RecordFirmwareLookup, Tone.Primary);

        var captureFile = LookupPath("capture");
        if (File.Exists(captureFile)) Action(card, "Review recorded lookup", async () =>
        {
            var capture = FirmwareLookupFiles.Load(captureFile);
            var summary = FirmwareLookupRecorder.Summary(capture);
            // Only this separately reviewed export reveals identifiers and headers.
            if (!await Confirm("Recorded lookup", summary + "\n\nCopying the full request may reveal serial numbers, MAC addresses or authentication headers. Review it privately before sharing. Nothing is sent to ARRIS automatically.", "Copy private request")) return;
            var clipboard = (Android.Content.ClipboardManager)GetSystemService(ClipboardService)!;
            clipboard.PrimaryClip = Android.Content.ClipData.NewPlainText("Private firmware lookup", capture.ToJsonString(new() { WriteIndented = true }));
            status.Text = "Private request copied. Review before sharing.";
        });
    }

    async Task RecordFirmwareLookup()
    {
        var journal = LookupPath("recovery");
        if (File.Exists(journal)) throw new RouterException("Restore the previous recording first.");
        await Task.Run(() => client!.ConnectAsync(lifetime.Token), lifetime.Token);
        var router = IPAddress.Parse(client!.Record.Address);
        var local = AndroidNetwork.AddressForRouter(router);
        using var recorder = new FirmwareLookupRecorder(local, router);
        var plan = await Task.Run(() => client.PrepareFirmwareLookupAsync(recorder.Address, lifetime.Token), lifetime.Token);
        var preview = "For up to 90 seconds, replace the firmware lookup address with a recorder on this phone, then restore the ARRIS address. Keep this screen open.\n\n" +
            "This is an unproven URL-only setting on this firmware. The app sends no check, download or install command. It waits for a request the router makes on its own. The recorder serves no files and returns HTTP 503.\n\n" +
            "If the router makes no request, the recording will be empty. An encrypted recovery record is saved before changing anything; if restoration fails, use Restore ARRIS firmware lookup here.\n\n" +
            "Phone recorder: " + recorder.Address + "\nOriginal: " + plan.Original;
        if (!await Confirm("Record the router's lookup?", preview, "Record")) return;
        FirmwareLookupFiles.SavePlan(journal, plan);
        lookupRecording = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        lookupRecording.CancelAfter(TimeSpan.FromSeconds(90));
        var captureFile = LookupPath("capture");
        Task<JsonObject?>? receive = null;
        string result = "No request was recorded. No update command was tried.";
        string restored = "";
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        try
        {
            receive = recorder.RecordAsync(o => FirmwareLookupFiles.Save(captureFile, o), lookupRecording.Token);
            status.Text = "Setting the temporary lookup address…";
            await Task.Run(() => client.StartFirmwareLookupAsync(plan, lookupRecording.Token), lookupRecording.Token);
            status.Text = "Waiting for a natural lookup, up to 90 seconds. Keep this screen open…";
            var captured = await receive;
            if (captured is not null) result = FirmwareLookupRecorder.Summary(captured);
        }
        catch (OperationCanceledException) { result = "Recording ended without a new request. No update command was tried."; }
        catch (Exception ex) when (ex is RouterException or System.Net.Http.HttpRequestException or System.IO.IOException)
        { result = "Recording could not be completed. The setting may be unsupported or its acknowledgement uncertain. No update command was tried."; }
        finally
        {
            lookupRecording.Cancel();
            if (receive is not null) try { await receive; } catch (Exception) { /* Receive is observed; recovery still runs. */ }
            status.Text = "Restoring the ARRIS lookup address…";
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await Task.Run(() => client.RestoreFirmwareLookupAsync(plan, recovery.Token), recovery.Token);
                File.Delete(journal); restored = "Original ARRIS source restored and read back.";
            }
            catch (Exception)
            { restored = "Restoration could not be verified. The recovery record was kept. Reconnect, open Software access diagnostics and use Restore ARRIS firmware lookup before any update."; }
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            lookupRecording.Dispose(); lookupRecording = null;
        }
        await Message("Lookup recording", result + "\n\n" + restored);
        ShowRouter(); await Read();
    }

    protected override void OnStop()
    {
        lookupRecording?.Cancel();
        base.OnStop();
    }
}
