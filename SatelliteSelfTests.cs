using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
namespace ArrisControl;

internal static class SatelliteSelfTests
{
    static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static void Refuses(Action action,string message){try{action();}catch(RouterException){return;}throw new InvalidOperationException(message);}
    static async Task RefusesAsync(Func<Task> action,string message){try{await action();}catch(RouterException){return;}throw new InvalidOperationException(message);}

    const string MainSerial="TESTSERIAL123",SatSerial="SATELLITE001",SatIp="192.168.0.20";
    static readonly SatelliteTarget Target=new(SatSerial,"001122334466",SatIp,"Upstairs");

    /// <summary>Fictional main router that answers forwarded reads for one satellite.</summary>
    sealed class ForwardingWire : IRouterWire
    {
        public string SatelliteSerial=SatSerial;public int Writes;public bool LoseWriteReply;
        public readonly List<string> Bodies=[];
        public JsonObject Led=new(){["CONTAINER"]="LEDRING",["ENABLE"]="1",["BRIGHTNESS"]="50"};
        public Task<JsonObject> SendAsync(string path,string? body=null,CancellationToken token=default)
        {
            Check(LocalWire.Allows(path,body),"Test client sent a request outside the transport allowlist: "+path);
            if(body is not null)
            {
                Settings.ValidateEnvelope(body);Bodies.Add(body);Writes++;
                if(LoseWriteReply)throw new HttpRequestException("lost reply");
                var request=JsonNode.Parse(body)!["REQUEST"]!.AsObject();
                if(request["LEDRING"] is JsonObject led)foreach(var kv in led)Led[kv.Key]=kv.Value?.DeepClone();
                return Task.FromResult(new JsonObject{["STATUS"]="SUCCESS"});
            }
            if(path=="/getData?key=DEVICE_INFO_ALL")return Task.FromResult(new JsonObject{["STATUS"]="OK",["DEVICE_INFO"]=new JsonObject{["SERIAL_KEY"]=MainSerial}});
            var forwarded=System.Text.RegularExpressions.Regex.Match(path,"key=([A-Z_]+)&ip=(.+)$");
            Check(forwarded.Success && forwarded.Groups[2].Value==SatIp,"Unexpected read "+path);
            JsonObject reply=forwarded.Groups[1].Value switch
            {
                "DEVICE_INFO_ALL"=>new(){["HNEIPADDRESS"]=SatIp,["DEVICE_INFO"]=new JsonObject{["SERIAL_KEY"]=SatelliteSerial,["MODEL_NAME"]="W30"}},
                "LEDRING_ALL"=>new(){["LEDRING"]=Led.DeepClone()},
                _=>new(){["FIRMWARE"]=new JsonObject{["VERSION"]="AXR.0814"}}
            };
            reply["STATUS"]="OK";
            return Task.FromResult(reply);
        }
        public void Dispose(){}
    }

    /// <summary>Fictional satellite mesh agent.</summary>
    sealed class FakeAgent : IMeshAgentWire
    {
        public string Serial=SatSerial,DfsValue="2";public int Puts;public bool Refuse;public bool LoseReply;public bool Ignore;
        public readonly List<string> Bodies=[];
        public Task<byte[]> SendAsync(HttpMethod method,string body,CancellationToken token)
        {
            Bodies.Add(body);
            if(method==HttpMethod.Put)
            {
                Puts++;
                Check(body==MeshAgentProtocol.DfsBody(false) || body==MeshAgentProtocol.DfsBody(true),"Mesh write outside the DFS allowlist.");
                if(Refuse)throw new RouterException("Satellite mesh agent returned HTTP 400.","HTTP400");
                if(LoseReply)throw new HttpRequestException("lost");
                if(!Ignore)DfsValue=body.Contains(">0<")?"0":"2";
                return Task.FromResult(Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"US-ASCII\"?><X_ARRIS_COM><tr181i2></tr181i2></X_ARRIS_COM>"));
            }
            var xml=$"<?xml version=\"1.0\" encoding=\"US-ASCII\"?>\n<X_ARRIS_COM>\n<tr181i2>\n<{MeshAgentProtocol.Serial}>{Serial}</{MeshAgentProtocol.Serial}>\n<{MeshAgentProtocol.Dfs}>{DfsValue}</{MeshAgentProtocol.Dfs}>\n<{MeshAgentProtocol.Channel}>{(DfsValue=="0"?"36":"100")}</{MeshAgentProtocol.Channel}>\n<{MeshAgentProtocol.Bandwidth}>{(DfsValue=="0"?"80MHz":"160MHz")}</{MeshAgentProtocol.Bandwidth}>\n<{MeshAgentProtocol.AutoChannel}>true</{MeshAgentProtocol.AutoChannel}>\n</tr181i2>\n</X_ARRIS_COM>";
            return Task.FromResult(Encoding.ASCII.GetBytes(xml));
        }
        public void Dispose(){}
    }

    public static async Task RunAsync()
    {
        // Forwarded reads: only the firmware's forwarded keys, only private literal IPv4 addresses.
        Check(SatelliteForwarding.ReadPath("LEDRING_ALL",SatIp)=="/getData?key=LEDRING_ALL&ip=192.168.0.20","Forwarded read path changed.");
        Check(LocalWire.Allows("/getData?key=FIRMWARE_ALL&ip=10.0.0.5",null),"A forwarded firmware read was refused.");
        foreach(var path in new[]{"/getData?key=NETWORKDNS_ALL&ip=192.168.0.20","/getData?key=LEDRING_ALL&ip=8.8.8.8","/getData?key=LEDRING_ALL&ip=192.168.0.20&x=1",
            "/getData?key=LEDRING_ALL&ip=192.168.000.020","/getData?key=WIFI_FH_DFS_ALL&ip=192.168.0.20","/getData?key=LEDRING_ALL&ip=0.0.0.0"})
            Check(!LocalWire.Allows(path,null),"Transport admitted "+path);
        Refuses(()=>SatelliteForwarding.ReadPath("NETWORKDNS_ALL",SatIp),"DNS was readable as a satellite feature.");

        // Forwarded writes: light ring/status light only; reboot fixed to SIMPLE_RESET.
        var reboot=SatelliteForwarding.Reboot(SatIp);
        Check(reboot=="{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"HNEIPADDRESS\":\"192.168.0.20\",\"DEVICERESET\":{\"RESETMODE\":\"SIMPLE_RESET\",\"DORESET\":\"true\"}}}","Restart body changed.");
        foreach(var mode in new[]{"FACTORY_RESET","WIFI_RESET","Device",""})
            Refuses(()=>Settings.ValidateEnvelope(reboot.Replace("SIMPLE_RESET",mode)),"Reset mode "+mode+" was admitted.");
        Refuses(()=>Settings.ValidateEnvelope("{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"HNEIPADDRESS\":\"192.168.0.20\",\"WIFI_FH_DFS\":{\"ENABLE\":\"false\"}}}"),"DFS was forwarded through the main router.");
        Refuses(()=>Settings.ValidateEnvelope("{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"HNEIPADDRESS\":\"192.168.0.20\",\"NETWORKDNS\":{\"DYNAMICV4\":\"1\"}}}"),"DNS was forwarded to a satellite.");
        Refuses(()=>Settings.ValidateEnvelope("{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"HNEIPADDRESS\":\"1.2.3.4\",\"LEDRING\":{\"BRIGHTNESS\":\"10\"}}}"),"A public forwarding address was admitted.");
        Refuses(()=>Settings.ValidateEnvelope("{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"HNEIPADDRESS\":\"192.168.0.20\",\"LEDRING\":{\"PASSWORD\":\"x\"}}}"),"An unknown light-ring field was forwarded.");
        Refuses(()=>Settings.ValidateEnvelope("{\"REQUEST\":{\"OPERTYPE\":\"DELETE\",\"HNEIPADDRESS\":\"192.168.0.20\",\"LEDRING\":{\"BRIGHTNESS\":\"10\"}}}"),"A non-MODIFY satellite operation was admitted.");
        Refuses(()=>Settings.ValidateEnvelope(reboot.Replace("\"DORESET\":\"true\"","\"DORESET\":\"true\",\"EXTRA\":\"1\"")),"An extra reset field was admitted.");
        Check(SatelliteTarget.From(new MeshRouter(SatSerial,"Upstairs","W30",SatIp,"001122334466","",false))==Target,"Mesh node to satellite target changed.");
        Check(SatelliteTarget.From(new MeshRouter(MainSerial,"Main","",SatIp,"001122334455","",true)) is null && SatelliteTarget.From(new MeshRouter(SatSerial,"x","","","","",false)) is null,
            "The main router or an address-less node became a satellite target.");

        var record=new CredentialRecord("unused",MainSerial,"001122334455","192.168.0.1");
        var ledDef=Settings.Definitions.Single(d=>d.Container=="LEDRING");
        // Read page, preview and one verified forwarded save.
        var wire=new ForwardingWire();
        using(var client=new RouterClient(record,wire))
        {
            var page=await client.ReadSatellitePageAsync(Target);
            Check(page.Reads.Count==SatelliteForwarding.ReadKeys.Length && page.Reads.All(r=>r.Readable),"Satellite page reads failed.");
            var section=Settings.Sections(page).Single(s=>s.Container=="LEDRING");
            var plan=await client.PrepareSatelliteAsync(Target,section,new(){["BRIGHTNESS"]="20"});
            Check(plan.Body.Contains("\"HNEIPADDRESS\":\"192.168.0.20\"") && plan.Preview.Contains(SatSerial),"Satellite preview lost its target.");
            var result=await client.ApplySatelliteAsync(Target,plan);
            Check(wire.Writes==1 && result.StartsWith("Saved on the satellite and verified",StringComparison.Ordinal),"Forwarded light-ring save was not verified exactly once.");
            await RefusesAsync(()=>client.ApplySatelliteAsync(Target with{Address="192.168.0.21"},plan),"A plan was applied to a different satellite.");
            await client.RebootSatelliteAsync(Target);
            Check(wire.Writes==2 && wire.Bodies[^1]==reboot,"Restart was not sent exactly once with the fixed body.");
        }
        // Wrong satellite identity: nothing is written.
        var impostor=new ForwardingWire{SatelliteSerial="SATELLITE999"};
        using(var client=new RouterClient(record,impostor))
        {
            await RefusesAsync(()=>client.RebootSatelliteAsync(Target),"A restart was sent to a satellite with a different serial.");
            await RefusesAsync(()=>client.SetSatelliteDfsAsync(Target,false,new FakeAgent()),"A DFS change proceeded after a forwarded serial mismatch.");
            Check(impostor.Writes==0,"A write was sent after an identity mismatch.");
        }
        // Lost reply: reported as unknown, never retried.
        var lost=new ForwardingWire{LoseWriteReply=true};
        using(var client=new RouterClient(record,lost))
        {
            await RefusesAsync(()=>client.RebootSatelliteAsync(Target),"A lost restart reply was reported as success.");
            Check(lost.Writes==1,"A lost restart was retried.");
        }

        // Mesh agent DFS: exact XML, identity over the agent itself, one PUT, read-back.
        Check(MeshAgentProtocol.DfsBody(false)=="<?xml version=\"1.0\" encoding=\"US-ASCII\"?><X_ARRIS_COM><tr181i2><Device.WiFi.Radio.2.X_ARRIS_COM.DFS>0</Device.WiFi.Radio.2.X_ARRIS_COM.DFS></tr181i2></X_ARRIS_COM>","DFS off body changed.");
        Check(MeshAgentProtocol.DfsBody(true).Contains(">2<"),"DFS on did not use the stock value 2.");
        Refuses(()=>MeshAgentProtocol.ReadBody(["Device.Users.User.1.Password"]),"A mesh read outside the allowlist was built.");
        Refuses(()=>MeshAgentProtocol.Parse(Encoding.ASCII.GetBytes("<!DOCTYPE x [<!ENTITY a \"b\">]><X_ARRIS_COM><tr181i2/></X_ARRIS_COM>"),MeshAgentProtocol.ReadParams),"A DTD was parsed.");
        Refuses(()=>MeshAgentProtocol.Parse(Encoding.ASCII.GetBytes("<X_ARRIS_COM><tr181i2><Device.Other>1</Device.Other></tr181i2></X_ARRIS_COM>"),MeshAgentProtocol.ReadParams),"An unrequested parameter was accepted.");
        Check(MeshAgentProtocol.DfsEnabled("false")==false && MeshAgentProtocol.DfsEnabled("2")==true && MeshAgentProtocol.DfsEnabled("x") is null,"DFS value mapping changed.");

        var agent=new FakeAgent();
        using(var client=new RouterClient(record,new ForwardingWire()))
        {
            var radio=await client.ReadSatelliteRadioAsync(Target,agent);
            Check(radio.Dfs==true && radio.Bandwidth=="160MHz","Satellite radio read failed.");
            var message=await client.SetSatelliteDfsAsync(Target,false,agent);
            Check(agent.Puts==1 && agent.DfsValue=="0" && message.StartsWith("Satellite DFS off verified",StringComparison.Ordinal),"Satellite DFS off was not one verified write: "+message);
            var again=await client.SetSatelliteDfsAsync(Target,false,agent);
            Check(agent.Puts==1 && again.Contains("Nothing was sent"),"An unchanged DFS state was written again.");
        }
        var refusing=new FakeAgent{Refuse=true};
        using(var client=new RouterClient(record,new ForwardingWire()))
        {
            await RefusesAsync(()=>client.SetSatelliteDfsAsync(Target,false,refusing),"A refused DFS change was reported as success.");
            Check(refusing.Puts==1,"A refused DFS change was retried.");
            var lostAgent=new FakeAgent{LoseReply=true};
            await RefusesAsync(()=>client.SetSatelliteDfsAsync(Target,false,lostAgent),"A lost DFS reply was reported as success.");
            Check(lostAgent.Puts==1,"A lost DFS write was retried.");
            var wrong=new FakeAgent{Serial="SATELLITE999"};
            await RefusesAsync(()=>client.SetSatelliteDfsAsync(Target,false,wrong),"DFS was written to an agent reporting another serial.");
            Check(wrong.Puts==0,"A DFS write reached a device with another serial.");
            var ignoring=new FakeAgent{Ignore=true};
            var unverified=await client.SetSatelliteDfsAsync(Target,false,ignoring);
            Check(ignoring.Puts==1 && unverified.Contains("still reports"),"An unapplied DFS change was reported as verified.");
        }
        Console.WriteLine("PASS: satellite forwarding allowlist, restart-only reset, identity checks, verified forwarded save, mesh-agent DFS XML, one-shot write and read-back. No network requests.");
    }
}
