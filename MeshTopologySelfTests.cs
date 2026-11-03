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
        // Officially provisioned satellites can be absent from AUTO_WOBA discovery.
        // Reproduce native HNC's WAN/device MAC aliases and the user's actual rate values.
        var native=Fixture(false);native.Data.Remove("AUTO_WOBA_SAT_STATUS_ALL");
        native.Data["NETWORKMAP_ALL"]=Obj("""{"HNC":{"data":{"type":"device","deviceType":"gateway","serialNum":"TESTSERIAL123","WANMACAddr":"00:11:22:33:44:55"},"children":[{"data":{"type":"interface","ifType":"WIFI-BACKHAUL"},"children":[{"text":"Existing satellite","data":{"type":"device","deviceType":"extender","serialNum":"SATELLITE001","WANMACAddr":"00:11:22:33:44:66","MACAddr":"00:11:22:33:44:67","IPAddr":"192.168.7.2","OBComplete":"true"},"children":[{"data":{"type":"device","deviceType":"unknown","MACAddr":"AA:BB:CC:DD:EE:FF","IPAddr":"192.168.7.20"}}]}]}]}}""");
        native.Data["LINKSPEEDBH_ALL"]=Obj("""{"LINKSPEEDBH":[{"ACTIVE":1,"STAADDRESS":"00:11:22:33:44:67","MACADDRESS":"00:11:22:33:44:67","DOWNLINKRATE":2882350,"UPLINKRATE":2594110,"SIGNALSTRENGTH":-54,"TXRATE":8,"RXRATE":9}]}""");
        var nativeMesh=MeshTopology.Parse(native,record);
        Check(nativeMesh.Routers.Count==2 && nativeMesh.Routers[1].Mac=="001122334466" && nativeMesh.Routers[1].Address=="192.168.7.2","Native HNC identity/IP aliases or client exclusion failed.");
        Check(nativeMesh.Links.Count==1 && nativeMesh.Links[0].Down=="2.88 Gbps" && nativeMesh.Links[0].Up=="2.59 Gbps" && nativeMesh.Links[0].Signal=="-54 dBm","Native HNC MAC aliases failed to correlate the reported active rates.");
        var savedNames=new Dictionary<string,string>();
        var otherNetwork=record with{Serial="OTHERSERIAL123",Mac="AABBCCDDEEFF"};
        var networkLabels=new Dictionary<string,string>{[SavedRouterLabels.Key(record)]="Home",[SavedRouterLabels.Key(otherNetwork)]="Parents"};
        var changedAddress=record with{Path="new-credential-version",Address="192.168.50.1",Created=DateTimeOffset.UtcNow};
        Check(SavedRouterLabels.Name(changedAddress,key=>networkLabels.GetValueOrDefault(key))=="Home" &&
            SavedRouterLabels.Name(otherNetwork,key=>networkLabels.GetValueOrDefault(key))=="Parents","Saved-network names followed IP/profile path or mixed routers sharing an IP.");
        networkLabels.Remove(SavedRouterLabels.Key(record));
        Check(SavedRouterLabels.Name(record,key=>networkLabels.GetValueOrDefault(key))==record.Serial &&
            SavedRouterLabels.Name(otherNetwork,key=>networkLabels.GetValueOrDefault(key))=="Parents","Clearing one saved-network label affected another router.");
        foreach(var key in MeshNodeLabels.Keys(nativeMesh.Routers[0]))savedNames[key]=MeshNodeLabels.Validate(" Living room ");
        foreach(var key in MeshNodeLabels.Keys(nativeMesh.Routers[1]))savedNames[key]=MeshNodeLabels.Validate("Upstairs 🛜");
        var named=MeshNodeLabels.Apply(nativeMesh,key=>savedNames.GetValueOrDefault(key));
        Check(named.Routers[0].Name=="Living room" && named.Routers[1].Name=="Upstairs 🛜" && named.Links.SequenceEqual(nativeMesh.Links),"Custom node names changed identities/rates or were not applied separately.");
        var moved=nativeMesh with{Routers=nativeMesh.Routers.Reverse().Select(n=>n with{Address="192.168.50.2",Name="Reported new name"}).ToArray()};
        Check(MeshNodeLabels.Apply(moved,key=>savedNames.GetValueOrDefault(key)).Routers.First(n=>n.Id=="SATELLITE001").Name=="Upstairs 🛜","A node name was tied to IP address, node position or the reported name.");
        foreach(var key in MeshNodeLabels.Keys(nativeMesh.Routers[1]))savedNames.Remove(key);
        var reset=MeshNodeLabels.Apply(nativeMesh,key=>savedNames.GetValueOrDefault(key));
        Check(reset.Routers[1].Name=="Existing satellite" && reset.Routers[0].Name=="Living room","Resetting one custom name changed another node or failed to restore its reported name.");
        foreach(var invalidName in new[]{new string('x',49),"Room\nUpstairs"})
        {
            var refused=false;try{MeshNodeLabels.Validate(invalidName);}catch(RouterException){refused=true;}
            Check(refused,"An overlong/multiline node label was accepted.");
        }
        var unrelated=native.Data["LINKSPEEDBH_ALL"]["LINKSPEEDBH"]![0]!;
        unrelated["STAADDRESS"]="AA:BB:CC:DD:EE:FF";unrelated["MACADDRESS"]="AA:BB:CC:DD:EE:FF";
        var unmatched=MeshTopology.Parse(native,record);
        Check(unmatched.Links.All(l=>l.Down=="Not reported"),"An unrelated client rate was assigned to the only satellite by elimination.");
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
        var target=new CloudDevice("W30","SATELLITE001","001122334466");
        var selectedAt=DateTimeOffset.UtcNow.AddMinutes(-1);
        Snapshot WithReads(Snapshot value)=>value with{Reads=value.Data.Keys.Select(k=>new ReadResult(k,200,"OK",value.Time,true)).ToList()};
        var joined=WithReads(Fixture());
        Check(SatelliteJoin.Verify(joined,record,target,selectedAt).Joined,"Fresh matching topology/backhaul did not verify the selected satellite.");
        Check(!SatelliteJoin.Verify(WithReads(Fixture(false)),record,target,selectedAt).Joined,"Discovery plus an association was mistaken for joined topology.");
        var inactive=WithReads(Fixture());inactive.Data["LINKSPEEDBH_ALL"]["LINKSPEEDBH"]![0]!["ACTIVE"]="false";
        Check(!SatelliteJoin.Verify(inactive,record,target,selectedAt).Joined,"Inactive backhaul was shown as verified.");
        var clientOnly=WithReads(Fixture());clientOnly.Data["NETWORKMAP_ALL"]["HNC"]!["children"]![0]!["children"]![0]!["data"]!["deviceType"]="client";
        Check(!SatelliteJoin.Verify(clientOnly,record,target,selectedAt).Joined,"A client node became a verified satellite.");
        Check(!SatelliteJoin.Verify(joined,record,target,DateTimeOffset.UtcNow.AddMinutes(1)).Joined,"A stale snapshot verified a newly selected satellite.");
        Check(!SatelliteJoin.Verify(joined with{Reads=[]},record,target,selectedAt).Joined,"Unread/unavailable topology was treated as confirmed.");
        Check(!SatelliteJoin.Verify(joined,record,new("W30",record.Serial,record.Mac),selectedAt).Joined,"The controller was selected as its own new satellite.");
        var conflict=WithReads(Fixture());conflict.Data["AUTO_WOBA_SAT_STATUS_ALL"]["AUTO_WOBA_SAT_STATUS"]!["SAT_LIST"]![0]!["MAX_WAN_MAC"]="001122334499";
        Check(!SatelliteJoin.Verify(conflict,record,target,selectedAt).Joined,"Conflicting identity verified a satellite.");
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
