using System.Net.Http;
using System.Text.Json.Nodes;

namespace ArrisControl;

// Kept outside generic settings: only the explicitly confirmed Android experiment
// can send this one-field request. Account/password fields are never copied.
internal sealed class WebUiPlan(JsonObject before, string next, string serial, string mac)
{
    internal JsonObject Before { get; } = (JsonObject)before.DeepClone();
    internal string Next { get; } = next;
    internal string Serial { get; } = serial;
    internal string Mac { get; } = mac;
    internal string Body => new JsonObject { ["REQUEST"] = new JsonObject {
        ["OPERTYPE"] = "MODIFY", ["LOGINCFG"] = new JsonObject { ["WEBUIENABLE"] = Next }
    }}.ToJsonString();
    public string Preview => $"Local browser interface: {Catalog.Text(Before["WEBUIENABLE"])} → {Next}.\n\n" +
        "This firmware reports browser support as unavailable, so the switch may have no effect. " +
        "Only the browser-interface flag is sent. No account or Wi-Fi password is included. " +
        "This does not enable SSH or install firmware.";
    public override string ToString() => "Local browser interface experiment (private baseline withheld)";
}

internal sealed partial class RouterClient
{
    async Task<JsonObject> ReadWebUiBaselineAsync(CancellationToken token)
    {
        await ConnectAsync(token); await VerifyIdentityAsync(token);
        var info = (await ReadAsync("DEVICE_INFO_ALL", token))["DEVICE_INFO"];
        if (Catalog.Text(info?["MODEL_NAME"]) != "W30" ||
            Catalog.Text(info?["HARDWARE_VERSION"]) != "WR11AXER1" ||
            Catalog.Text(info?["SOFTWARE_VERSION"]) != "AXR.0814.240522.10")
            throw new RouterException("This experiment is limited to the W30 hardware and firmware that were inspected.");
        var state = (await ReadAsync("DEVICESTATUS_ALL", token))["DEVICESTATUS"];
        if (Catalog.Text(state?["BOOTING"]) != "0" || Catalog.Text(state?["SOFTWAREUPDATEACTIVE"]) != "0")
            throw new RouterException("The router must report that startup and software updates are inactive.");
        var config = (await ReadAsync("LOGINCFG_ALL", token))["LOGINCFG"] as JsonObject;
        if (config is null || config["WEBUIENABLE"] is not JsonValue ||
            Catalog.Text(config["WEBUIENABLE"]) is not ("0" or "1"))
            throw new RouterException("The router did not return a supported browser-interface flag.");
        return config;
    }
    public async Task<WebUiPlan> PrepareWebUiExperimentAsync(bool enable, CancellationToken token = default)
    {
        var before = await ReadWebUiBaselineAsync(token); var next = enable ? "1" : "0";
        if (Catalog.Text(before["WEBUIENABLE"]) == next)
            throw new RouterException("The router already reports that value. No request is needed.");
        return new(before, next, record.Serial, record.Mac);
    }
    public async Task<string> ApplyWebUiExperimentAsync(WebUiPlan plan, CancellationToken token = default)
    {
        if (plan.Next is not ("0" or "1") || plan.Next == Catalog.Text(plan.Before["WEBUIENABLE"]))
            throw new RouterException("Unsupported or unchanged browser-interface flag.");
        if (plan.Serial != record.Serial || plan.Mac != record.Mac)
            throw new RouterException("The selected router changed. Review the experiment again.");
        var fresh = await ReadWebUiBaselineAsync(token);
        if (!JsonNode.DeepEquals(fresh, plan.Before))
            throw new RouterException("Login settings changed after the preview. Refresh and review again; nothing was sent.");
        token.ThrowIfCancellationRequested();
        // One attempt, including when the acknowledgement is lost. Never resend
        // a login group, fill absent passwords, or retry an uncertain write.
        try { await wire.SendAsync("/setData", plan.Body, token); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { throw new RouterException("The browser-interface request was sent, but its result is unknown. Refresh before another attempt; it was not retried."); }
        try
        {
            var after = (await ReadAsync("LOGINCFG_ALL", token))["LOGINCFG"] as JsonObject;
            if (after is null) return "Acknowledged; the flag could not be verified. Refresh before another attempt.";
            var unchanged = (JsonObject)after.DeepClone(); unchanged["WEBUIENABLE"] = plan.Before["WEBUIENABLE"]?.DeepClone();
            if (!JsonNode.DeepEquals(unchanged, plan.Before))
                return "Acknowledged, but other returned login fields changed. Stop this experiment and verify the saved login before continuing.";
            if (Catalog.Text(after["WEBUIENABLE"]) != plan.Next)
                return "Acknowledged, but the browser-interface flag did not change. This does not confirm browser access.";
            return plan.Next == "1"
                ? "Flag enabled and read back. Browser access is not verified. Try the router address in a browser, then refresh this report. You can disable the flag here."
                : "Flag disabled and read back. No account or Wi-Fi password was sent.";
        }
        catch (Exception ex) when (ex is RouterException or HttpRequestException or TaskCanceledException)
        { return "Acknowledged; read-back failed. Refresh before another attempt. Nothing was retried."; }
    }
}
