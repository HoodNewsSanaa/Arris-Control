using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace ArrisControl;

internal sealed record LocalOnboardingState(CredentialRecord Router, string Pin, string Model, string Hardware,
    string Firmware, string Mode, string Onboarding, string BluetoothService, string Booting, string Updating, DateTimeOffset Time);
internal sealed record LocalCompletionResult(bool Acknowledged, bool Complete, string Message, LocalOnboardingState? State);

internal sealed class LocalCompletionPlan
{
    // MobAppOperation.php: normal authenticated setter. It calls the existing
    // obagent process locally; no phone Bluetooth link or arbitrary command.
    public const string Body = "{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"TURNOFFBLE\":\"1\"}}";
    readonly LocalOnboardingState before;
    int used;
    LocalCompletionPlan(LocalOnboardingState state) { before = state; }
    public static string? Unavailable(LocalOnboardingState state)
    {
        if (state.Onboarding == "0") return "The router already reports onboarding complete. No completion request is needed.";
        if (state.Onboarding != "1") return "The router did not return a recognized onboarding flag. Completion is unavailable; normal administration can continue.";
        if (state.BluetoothService == "0") return "The router's onboarding process is stopped (BLESTATUS=0). The known local completion command requires that process. No verified alternative is available on this firmware. Normal local administration can continue.";
        if (state.BluetoothService != "1") return "The router did not report whether its onboarding process is running. No completion request will be sent.";
        if (state.Model != "W30" || state.Hardware != "WR11AXER1" || state.Firmware != "AXR.0814.240522.10")
            return "Local completion is limited to the inspected W30 hardware and firmware. This router can still be managed normally.";
        if (!CertificatePin.IsValid(state.Pin) || !CredentialStore.IsPrivate(state.Router.Address)) return "A verified, pinned local admin connection is required.";
        if (state.Booting != "0" || state.Updating != "0") return "Startup or firmware activity is active or unknown. Recheck status before finishing onboarding.";
        return null;
    }
    public static LocalCompletionPlan Prepare(LocalOnboardingState state)
    {
        if (Unavailable(state) is { } reason) throw new RouterException(reason);
        return new(state);
    }
    static bool SameRouter(LocalOnboardingState a, LocalOnboardingState b) =>
        a.Router.Serial == b.Router.Serial && a.Router.Mac == b.Router.Mac && a.Pin == b.Pin &&
        a.Model == b.Model && a.Hardware == b.Hardware && a.Firmware == b.Firmware;
    public string Preview => $"Router: {before.Router.Serial}\nAddress: {before.Router.Address}\nRouting mode: {before.Mode}\n\nSend one local completion request through the normal admin API. The router reports its onboarding process running. The phone does not need Bluetooth. The request asks that process to mark setup complete, then stops its Bluetooth services. Connectivity may briefly change.\n\nSaved admin credentials, Wi-Fi settings and mesh membership are not changed by the app. A fresh normal login must confirm OBASTATUS=0 before success is reported. This firmware path still needs physical-router validation.";
    public async Task<LocalCompletionResult> ApplyAsync(Func<CancellationToken, Task<LocalOnboardingState>> refresh,
        Func<string, CancellationToken, Task> write, CancellationToken token)
    {
        if (Interlocked.Exchange(ref used, 1) != 0) throw new RouterException("This completion attempt is closed. Recheck its result before another attempt.");
        token.ThrowIfCancellationRequested();
        var fresh = await refresh(token);
        if (!SameRouter(before, fresh) || before.Router.Address != fresh.Router.Address || before.Mode != fresh.Mode)
            throw new RouterException("Router identity, address or mode changed after review. No completion request was sent.");
        if (Unavailable(fresh) is { } reason) throw new RouterException(reason);
        token.ThrowIfCancellationRequested();
        try { await write(Body, token); }
        catch (Exception ex) when (ex is RouterException or HttpRequestException or IOException or OperationCanceledException)
        { return new(false, false, "One local completion request was attempted; its result is unknown. It was not retried. Recheck the normal login and onboarding flag.", null); }
        LocalOnboardingState after;
        try { after = await refresh(token); }
        catch (Exception ex) when (ex is RouterException or HttpRequestException or IOException or OperationCanceledException)
        { return new(true, false, "The completion request was acknowledged, but the normal login could not be verified afterward. Reconnect and check status; no request was repeated.", null); }
        if (!SameRouter(before, after)) return new(true, false, "The returned router identity changed. Completion remains unverified.", null);
        if (after.Mode != before.Mode) return new(true, false,
            "The router's routing mode changed during completion. Review its status before treating the operation as successful. No automatic settings restoration was attempted.", after);
        return after.Onboarding switch
        {
            "0" => new(true, true, "Onboarding is complete (OBASTATUS=0), and the saved normal admin login works.", after),
            "1" => new(true, false, "The request was acknowledged, but the router still reports incomplete onboarding (OBASTATUS=1). No request was repeated. Normal admin access works.", after),
            _ => new(true, false, "Normal admin access works, but the router did not return a recognized completion flag. Completion is unverified.", after)
        };
    }
}

internal sealed partial class RouterClient
{
    public async Task<LocalOnboardingState> ReadLocalOnboardingStateAsync(CancellationToken token = default)
    {
        await ReconnectAsync(token);
        var state = (await ReadAsync("DEVICESTATUS_ALL", token))["DEVICESTATUS"];
        return new(record, pin ?? "", Model, HardwareVersion, FirmwareVersion, RoutingMode, OnboardingStatus,
            BluetoothServiceStatus, Catalog.Text(state?["BOOTING"]), Catalog.Text(state?["SOFTWAREUPDATEACTIVE"]), DateTimeOffset.Now);
    }
    public async Task<LocalCompletionPlan> PrepareLocalCompletionAsync(CancellationToken token = default) =>
        LocalCompletionPlan.Prepare(await ReadLocalOnboardingStateAsync(token));
    public Task<LocalCompletionResult> ApplyLocalCompletionAsync(LocalCompletionPlan plan, CancellationToken token = default) =>
        plan.ApplyAsync(ReadLocalOnboardingStateAsync, async (body, ct) => { await wire.SendAsync("/setData", body, ct); }, token);
}
