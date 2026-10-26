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
            ["DEVICE_INFO_ALL"]=JsonNode.Parse("{\"STATUS\":\"OK\",\"DEVICE_INFO\":{\"SERIAL_KEY\":\"TESTSERIAL123\"}}")!.AsObject(),
            ["NETWORKDNS_ALL"]=JsonNode.Parse("{\"STATUS\":\"OK\",\"NETWORKDNS\":{\"DYNAMICV4\":\"0\",\"DNSIPV4SERVERS\":\"8.8.8.8,1.1.1.1\",\"DYNAMICV6\":\"1\",\"DNSIPV6SERVERS\":\"0.0.0.0,0.0.0.0\"}}")!.AsObject()};
        public int Writes;public bool LoseWriteReply;
        public Task<JsonObject> SendAsync(string path,string? body=null,CancellationToken token=default)
        {
            if(path=="/setData")
            {
                Writes++;if(LoseWriteReply)throw new HttpRequestException("simulated connection loss");
                var request=JsonNode.Parse(body!)!["REQUEST"]!;var payload=request["NETWORKDNS"]!.AsObject();var dns=State["NETWORKDNS_ALL"]["NETWORKDNS"]!.AsObject();
                foreach(var name in new[]{"DYNAMICV4","DYNAMICV6"})dns[name]=payload[name]!.DeepClone();
                dns["DNSIPV4SERVERS"]=payload["IPV4ADDRESS"]!.DeepClone();dns["DNSIPV6SERVERS"]=payload["IPV6ADDRESS"]!.DeepClone();
                return Task.FromResult(new JsonObject{["STATUS"]="OK"});
            }
            if(!path.StartsWith("/getData?key=",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected fake request");
            return Task.FromResult((JsonObject)State[path[13..]].DeepClone());
        }
        public void Dispose(){}
    }
    public static async Task RunAsync()
    {
        await CloudSelfTests.RunAsync();
        Assert(CredentialStore.IsPrivate("192.168.7.1")&&!CredentialStore.IsPrivate("8.8.8.8")&&!CredentialStore.IsPrivate("127.0.0.1"),"Local address boundary failed.");
        var login=RouterClient.LoginBody("a+b@example.com","P@ss word!é","abcxyz123_windows_test",123456);
        var expected=Convert.ToBase64String(Encoding.UTF8.GetBytes("a%2Bb%40example.com:P%40ss%20word!%C3%A9"));
        Assert(login==$"arg={expected}&_n=08292&_=123456&_uuid=abcxyz123_windows_test&_content=json","Native credential encoding differs from the verified protocol.");
        foreach(var response in new[]{"{\"RESPONSE\":{\"STATUS\":\"SessionTimeout\"}}","{\"RESPONSE\":{\"STATUS\":\"Login-OK\"}}","{\"RESPONSE\":{\"STATUS\":\"IncorrectCredentials\"}}","{\"RESPONSE\":{}}","<html>error</html>"})
            Refuses(()=>LocalWire.ParseResponse(Encoding.UTF8.GetBytes(response)),"Failed or login-only response accepted as protected data.");
        var secret=JsonNode.Parse("{\"STATUS\":\"OK\",\"WIFI\":{\"PASSWORD\":{\"value\":\"fixture-secret\"},\"SSIDNAME\":\"fixture network\"},\"TOKEN\":\"fixture-token\"}")!;
        var rows=Catalog.Flatten(secret,"FIXTURE");Assert(rows.Any(r=>r.Value=="fixture network") && rows.All(r=>!r.Value.Contains("fixture-secret")&&!r.Value.Contains("fixture-token")),"Secret branch redaction failed.");
        var mode=Settings.Definitions.Single(d=>d.Container=="DEVICEOPERMODE");
        Refuses(()=>Settings.Prepare(mode,"DEVICEOPERMODE",new JsonObject{["MODE"]="DUAL"},new(){["MODE"]="BRIDGE"}),"Bridge enable must be blocked.");
        foreach(var body in new[]{"{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"DEVICEOPERMODE\":{\"MODE\":\"BRIDGE\"}}}","{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"UPDATE\":{\"CHECKNOW\":\"CheckNowDownloadInstall\"}}}","{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"NETWORKMISC\":{\"ADMIN_PASSWORD\":\"x\"}}}"})
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
        Assert(ReleaseUpdater.IsNewer("v1.2.3",new Version(1,2,2),out _)&&!ReleaseUpdater.IsNewer("v1.2.3-beta",new Version(1,2,2),out _)&&!ReleaseUpdater.IsNewer("v1.2.1",new Version(1,2,2),out _),"Update version policy failed.");
        var hash=new string('A',64);Assert(ReleaseUpdater.ParseChecksum(hash+"  "+ReleaseUpdater.AssetName)==hash,"Checksum parsing failed.");
        Refuses(()=>ReleaseUpdater.ValidateDigest(hash,new string('B',64),null),"Corrupted update accepted.");
        Refuses(()=>ReleaseUpdater.ValidateDigest(hash,hash,"sha256:"+new string('B',64)),"GitHub digest mismatch accepted.");
        Refuses(()=>ReleaseUpdater.ValidateAsset(new Uri("https://github.com/other/repo/releases/download/v1.2.3/"+ReleaseUpdater.AssetName),"v1.2.3",ReleaseUpdater.AssetName),"Foreign repository accepted.");
        Refuses(()=>ReleaseUpdater.ParseChecksum(hash+"  "+ReleaseUpdater.AssetName+"\n"+hash+"  "+ReleaseUpdater.AssetName),"Ambiguous checksum accepted.");
        var directory=Path.Combine(AppContext.BaseDirectory,"selftest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var old=Path.Combine(directory,"old.exe");var candidate=Path.Combine(directory,"new.data");File.WriteAllText(old,"old fixture");File.WriteAllText(candidate,"new fixture");
            ReleaseUpdater.ReplaceFile(candidate,old,ReleaseUpdater.Hash(candidate));Assert(File.ReadAllText(old)=="new fixture" && Directory.EnumerateFiles(directory,"*.previous").Any(p=>File.ReadAllText(p)=="old fixture"),"Atomic replacement or backup failed.");
        }
        finally{foreach(var file in Directory.EnumerateFiles(directory))File.Delete(file);Directory.Delete(directory,false);}
    }
}
