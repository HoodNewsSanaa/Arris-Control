using System.Text;
using System.Text.Json.Nodes;
namespace ArrisControl;
internal static class MeshTopologySelfTests
{
    static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static JsonObject Obj(string text)=>JsonNode.Parse(text)!.AsObject();
    static Snapshot Fixture(bool topology=true)
    {
        var data=new Dictionary<string,JsonObject>{["AUTO_WOBA_SAT_STATUS_ALL"]=Obj("""{"AUTO_WOBA_SAT_STATUS":{"ENABLE":"0","AGENT_TIMEOUT":"180","SAT_LIST":[{"MAX_SERIAL_NO":"SATELLITE001","MAX_WAN_MAC":"00:11:22:33:44:66","MAX_WiFi_MAC_BH":"00:11:22:33:44:67","MAX_MODEL_NO":"W30"},{"MAX_SERIAL_NO":"SATELLITE002","MAX_WAN_MAC":"00:11:22:33:44:77","MAX_MODEL_NO":"W30"}]}}""")};
        if(topology)data["NETWORKMAP_ALL"]=Obj("""{"HNC":{"text":"Main","data":{"type":"device","deviceType":"router","serialNum":"TESTSERIAL123"},"children":[{"text":"WiFi backhaul","data":{"type":"interface"},"children":[{"text":"Upstairs","data":{"type":"device","deviceType":"extender","serialNum":"SATELLITE001"},"children":[{"text":"Phone","data":{"type":"device","deviceType":"client","macAddress":"AABBCCDDEEFF"}}]}]}]}}""");
        data["LINKSPEEDBH_ALL"]=Obj("""{"LINKSPEEDBH":[{"ACTIVE":"true","STAADDRESS":"00:11:22:33:44:67","DOWNLINKRATE":"2402000","UPLINKRATE":"2161000","SIGNALSTRENGTH":"-56","TXRATE":"999999"},{"ACTIVE":"false","STAADDRESS":"00:11:22:33:44:77","DOWNLINKRATE":"9999000"}]}""");
        return new(Catalog.Pages.First(p=>p.Id=="mesh"),data,[],[],DateTimeOffset.UtcNow);
    }
    public static async Task RunAsync()
    {
        var record=new CredentialRecord("unused","TESTSERIAL123","001122334455","192.168.7.1");
        var mesh=MeshTopology.Parse(Fixture(),record);
        Check(mesh.Routers.Count==3 && mesh.Routers.All(n=>n.Name!="Phone"),"Client became a mesh router or a router duplicated.");
        Check(mesh.Links.Count==1 && mesh.Links[0].Down=="2.4 Gbps" && mesh.Links[0].Up=="2.16 Gbps" && mesh.Links[0].Signal=="-56 dBm","MAC-correlated rates or inactive-link handling failed.");
        Check(mesh.Describe(mesh.Routers[2]).Contains("not reported"),"An unknown upstream link was invented.");
        Check(MeshTopology.Rate("0")=="Not reported" && MeshTopology.Rate("bad")=="Not reported" && MeshTopology.Rate("100000")=="100 Mbps","Rate units/unknown handling failed.");
        var anonymous=Fixture(false);anonymous.Data.Remove("LINKSPEEDBH_ALL");
        Check(MeshTopology.Parse(anonymous,record).Links.Count==0,"Satellite table was mistaken for an active topology.");
        var ambiguous=Fixture(false);var list=ambiguous.Data["AUTO_WOBA_SAT_STATUS_ALL"]["AUTO_WOBA_SAT_STATUS"]!["SAT_LIST"]!.AsArray();
        list[1]!["MAX_WiFi_MAC_BH"]="00:11:22:33:44:67";
        Check(MeshTopology.Parse(ambiguous,record).Links.Count==0,"Ambiguous backhaul MAC was assigned to a node.");
        var discovery=Settings.Definitions.Single(d=>d.Container=="AUTO_WOBA");
        var baseline=Settings.Locate(Fixture().Data["AUTO_WOBA_SAT_STATUS_ALL"],"AUTO_WOBA",0,"")!;
        var plan=Settings.Prepare(discovery,"AUTO_WOBA",baseline,new(){["ENABLE"]="1",["TIMEOUT"]="240"});
        var request=JsonNode.Parse(plan.Body)!["REQUEST"]!["AUTO_WOBA"]!;
        Check(Catalog.Text(request["TIMEOUT"])=="240" && request["AGENT_TIMEOUT"] is null && Settings.VerifyValues(Settings.Locate(Obj("""{"AUTO_WOBA_SAT_STATUS":{"ENABLE":"1","AGENT_TIMEOUT":"240"}}"""),"AUTO_WOBA",0,""),plan.Expected),"Discovery write/read alias mapping failed.");
        var rejected=false;try{Settings.Prepare(discovery,"AUTO_WOBA",baseline,new(){["TIMEOUT"]="3601"});}catch(RouterException){rejected=true;}
        Check(rejected,"Unbounded discovery timeout accepted.");
        Check(Catalog.Text(LocalWire.ParseResponse(Encoding.UTF8.GetBytes("{\"RESPONSE\":{\"STATUS\":\"SUCCESS\"}}"),write:true)["STATUS"])=="SUCCESS","Vendor write acknowledgement rejected.");
        rejected=false;try{LocalWire.ParseResponse(Encoding.UTF8.GetBytes("{\"RESPONSE\":{\"STATUS\":\"SUCCESS\"}}"));}catch(RouterException){rejected=true;}Check(rejected,"Write-only acknowledgement accepted as a protected read.");
        Console.WriteLine("PASS: mesh identity correlation, client exclusion, unknown/ambiguous links, kbps conversion, discovery aliases/bounds and write-only acknowledgement. No network requests.");
        using(var wire=new DiscoveryWire())using(var client=new RouterClient(record,wire))
        {
            var section=new SettingSection(discovery,"AUTO_WOBA",0,"",baseline,"Discovery");
            var pending=await client.PrepareAsync(section,new(){["ENABLE"]="1",["TIMEOUT"]="240"});
            var message=await client.ApplyAsync(pending);
            Check(wire.Writes==1 && message.Contains("Saved and verified"),"Discovery did not complete one write with mapped read-back.");
        }
        using(var wire=new DiscoveryWire{LoseReply=true})using(var client=new RouterClient(record,wire))
        {
            var section=new SettingSection(discovery,"AUTO_WOBA",0,"",baseline,"Discovery");
            var pending=await client.PrepareAsync(section,new(){["ENABLE"]="1"});
            var unknown=false;try{await client.ApplyAsync(pending);}catch(RouterException ex){unknown=ex.Message.Contains("unknown");}
            Check(wire.Writes==1 && unknown,"An uncertain discovery write was retried or reported successful.");
        }
    }
    sealed class DiscoveryWire : IRouterWire
    {
        public int Writes;public bool LoseReply;
        readonly JsonObject state=Obj("""{"STATUS":"OK","AUTO_WOBA_SAT_STATUS":{"ENABLE":"0","AGENT_TIMEOUT":"180"}}""");
        public Task<JsonObject> SendAsync(string path,string? body=null,CancellationToken token=default)
        {
            if(path=="/setData")
            {
                Writes++;if(LoseReply)throw new System.Net.Http.HttpRequestException("Fictional lost acknowledgement");
                var value=JsonNode.Parse(body!)!["REQUEST"]!["AUTO_WOBA"]!;
                state["AUTO_WOBA_SAT_STATUS"]!["ENABLE"]=value["ENABLE"]!.DeepClone();
                state["AUTO_WOBA_SAT_STATUS"]!["AGENT_TIMEOUT"]=value["TIMEOUT"]!.DeepClone();
                return Task.FromResult(Obj("""{"STATUS":"SUCCESS"}"""));
            }
            if(path.EndsWith("DEVICE_INFO_ALL",StringComparison.Ordinal))return Task.FromResult(Obj("""{"DEVICE_INFO":{"SERIAL_KEY":"TESTSERIAL123"}}"""));
            if(path.EndsWith("AUTO_WOBA_SAT_STATUS_ALL",StringComparison.Ordinal))return Task.FromResult(state.DeepClone().AsObject());
            throw new InvalidOperationException("Unexpected mesh fixture request");
        }
        public void Dispose(){}
    }

}
