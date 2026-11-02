using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArrisControl;

internal sealed record MeshRouter(string Id,string Name,string Model,string Address,string Mac,string BackhaulMac,bool Controller);
internal sealed record MeshBackhaul(string Parent,string Child,string Kind,string Down,string Up,string Signal,string Source);
internal sealed record MeshTopology(IReadOnlyList<MeshRouter> Routers,IReadOnlyList<MeshBackhaul> Links,DateTimeOffset Time)
{
    internal static string Mac(string value)=>Regex.Replace(value.ToUpperInvariant(),"[^A-F0-9]","") is var m && m.Length==12?m:"";
    static string Get(JsonObject? obj,params string[] keys)
    {
        if(obj is null)return "";
        foreach(var key in keys){var value=obj.FirstOrDefault(k=>k.Key.Equals(key,StringComparison.OrdinalIgnoreCase)).Value;if(value is JsonValue)return Catalog.Text(value);}
        return "";
    }
    static IEnumerable<JsonObject> Rows(JsonNode? value)=>value is JsonArray a?a.OfType<JsonObject>():value is JsonObject o?[o]:[];
    public static string Rate(string raw)
    {
        // ARRIS DOWNLINKRATE / UPLINKRATE map to TR-181 LastData{Down,Up}linkRate (kbit/s).
        if(!decimal.TryParse(raw,NumberStyles.Number,CultureInfo.InvariantCulture,out var kbps) || kbps<=0 || kbps>100_000_000)return "Not reported";
        return kbps>=1_000_000?(kbps/1_000_000).ToString("0.##",CultureInfo.InvariantCulture)+" Gbps":(kbps/1000).ToString("0.##",CultureInfo.InvariantCulture)+" Mbps";
    }
    public static MeshTopology Parse(Snapshot snapshot,CredentialRecord controller)
    {
        var nodes=new List<MeshRouter>{new(controller.Serial,"Main router","",controller.Address,controller.Mac,"",true)};
        var edges=new List<MeshBackhaul>();
        snapshot.Data.TryGetValue("AUTO_WOBA_SAT_STATUS_ALL",out var auto);
        foreach(var sat in Rows(auto?["AUTO_WOBA_SAT_STATUS"]?["SAT_LIST"]).Take(127))
        {
            var id=Get(sat,"MAX_SERIAL_NO");var mac=Mac(Get(sat,"MAX_WAN_MAC"));var bh=Mac(Get(sat,"MAX_WiFi_MAC_BH"));
            if(id.Length==0)id=mac.Length>0?mac:bh;
            if(id.Length==0 || nodes.Any(n=>n.Id==id || mac.Length>0 && n.Mac==mac))continue;
            nodes.Add(new(id,"Satellite "+nodes.Count,Get(sat,"MAX_MODEL_NO"),Get(sat,"IPADDRESS","IP"),mac,bh,false));
        }
        MeshRouter? Find(string serial,string mac)=>nodes.FirstOrDefault(n=>serial.Length>0 && n.Id==serial || mac.Length>0 && (n.Mac==mac || n.BackhaulMac==mac));
        void Walk(JsonObject obj,MeshRouter? parent,string kind,int depth,bool root=false)
        {
            if(depth>16 || nodes.Count>=128)return;
            var data=obj["data"] as JsonObject??obj;
            var serial=Get(data,"serialNum","serialNumber");var mac=Mac(Get(data,"macAddress","wanMac","mac"));
            var deviceType=Get(data,"deviceType").ToLowerInvariant();
            var router=root?nodes[0]:Find(serial,mac);
            // Interface and client nodes must never become satellite routers.
            if(router is null && deviceType is "router" or "extender" or "satellite" or "hne" or "hnc")
            {
                var id=serial.Length>0?serial:mac;
                if(id.Length>0){router=new(id,Get(obj,"text").Length>0?Get(obj,"text"):"Satellite "+nodes.Count,Get(data,"modelName","modelNumber"),Get(data,"ipAddress","ip"),mac,Mac(Get(data,"backhaulMac")),false);nodes.Add(router);}
            }
            if(router is not null)
            {
                var name=Get(obj,"text");var model=Get(data,"modelName","modelNumber");var ip=Get(data,"ipAddress","ip");
                var updated=router with{Name=name.Length>0?name:router.Name,Model=model.Length>0?model:router.Model,Address=ip.Length>0?ip:router.Address};
                nodes[nodes.IndexOf(router)]=updated;router=updated;
                if(parent is not null && parent.Id!=router.Id && !edges.Any(e=>e.Parent==parent.Id && e.Child==router.Id))
                    edges.Add(new(parent.Id,router.Id,kind,"Not reported","Not reported","Not reported","NETWORKMAP_ALL"));
                parent=router;kind="Not reported";
            }
            if(Get(data,"type").Equals("interface",StringComparison.OrdinalIgnoreCase))
            {
                var text=Get(data,"interfaceType","ifType","typeName","medium")+" "+Get(obj,"text");
                if(text.Contains("ethernet",StringComparison.OrdinalIgnoreCase))kind="Ethernet";
                else if(text.Contains("wifi",StringComparison.OrdinalIgnoreCase)||text.Contains("wi-fi",StringComparison.OrdinalIgnoreCase)||text.Contains("wireless",StringComparison.OrdinalIgnoreCase))kind="Wireless";
            }
            foreach(var child in Rows(obj["children"]).Take(256))Walk(child,parent,kind,depth+1);
        }
        if(snapshot.Data.TryGetValue("NETWORKMAP_ALL",out var topology) && topology["HNC"] is JsonObject tree && tree.Count>0)Walk(tree,null,"Not reported",0,true);
        if(snapshot.Data.TryGetValue("LINKSPEEDBH_ALL",out var links))foreach(var link in Rows(links["LINKSPEEDBH"]).Take(256))
        {
            if(Get(link,"ACTIVE").ToLowerInvariant() is not ("true" or "1"))continue;
            var macs=new[]{Mac(Get(link,"MACADDRESS")),Mac(Get(link,"STAADDRESS"))}.Where(m=>m.Length>0).ToArray();
            var matches=nodes.Where(n=>!n.Controller && macs.Any(m=>m==n.Mac||m==n.BackhaulMac)).ToArray();
            if(matches.Length!=1)continue;var child=matches[0];
            var existing=edges.FindIndex(e=>e.Parent==controller.Serial && e.Child==child.Id);
            var signal=Get(link,"SIGNALSTRENGTH");if(!int.TryParse(signal,out var dbm)||dbm< -200||dbm>=0)signal="Not reported";else signal+=" dBm";
            var edge=new MeshBackhaul(controller.Serial,child.Id,"Wireless",Rate(Get(link,"DOWNLINKRATE")),Rate(Get(link,"UPLINKRATE")),signal,"LINKSPEEDBH_ALL · local AP association");
            if(existing<0)edges.Add(edge);else edges[existing]=edge;
        }
        return new(nodes,edges,snapshot.Time);
    }
    public string Describe(MeshRouter node)
    {
        var incoming=Links.Where(l=>l.Child==node.Id).ToArray();
        return node.Controller?"Controller · "+Links.Count+" reported connections":incoming.Length==0?"Upstream connection not reported":string.Join("\n",incoming.Select(e=>
            (Routers.FirstOrDefault(r=>r.Id==e.Parent)?.Name??"Unknown parent")+" ↔ "+node.Name+"\n"+e.Kind+" · ↓ "+e.Down+" · ↑ "+e.Up+"\nSignal: "+e.Signal));
    }
}
