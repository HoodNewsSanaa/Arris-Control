using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArrisControl;

internal sealed record MeshRouter(string Id,string Name,string Model,string Address,string Mac,string BackhaulMac,bool Controller,
    IReadOnlyList<string>? ReportedMacs=null);
internal sealed record MeshBackhaul(string Parent,string Child,string Kind,string Down,string Up,string Signal,string Source);
internal static class SavedRouterLabels
{
    public static string Key(CredentialRecord record)=>"router-"+Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(record.Serial.Trim().ToUpperInvariant()+"|"+record.Mac.Trim().ToUpperInvariant())));
    public static string Name(CredentialRecord record,Func<string,string?> read)=>read(Key(record)) is {Length:>0} name?name:record.Serial;
}
internal static class MeshNodeLabels
{
    // These keys describe a physical router, never its IP address or position in a diagram.
    public static string[] Keys(MeshRouter node)
    {
        var identities=new List<string>();
        var id=node.Id.Trim().ToUpperInvariant();
        if(Regex.IsMatch(id,"^[A-Z0-9]{12,24}$") && id!=node.Mac && id!=node.BackhaulMac)identities.Add("serial:"+id);
        identities.AddRange(new[]{node.Mac,node.BackhaulMac}.Concat(node.ReportedMacs??[])
            .Select(MeshTopology.Mac).Where(m=>m.Length>0).Distinct().Select(m=>"mac:"+m));
        return identities.Select(i=>"node-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(i)))).ToArray();
    }
    public static string Validate(string text)
    {
        var name=text.Trim().Normalize(NormalizationForm.FormC);
        if(name.Length>48 || name.Any(c=>char.IsControl(c)||c is '\u2028' or '\u2029'))
            throw new RouterException("Use a name up to 48 characters on one line.");
        return name;
    }
    public static MeshTopology Apply(MeshTopology reported,Func<string,string?> read)=>reported with{
        Routers=reported.Routers.Select(node=>
        {
            var custom=Keys(node).Select(read).FirstOrDefault(name=>!string.IsNullOrWhiteSpace(name));
            return custom is null?node:node with{Name=custom};
        }).ToArray()};
}
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
    static bool HasMac(MeshRouter router,string mac)=>mac.Length>0 &&
        (router.Mac==mac || router.BackhaulMac==mac || router.ReportedMacs?.Contains(mac)==true);
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
        MeshRouter? Find(string serial,params string[] macs)=>nodes.FirstOrDefault(n=>serial.Length>0 && n.Id==serial || macs.Any(m=>HasMac(n,m)));
        void Walk(JsonObject obj,MeshRouter? parent,string kind,int depth,bool root=false)
        {
            if(depth>16 || nodes.Count>=128)return;
            var data=obj["data"] as JsonObject??obj;
            var serial=Get(data,"serialNum","serialNumber");
            // Native HNC uses WANMACAddr/MACAddr/IPAddr, unlike the older fixtures.
            // Its device MAC can differ from the WAN MAC and identify the backhaul station.
            var wan=Mac(Get(data,"wanMac","WANMACAddr"));
            var deviceMac=Mac(Get(data,"macAddress","MACAddr","mac"));
            var mac=wan.Length>0?wan:deviceMac;
            var deviceType=Get(data,"deviceType").ToLowerInvariant();
            var router=root?nodes[0]:Find(serial,wan,deviceMac);
            // Interface and client nodes must never become satellite routers.
            if(router is null && deviceType is "router" or "extender" or "satellite" or "hne" or "hnc")
            {
                var id=serial.Length>0?serial:mac;
                if(id.Length>0){router=new(id,Get(obj,"text").Length>0?Get(obj,"text"):"Satellite "+nodes.Count,Get(data,"modelName","modelNumber"),Get(data,"ipAddress","IPAddr","ip"),mac,Mac(Get(data,"backhaulMac")),false);nodes.Add(router);}
            }
            if(router is not null)
            {
                var name=Get(obj,"text");var model=Get(data,"modelName","modelNumber");var ip=Get(data,"ipAddress","IPAddr","ip");
                var conflict=wan.Length>0 && router.Mac.Length>0 && wan!=router.Mac;
                var aliases=(router.ReportedMacs??[]).Concat(conflict?[]:new[]{wan,deviceMac,Mac(Get(data,"backhaulMac"))})
                    .Where(m=>m.Length>0).Distinct().ToArray();
                var updated=router with{Name=name.Length>0?name:router.Name,Model=model.Length>0?model:router.Model,Address=ip.Length>0?ip:router.Address,
                    Mac=router.Mac.Length>0?router.Mac:mac,ReportedMacs=aliases};
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
            var matches=nodes.Where(n=>!n.Controller && macs.Any(m=>HasMac(n,m))).ToArray();
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
