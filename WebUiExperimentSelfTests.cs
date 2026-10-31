using System.Net.Http;
using System.Text.Json.Nodes;

namespace ArrisControl;

internal static class WebUiExperimentSelfTests
{
    static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    sealed class Fixture : IRouterWire
    {
        public JsonObject Info = JsonNode.Parse("""{"SERIAL_KEY":"TESTSERIAL123","MODEL_NAME":"W30","HARDWARE_VERSION":"WR11AXER1","SOFTWARE_VERSION":"AXR.0814.240522.10"}""")!.AsObject();
        public JsonObject State = JsonNode.Parse("""{"BOOTING":"0","SOFTWAREUPDATEACTIVE":"0"}""")!.AsObject();
        public JsonObject Login = JsonNode.Parse("""{"CONTAINER":"LOGINCFG","WEBUIENABLE":"0","SSOUSERNAME":"FictionalUser","SSOPASSWORD":"FictionalSecret","WEBUIPASSWORD":"*****"}""")!.AsObject();
        public int Writes; public bool LostReply, IgnoreWrite, ReadBackFailure;
        public Task<JsonObject> SendAsync(string path, string? body = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (path == "/setData")
            {
                Settings.ValidateEnvelope(body!);
                var root = JsonNode.Parse(body!)!.AsObject(); var request = root["REQUEST"]!.AsObject();
                var login = request["LOGINCFG"]!.AsObject(); var value = Catalog.Text(login["WEBUIENABLE"]);
                Check(root.Count == 1 && request.Count == 2 && Catalog.Text(request["OPERTYPE"]) == "MODIFY" &&
                    login.Count == 1 && value is "0" or "1", "The experiment sent an unexpected field or operation.");
                Check(!body!.Contains("PASSWORD") && !body.Contains("USERNAME") && !body.Contains("FictionalSecret"),
                    "The experiment resent a credential.");
                Writes++; if (!IgnoreWrite) Login["WEBUIENABLE"] = value;
                if (LostReply) throw new HttpRequestException("Simulated lost acknowledgement");
                return Task.FromResult(new JsonObject { ["STATUS"] = "OK" });
            }
            var entry = path switch {
                "/getData?key=DEVICE_INFO_ALL" => ("DEVICE_INFO", Info),
                "/getData?key=DEVICESTATUS_ALL" => ("DEVICESTATUS", State),
                "/getData?key=LOGINCFG_ALL" => ("LOGINCFG", Login),
                _ => throw new InvalidOperationException("Unexpected experimental read.")
            };
            if (ReadBackFailure && Writes > 0 && entry.Item1 == "LOGINCFG") throw new RouterException("Read-back fixture failure");
            return Task.FromResult(new JsonObject { ["STATUS"] = "OK", [entry.Item1] = entry.Item2.DeepClone() });
        }
        public void Dispose() { }
    }
    static RouterClient Client(Fixture fixture) => new(new CredentialRecord("", "TESTSERIAL123", "001122334455", "192.168.7.1"), fixture);
    static async Task Refuses(Func<Task> operation, string message)
    {
        try { await operation(); } catch (RouterException) { return; }
        throw new InvalidOperationException(message);
    }
    public static async Task RunAsync()
    {
        foreach(var value in new[]{"0","1"}) Settings.ValidateEnvelope(new WebUiPlan(new JsonObject(),value,"","").Body);
        foreach(var body in new[]{
            """{"REQUEST":{"OPERTYPE":"MODIFY","LOGINCFG":{"WEBUIENABLE":"2"}}}""",
            """{"REQUEST":{"OPERTYPE":"MODIFY","LOGINCFG":{"WEBUIENABLE":true}}}""",
            """{"REQUEST":{"OPERTYPE":"MODIFY","LOGINCFG":{"WEBUIENABLE":"1","WEBUIPASSWORD":"no"}}}""",
            """{"REQUEST":{"OPERTYPE":"DELETE","LOGINCFG":{"WEBUIENABLE":"1"}}}"""})
            await Refuses(()=>{Settings.ValidateEnvelope(body);return Task.CompletedTask;},"Unsafe login envelope admitted.");
        var normal = new Fixture();
        using (var client = Client(normal))
        {
            var plan = await client.PrepareWebUiExperimentAsync(true);
            Check(normal.Writes == 0 && !plan.Preview.Contains("FictionalSecret") && !plan.ToString().Contains("FictionalSecret"),
                "Preparing a review sent a request or exposed a secret.");
            var result = await client.ApplyWebUiExperimentAsync(plan);
            Check(normal.Writes == 1 && result.StartsWith("Flag enabled and read back") &&
                Catalog.Text(normal.Login["SSOPASSWORD"]) == "FictionalSecret", "Enable/read-back or credential preservation failed.");
            await client.ApplyWebUiExperimentAsync(await client.PrepareWebUiExperimentAsync(false));
            Check(normal.Writes == 2 && Catalog.Text(normal.Login["WEBUIENABLE"]) == "0", "Disable/read-back failed.");
            await Refuses(() => client.ApplyWebUiExperimentAsync(new(normal.Login, "2", "TESTSERIAL123", "001122334455")), "Invalid flag was accepted.");
        }
        foreach (var fault in new[] { "stale", "identity", "hardware", "upgrade", "malformed" })
        {
            var fixture = new Fixture(); using var client = Client(fixture);
            var plan = await client.PrepareWebUiExperimentAsync(true);
            switch (fault)
            {
                case "stale": fixture.Login["SSOPASSWORD"] = "Changed"; break;
                case "identity": fixture.Info["SERIAL_KEY"] = "AnotherSerial"; break;
                case "hardware": fixture.Info["HARDWARE_VERSION"] = "Unknown"; break;
                case "upgrade": fixture.State["SOFTWAREUPDATEACTIVE"] = "1"; break;
                case "malformed": fixture.Login["WEBUIENABLE"] = new JsonObject(); break;
            }
            await Refuses(() => client.ApplyWebUiExperimentAsync(plan), "Unsafe state accepted: " + fault);
            Check(fixture.Writes == 0, "A refused experiment wrote to the router: " + fault);
        }
        var lost = new Fixture { LostReply = true }; using (var client = Client(lost))
        {
            var plan = await client.PrepareWebUiExperimentAsync(true);
            await Refuses(() => client.ApplyWebUiExperimentAsync(plan), "A lost acknowledgement was reported as success.");
            Check(lost.Writes == 1, "An uncertain experiment was retried.");
        }
        var ignored = new Fixture { IgnoreWrite = true }; using (var client = Client(ignored))
            Check((await client.ApplyWebUiExperimentAsync(await client.PrepareWebUiExperimentAsync(true))).Contains("did not change"), "Acknowledgement was confused with a changed flag.");
        var unreadable = new Fixture { ReadBackFailure = true }; using (var client = Client(unreadable))
            Check((await client.ApplyWebUiExperimentAsync(await client.PrepareWebUiExperimentAsync(true))).Contains("read-back failed") && unreadable.Writes == 1, "Read-back failure was retried or reported as verified.");
        var cancelled = new Fixture(); using (var client = Client(cancelled))
        {
            var plan = await client.PrepareWebUiExperimentAsync(true);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { await client.ApplyWebUiExperimentAsync(plan, stop.Token); throw new InvalidOperationException("Cancelled experiment continued."); }
            catch (OperationCanceledException) { }
            Check(cancelled.Writes == 0, "Cancelled experiment sent a write.");
        }
        Console.WriteLine("PASS: reviewed one-field web-interface experiment, credential omission, disable/read-back, stale/identity/hardware/upgrade guards, cancellation and no uncertain-write retry.");
    }
}
