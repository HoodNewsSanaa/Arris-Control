using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ArrisControl;

internal static class FirmwareLookupCaptureSelfTests
{
    const string Recorder = "http://192.168.7.2:8765/arris-lookup/0123456789abcdef0123456789abcdef/manifest";
    static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    sealed class Fixture : IRouterWire
    {
        public JsonObject Info = JsonNode.Parse("""{"SERIAL_KEY":"TESTSERIAL123","MODEL_NAME":"W30","HARDWARE_VERSION":"WR11AXER1","SOFTWARE_VERSION":"AXR.0814.240522.10"}""")!.AsObject();
        public JsonObject State = JsonNode.Parse("""{"BOOTING":"0","SOFTWAREUPDATEACTIVE":"0"}""")!.AsObject();
        public JsonObject Firmware = JsonNode.Parse("""{"UPGRADE_SW":"0"}""")!.AsObject();
        public JsonObject Update = new() { ["URL"] = FirmwareLookupPlan.Vendor, ["STATUS"] = "Unknown", ["PERCENTAGE"] = "0",
            ["CURRENT_VERSION"] = "AXR.0814.240522.10", ["UPGRADE_VERSION"] = "AXR.0814.240522.10" };
        public int Writes; public bool LoseReply, IgnoreWrite;
        public Task<JsonObject> SendAsync(string path, string? body = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (path == "/setData")
            {
                Settings.ValidateEnvelope(body!);
                var request = JsonNode.Parse(body!)!["REQUEST"]!;
                var source = request["UPDATE"]!.AsObject();
                Check(source.Count == 2 && source["CHECKNOW"] is null && source["VERSION"] is null, "Firmware action escaped source-only restriction.");
                Writes++; if (!IgnoreWrite) Update["URL"] = Catalog.Text(source["URL"]);
                if (LoseReply) throw new HttpRequestException("Fictional lost acknowledgement");
                return Task.FromResult(new JsonObject { ["STATUS"] = "OK" });
            }
            var entry = path switch {
                "/getData?key=DEVICE_INFO_ALL" => ("DEVICE_INFO", Info),
                "/getData?key=DEVICESTATUS_ALL" => ("DEVICESTATUS", State),
                "/getData?key=FIRMWARE_ALL" => ("FIRMWARE", Firmware),
                "/getData?key=UPDATE_ALL" => ("UPDATE", Update),
                _ => throw new InvalidOperationException("Unexpected lookup request") };
            return Task.FromResult(new JsonObject { ["STATUS"] = "OK", [entry.Item1] = entry.Item2.DeepClone() });
        }
        public void Dispose() { }
    }
    static RouterClient Client(Fixture f) => new(new("", "TESTSERIAL123", "001122334455", "192.168.7.1"), f);
    static async Task Refuses(Func<Task> f)
    {
        try { await f(); } catch (RouterException) { return; }
        throw new InvalidOperationException("Unsafe lookup was accepted.");
    }
    public static async Task RunAsync()
    {
        Settings.ValidateEnvelope(FirmwareLookupPlan.Body(Recorder));
        Settings.ValidateEnvelope(FirmwareLookupPlan.Body(FirmwareLookupPlan.Vendor));
        foreach (var bad in new[] { "http://8.8.8.8:8765/manifest", "https://example.com/firmware", Recorder + "?x=1", Recorder + "#x",
            Recorder.Replace(":8765", ":80"), Recorder.Replace("192.168.7.2", "user@192.168.7.2"), Recorder.Replace("/manifest", "/image.bin") })
            Check(!FirmwareLookupPlan.AllowedSource(bad), "Unexpected update source accepted.");
        foreach (var extra in new[] { "CHECKNOW", "VERSION", "CURRENT_VERSION", "UPGRADE_SW" })
        {
            var body = JsonNode.Parse(FirmwareLookupPlan.Body(Recorder))!; body["REQUEST"]!["UPDATE"]![extra] = "CheckNowDownloadInstall";
            await Refuses(() => { Settings.ValidateEnvelope(body.ToJsonString()); return Task.CompletedTask; });
        }
        var normal = new Fixture(); using (var client = Client(normal))
        {
            var plan = await client.PrepareFirmwareLookupAsync(Recorder, default);
            Check(normal.Writes == 0, "Preview changed the router.");
            await client.StartFirmwareLookupAsync(plan, default);
            normal.Update["STATUS"] = "Error";
            await client.RestoreFirmwareLookupAsync(plan, default);
            await client.RestoreFirmwareLookupAsync(plan, default);
            Check(normal.Writes == 2 && Catalog.Text(normal.Update["URL"]) == plan.Original, "URL-only restoration or already-restored handling failed.");
        }
        foreach (var fault in new[] { "identity", "hardware", "boot", "active", "upgrade", "pending", "progress", "status", "source" })
        {
            var f = new Fixture(); using var client = Client(f); var plan = await client.PrepareFirmwareLookupAsync(Recorder, default);
            switch (fault)
            {
                case "identity": f.Info["SERIAL_KEY"] = "OTHERUNIT1234"; break;
                case "hardware": f.Info["HARDWARE_VERSION"] = "Other"; break;
                case "boot": f.State["BOOTING"] = "1"; break;
                case "active": f.State["SOFTWAREUPDATEACTIVE"] = "1"; break;
                case "upgrade": f.Firmware["UPGRADE_SW"] = "1"; break;
                case "pending": f.Update["UPGRADE_VERSION"] = "Other"; break;
                case "progress": f.Update["PERCENTAGE"] = "1"; break;
                case "status": f.Update["STATUS"] = "Downloading"; break;
                case "source": f.Update["URL"] = "https://example.com/changed"; break;
            }
            await Refuses(() => client.StartFirmwareLookupAsync(plan, default));
            Check(f.Writes == 0, "Unsafe lookup sent a request: " + fault);
        }
        var lost = new Fixture { LoseReply = true }; using (var client = Client(lost))
        {
            var plan = await client.PrepareFirmwareLookupAsync(Recorder, default);
            try { await client.StartFirmwareLookupAsync(plan, default); throw new InvalidOperationException("Lost reply reported as verified."); }
            catch (HttpRequestException) { }
            Check(lost.Writes == 1, "Uncertain source write retried.");
            lost.LoseReply = false; await client.RestoreFirmwareLookupAsync(plan, default);
            Check(lost.Writes == 2, "Uncertain write could not be restored with a fresh read.");
        }
        var ignored = new Fixture { IgnoreWrite = true }; using (var client = Client(ignored))
            await Refuses(async () => await client.StartFirmwareLookupAsync(await client.PrepareFirmwareLookupAsync(Recorder, default), default));
        var changed = new Fixture(); using (var client = Client(changed))
        {
            var plan = await client.PrepareFirmwareLookupAsync(Recorder, default);
            changed.Update["URL"] = "https://example.com/changed";
            await Refuses(() => client.RestoreFirmwareLookupAsync(plan, default));
            Check(changed.Writes == 0, "Restoration overwrote a third-party change.");
        }
        var folder = Path.Combine(Path.GetTempPath(), "arris-lookup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = FirmwareLookupFiles.PathFor(folder, "fictional", "001122334455", "recovery");
            var plan = new FirmwareLookupPlan("fictional", "001122334455", FirmwareLookupPlan.Vendor, Recorder);
            FirmwareLookupFiles.SavePlan(path, plan);
            Check(FirmwareLookupFiles.LoadPlan(path) == plan && !Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("surfboardconnect"), "Recovery record encryption/roundtrip failed.");
            var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
            try { FirmwareLookupFiles.LoadPlan(path); throw new InvalidOperationException("Tampered recovery accepted."); }
            catch (CryptographicException) { }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        const string pathOnly = "/arris-lookup/0123456789abcdef0123456789abcdef/manifest";
        var capture = FirmwareLookupRecorder.ParseRequest("GET " + pathOnly + "?model=W30&serial=FICTIONAL HTTP/1.1\r\nHost: local\r\nAuthorization: SECRET\r\n\r\n", pathOnly)!;
        Check(capture is not null && !FirmwareLookupRecorder.Summary(capture).Contains("FICTIONAL") && !FirmwareLookupRecorder.Summary(capture).Contains("SECRET"), "Capture summary disclosed values.");
        foreach (var request in new[] { "GET /image.bin HTTP/1.1\r\n\r\n", "POST " + pathOnly + " HTTP/1.1\r\n\r\n", "GET " + pathOnly + " HTTP/1.1\r\n", new string('x', 17000) })
            Check(FirmwareLookupRecorder.ParseRequest(request, pathOnly) is null, "Recorder accepted an unsupported request.");
        Console.WriteLine("PASS: URL-only firmware lookup capture, exact transport allowlist, idle/identity guards, uncertain-write restoration, encrypted recovery and redacted summaries; no install/check commands.");
    }

    internal static async Task RunRecorderAsync(IPAddress local, CancellationToken token)
    {
        using var recorder = new FirmwareLookupRecorder(local, local, 0);
        var saved = false;
        var receive = recorder.RecordAsync(_ => saved = true, token);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        using var response = await http.GetAsync(recorder.Address + "?model=W30&version=FICTIONAL", token);
        var result = await receive;
        Check((int)response.StatusCode == 503 && (await response.Content.ReadAsByteArrayAsync(token)).Length == 0 && saved && result is not null,
            "Recorder did not persist before returning an empty 503.");
        Console.WriteLine("PASS: real TCP lookup recorder returned empty HTTP 503 and retained the request; no files or redirects served.");
    }
}
