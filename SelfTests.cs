using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace ArrisControl;
internal static class SelfTests
{
    static void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static void Refuses(Action action,string message){try{action();}catch(RouterException){return;}throw new InvalidOperationException(message);}
    sealed class FakeWire : IRouterWire
    {
        public Dictionary<string,JsonObject> State=new(){
            ["DEVICEOPERMODE_ALL"]=new JsonObject{["STATUS"]="OK",["DEVICEOPERMODE"]=new JsonObject{["MODE"]="DUAL"}},
            ["DEVICE_INFO_ALL"]=JsonNode.Parse("{\"STATUS\":\"OK\",\"DEVICE_INFO\":{\"SERIAL_KEY\":\"TESTSERIAL123\"}}")!.AsObject(),
            ["NETWORKDNS_ALL"]=JsonNode.Parse("{\"STATUS\":\"OK\",\"NETWORKDNS\":{\"DYNAMICV4\":\"0\",\"DNSIPV4SERVERS\":\"8.8.8.8,1.1.1.1\",\"DYNAMICV6\":\"1\",\"DNSIPV6SERVERS\":\"0.0.0.0,0.0.0.0\"}}")!.AsObject()};
        public int Writes;public bool LoseWriteReply;
        public Task<JsonObject> SendAsync(string path,string? body=null,CancellationToken token=default)
        {
            if(path=="/setData")
            {
                Writes++;if(LoseWriteReply)throw new HttpRequestException("simulated connection loss");
                Settings.ValidateEnvelope(body!);var request=JsonNode.Parse(body!)!["REQUEST"]!;
                if(request["DEVICEOPERMODE"] is JsonObject mode){State["DEVICEOPERMODE_ALL"]["DEVICEOPERMODE"]=mode.DeepClone();return Task.FromResult(new JsonObject{["STATUS"]="OK"});}
                var payload=request["NETWORKDNS"]!.AsObject();var dns=State["NETWORKDNS_ALL"]["NETWORKDNS"]!.AsObject();
                foreach(var name in new[]{"DYNAMICV4","DYNAMICV6"})dns[name]=payload[name]!.DeepClone();
                dns["DNSIPV4SERVERS"]=payload["IPV4ADDRESS"]!.DeepClone();dns["DNSIPV6SERVERS"]=payload["IPV6ADDRESS"]!.DeepClone();
                return Task.FromResult(new JsonObject{["STATUS"]="OK"});
            }
            if(!path.StartsWith("/getData?key=",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected fake request");
            return Task.FromResult((JsonObject)State[path[13..]].DeepClone());
        }
        public void Dispose(){}
    }
    static void FeatureTests()
    {
        SettingDefinition Def(string container)=>Settings.Definitions.Single(d=>d.Container==container);
        // New toggles produce a minimal MODIFY envelope for their own container.
        var dfs=Settings.Prepare(Def("WIFI_FH_DFS"),"WIFI_FH_DFS",new JsonObject{["ENABLE"]="1"},new(){["ENABLE"]="0"});
        Assert(dfs.Body=="{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"WIFI_FH_DFS\":{\"ENABLE\":\"0\"}}}","DFS toggle envelope changed.");
        Refuses(()=>Settings.Prepare(Def("LEDRING"),"LEDRING",new JsonObject{["ENABLE"]="1",["BRIGHTNESS"]="50"},new(){["BRIGHTNESS"]="101"}),"Out-of-range brightness accepted.");
        Assert(Settings.Prepare(Def("LEDRING"),"LEDRING",new JsonObject{["ENABLE"]="1",["BRIGHTNESS"]="50"},new(){["BRIGHTNESS"]="0"}).Body.Contains("\"BRIGHTNESS\":\"0\""),"Brightness 0 rejected.");
        // Static WAN: valid mask accepted, missing gateway and bad masks refused.
        var wan=new JsonObject{["DYNAMICV4"]="1",["WANIPV4ADDRESS"]="",["SUBNETMASK"]="",["GATEWAYIPV4"]="",["DNSIPV4SERVERS"]=""};
        var edits=new Dictionary<string,string>{["DYNAMICV4"]="0",["WANIPV4ADDRESS"]="203.0.113.10",["SUBNETMASK"]="255.255.255.0",["GATEWAYIPV4"]="203.0.113.1",["DNSIPV4SERVERS"]="9.9.9.9"};
        Assert(Settings.Prepare(Def("WANSTATICIPV4"),"WANSTATICIPV4",wan,edits).Body.Contains("\"SUBNETMASK\":\"255.255.255.0\""),"Valid static WAN refused.");
        Refuses(()=>Settings.Prepare(Def("WANSTATICIPV4"),"WANSTATICIPV4",wan,new(edits){["GATEWAYIPV4"]=""}),"Static WAN without gateway accepted.");
        Refuses(()=>Settings.Prepare(Def("WANSTATICIPV4"),"WANSTATICIPV4",wan,new(edits){["SUBNETMASK"]="255.0.255.0"}),"Non-contiguous subnet mask accepted.");
        // Blocked websites: plain domains only.
        var site=Settings.Prepare(Def("PC_URLS_BLOCKED"),"PC_URLS_BLOCKED",null,new(){["ENABLE"]="1",["URL"]="example.com"},"ADD",-1);
        Assert(site.Body.Contains("\"URL\":\"example.com\"") && site.Body.Contains("\"OPERTYPE\":\"ADD\""),"Blocked-site add envelope changed.");
        Refuses(()=>Settings.Prepare(Def("PC_URLS_BLOCKED"),"PC_URLS_BLOCKED",null,new(){["ENABLE"]="1",["URL"]="http://bad url"},"ADD",-1),"Invalid website accepted.");
        // One-shot actions are allowlisted exactly and never become editable sections.
        Assert(Settings.Action("WANIPCONFIG",new(){["IPSTACK"]="IPV4",["RENEW"]="true"})=="{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"WANIPCONFIG\":{\"IPSTACK\":\"IPV4\",\"RENEW\":\"true\"}}}","WAN renew body changed.");
        Assert(Settings.Action("SPEEDTESTWAN",new(){["DIAGSTATE"]="Requested"})=="{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"SPEEDTESTWAN\":{\"DIAGSTATE\":\"Requested\"}}}","Speed test body changed.");
        Refuses(()=>Settings.Action("SPEEDTESTWAN",new(){["DIAGSTATE"]="Cancel"}),"Unknown action value accepted.");
        Refuses(()=>Settings.Action("WIFI_FH_DFS",new(){["ENABLE"]="1"}),"A settings container was accepted as an action.");
        Refuses(()=>Settings.Action("WANIPCONFIG",new(){["IPSTACK"]="IPV4",["RENEW"]="true",["EXTRA"]="1"}),"Extra action field accepted.");
        var page=new Page("t","t","",["SPEEDTESTWAN_ALL"]);
        var snap=new Snapshot(page,new(){["SPEEDTESTWAN_ALL"]=new JsonObject{["STATUS"]="OK",["SPEEDTESTWAN"]=new JsonObject{["DIAGSTATE"]="Completed"}}},[],[],DateTimeOffset.Now);
        Assert(Settings.Sections(snap).Count==0,"An action became an editable section.");
        // Channel choices per band, keeping an unlisted current value.
        var ch24=Settings.ChannelChoices("WIFI_24G","6");var ch5=Settings.ChannelChoices("WIFI_5G","13");
        Assert(ch24.Any(c=>c.Value=="11") && ch24.All(c=>c.Value!="36") && ch5.Any(c=>c.Value=="149") && ch5.Any(c=>c.Value=="52" && c.Label.Contains("DFS")) && ch5[0].Value=="13","Channel choices changed.");
        Assert(Catalog.ReadKeys.IsSupersetOf(Diagnostics.Keys) && !Diagnostics.Keys.Any(k=>k.StartsWith("OB_") || k is "ADMIN_LOGIN_ALL" or "CAPTCHA_INFO_ALL" or "REMOTE_CONFIG_ALL"),"Diagnostics key allowlist changed.");
        // Diagnostics never reveal secrets or identifying values.
        Assert(Diagnostics.Value("PASSPHRASE","hunter22")=="[hidden]" && Diagnostics.Value("GATEWAYIPV4","192.168.0.1")=="<ipv4>" && Diagnostics.Value("X","fe80::1")=="<ipv6>","IP/secret redaction failed.");
        Assert(Diagnostics.Value("MACADDRESS","00:11:22:33:44:55")=="<mac>" && Diagnostics.Value("SSIDNAME","My Home")=="<text, 7 chars>" && Diagnostics.Value("ENABLE","1")=="1" && Diagnostics.Value("CHANNEL","36")=="36","Value description failed.");
        var report=Diagnostics.Report(new Snapshot(Diagnostics.Page,new(){
            ["DEVICE_INFO_ALL"]=JsonNode.Parse("{\"STATUS\":\"OK\",\"DEVICE_INFO\":{\"MODEL_NAME\":\"W31\",\"SERIAL_KEY\":\"SECRETSERIAL1\"}}")!.AsObject(),
            ["WIFI_5G_ALL"]=JsonNode.Parse("{\"STATUS\":\"OK\",\"WIFI_5G\":[{\"SSIDNAME\":\"Home Net\",\"PASSPHRASE\":\"hunter22hunter\",\"CHANNEL\":\"44\"}]}")!.AsObject()},
            [new("DEVICE_INFO_ALL",200,"OK",DateTimeOffset.Now,true),new("WIFI_5G_ALL",200,"OK",DateTimeOffset.Now,true),new("WPS",null,"Unsupported",DateTimeOffset.Now,false)],[],DateTimeOffset.Now),"Test","0.0.0");
        Assert(report.Contains("model W31") && report.Contains("[--] WPS") && report.Contains("WIFI_5G[0].CHANNEL = 44") && !report.Contains("SECRETSERIAL1") && !report.Contains("Home Net") && !report.Contains("hunter22"),"Diagnostics report leaked or lost information.");
    }
    public static async Task RunAsync()
    {
        await CloudSelfTests.RunAsync();
        await DiscoverySelfTests.RunAsync();
        FeatureTests();
        Assert(CredentialStore.IsPrivate("192.168.7.1")&&!CredentialStore.IsPrivate("8.8.8.8")&&!CredentialStore.IsPrivate("127.0.0.1"),"Local address boundary failed.");
        var login=RouterClient.LoginBody("a+b@example.com","P@ss word!é","abcxyz123_windows_test",123456);
        var expected=Convert.ToBase64String(Encoding.UTF8.GetBytes("a%2Bb%40example.com:P%40ss%20word!%C3%A9"));
        Assert(login==$"arg={expected}&_n=08292&_=123456&_uuid=abcxyz123_windows_test&_content=json","Native credential encoding differs from the verified protocol.");
        foreach(var response in new[]{"{\"RESPONSE\":{\"STATUS\":\"SessionTimeout\"}}","{\"RESPONSE\":{\"STATUS\":\"Login-OK\"}}","{\"RESPONSE\":{\"STATUS\":\"IncorrectCredentials\"}}","{\"RESPONSE\":{}}","<html>error</html>"})
            Refuses(()=>LocalWire.ParseResponse(Encoding.UTF8.GetBytes(response)),"Failed or login-only response accepted as protected data.");
        var secret=JsonNode.Parse("{\"STATUS\":\"OK\",\"WIFI\":{\"PASSWORD\":{\"value\":\"fixture-secret\"},\"SSIDNAME\":\"fixture network\"},\"TOKEN\":\"fixture-token\"}")!;
        var rows=Catalog.Flatten(secret,"FIXTURE");Assert(rows.Any(r=>r.Value=="fixture network") && rows.All(r=>!r.Value.Contains("fixture-secret")&&!r.Value.Contains("fixture-token")),"Secret branch redaction failed.");
        var mode=Settings.Definitions.Single(d=>d.Container=="DEVICEOPERMODE");
        var bridge=Settings.Prepare(mode,"DEVICEOPERMODE",new JsonObject{["MODE"]="DUAL"},new(){["MODE"]="BRIDGE"});
        Assert(bridge.Preview.Contains("unreachable") && bridge.Preview.Contains("has not been verified"),"Bridge enable warning missing.");
        foreach(var body in new[]{"{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"DEVICEOPERMODE\":{\"MODE\":\"UNKNOWN\"}}}","{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"UPDATE\":{\"CHECKNOW\":\"CheckNowDownloadInstall\"}}}","{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"NETWORKMISC\":{\"ADMIN_PASSWORD\":\"x\"}}}"})
            Refuses(()=>Settings.ValidateEnvelope(body),"Unexpected modifying payload accepted.");
        var definition=Settings.Definitions.Single(d=>d.Container=="NETWORKDNS");var wire=new FakeWire();var baseline=wire.State["NETWORKDNS_ALL"]["NETWORKDNS"]!.AsObject();
        var change=Settings.Prepare(definition,"NETWORKDNS",baseline,new(){["DNSIPV4SERVERS"]="9.9.9.9,1.1.1.1"});
        var bodyNode=JsonNode.Parse(change.Body)!["REQUEST"]!["NETWORKDNS"]!;
        Assert(Catalog.Text(bodyNode["IPV4ADDRESS"])=="9.9.9.9,1.1.1.1" && bodyNode["DNSIPV4SERVERS"] is null && Catalog.Text(bodyNode["IPV6ADDRESS"])=="::,::","DNS read/write field mapping differs from APK.");
        Refuses(()=>Settings.Prepare(definition,"NETWORKDNS",baseline,new(){["DNSIPV4SERVERS"]="bad.address"}),"Invalid DNS accepted.");
        Refuses(()=>Settings.Prepare(definition,"NETWORKDNS",baseline,new(){["DYNAMICV4"]="2"}),"Invalid DNS mode accepted.");
        var profile=new CredentialRecord("unused","TESTSERIAL123","001122334455","192.168.7.1");
        using(var client=new RouterClient(profile,wire))
        {
            var result=await client.ApplyAsync(change);Assert(wire.Writes==1 && result.StartsWith("Saved and verified",StringComparison.Ordinal),"Save/read-back failed.");
        }
        var bridgeWire=new FakeWire();
        using(var client=new RouterClient(profile,bridgeWire))
        {
            var enabled=await client.ApplyAsync(bridge);
            Assert(bridgeWire.Writes==1 && enabled.StartsWith("Saved and verified") && Catalog.Text(bridgeWire.State["DEVICEOPERMODE_ALL"]["DEVICEOPERMODE"]!["MODE"])=="BRIDGE","Simulated bridge enable failed.");
            var restore=Settings.Prepare(mode,"DEVICEOPERMODE",bridgeWire.State["DEVICEOPERMODE_ALL"]["DEVICEOPERMODE"]!.AsObject(),new(){["MODE"]="DUAL"});
            var disabled=await client.ApplyAsync(restore);
            Assert(bridgeWire.Writes==2 && disabled.StartsWith("Saved and verified") && Catalog.Text(bridgeWire.State["DEVICEOPERMODE_ALL"]["DEVICEOPERMODE"]!["MODE"])=="DUAL","Simulated bridge disable failed.");
        }
        var stale=new FakeWire();stale.State["NETWORKDNS_ALL"]["NETWORKDNS"]!["DNSIPV4SERVERS"]="4.4.4.4,1.1.1.1";
        using(var client=new RouterClient(profile,stale))
        {
            bool refused=false;try{await client.ApplyAsync(change);}catch(RouterException){refused=true;}Assert(refused && stale.Writes==0,"Stale save was sent.");
        }
        var lost=new FakeWire{LoseWriteReply=true};
        using(var client=new RouterClient(profile,lost))
        {
            bool unknown=false;try{await client.ApplyAsync(change);}catch(RouterException ex){unknown=ex.Message.Contains("unknown");}Assert(unknown && lost.Writes==1,"A lost save reply caused a retry or false success.");
        }
        var wrong=new FakeWire();wrong.State["DEVICE_INFO_ALL"]["DEVICE_INFO"]!["SERIAL_KEY"]="WRONGSERIAL12";
        using(var client=new RouterClient(profile,wrong))
        {
            bool refused=false;try{await client.ApplyAsync(change);}catch(RouterException){refused=true;}Assert(refused && wrong.Writes==0,"Wrong target accepted a write.");
        }

#if !PORTABLE_TEST && !ANDROID
        Assert(ReleaseUpdater.IsNewer("v1.2.3",new Version(1,2,2),out _)&&!ReleaseUpdater.IsNewer("v1.2.3-beta",new Version(1,2,2),out _)&&!ReleaseUpdater.IsNewer("v1.2.1",new Version(1,2,2),out _),"Update version policy failed.");
        var hash=new string('A',64);Assert(ReleaseUpdater.ParseChecksum(hash+"  "+ReleaseUpdater.AssetName)==hash,"Checksum parsing failed.");
        Refuses(()=>ReleaseUpdater.ValidateDigest(hash,new string('B',64),null),"Corrupted update accepted.");
        Refuses(()=>ReleaseUpdater.ValidateDigest(hash,hash,"sha256:"+new string('B',64)),"GitHub digest mismatch accepted.");
        Refuses(()=>ReleaseUpdater.ValidateAsset(new Uri("https://github.com/other/repo/releases/download/v1.2.3/"+ReleaseUpdater.AssetName),"v1.2.3",ReleaseUpdater.AssetName),"Foreign repository accepted.");
        Refuses(()=>ReleaseUpdater.ParseChecksum(hash+"  "+ReleaseUpdater.AssetName+"\n"+hash+"  "+ReleaseUpdater.AssetName),"Ambiguous checksum accepted.");
        var directory=Path.Combine(Path.GetTempPath(),"selftest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var old=Path.Combine(directory,"old.exe");var candidate=Path.Combine(directory,"new.data");File.WriteAllText(old,"old fixture");File.WriteAllText(candidate,"new fixture");
            ReleaseUpdater.ReplaceFile(candidate,old,ReleaseUpdater.Hash(candidate));Assert(File.ReadAllText(old)=="new fixture" && Directory.EnumerateFiles(directory,"*.previous").Any(p=>File.ReadAllText(p)=="old fixture"),"Atomic replacement or backup failed.");
        }
        finally{foreach(var file in Directory.EnumerateFiles(directory))File.Delete(file);Directory.Delete(directory,false);}
#endif
    }
}
