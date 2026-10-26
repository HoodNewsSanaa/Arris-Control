using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArrisControl;

internal record Choice(string Value,string Label){public override string ToString()=>Label;}
internal record SettingField(string Key,string Label,string Kind="text",Choice[]? Choices=null,string Hint="");
internal record SettingDefinition(string Key,string Container,string Title,SettingField[] Fields,
    bool Collection=false,string Impact="",string[]? Metadata=null);
internal record SettingSection(SettingDefinition Definition,string Container,int Index,string Identity,JsonObject Baseline,string Title);
internal record PendingChange(SettingDefinition Definition,string Container,int Index,string Identity,
    JsonObject Before,JsonObject Expected,string Body,string Preview,string Operation);

internal static class Settings
{
    static SettingField Toggle(string key,string label)=>new(key,label,"toggle");
    static SettingField Text(string key,string label,string kind="text",string hint="")=>new(key,label,kind,null,hint);
    public static readonly SettingDefinition[] Definitions=[
        new("NETWORKDNS_ALL","NETWORKDNS","DNS servers",[
            new("DYNAMICV4","IPv4 DNS mode","choice",[new("1","Automatic"),new("0","Manual")]),
            Text("DNSIPV4SERVERS","IPv4 DNS servers","dns4"),
            new("DYNAMICV6","IPv6 DNS mode","choice",[new("1","Automatic"),new("0","Manual")]),
            Text("DNSIPV6SERVERS","IPv6 DNS servers","dns6")],false,"DNS changes affect how devices resolve Internet names.",["IPV4ADDRESS","IPV6ADDRESS"]),
        new("NETWORKMISC_ALL","NETWORKMISC","UPnP",[Toggle("UPNP_ENABLE","Allow automatic port mappings")]),
        new("DEVICEOPERMODE_ALL","DEVICEOPERMODE","Routing and IPv6",[
            new("MODE","Routing mode","choice",[new("DUAL","Router mode · IPv4 and IPv6"),new("IPV4ONLY","Router mode · IPv4 only"),new("BRIDGE","Bypass (bridge) mode")])],false,"Routing changes can interrupt connectivity."),
        new("LED_DISPLAY_ALL","LED_DISPLAY","Status light",[
            new("DISPLAY","Light behavior","choice",[new("1","On"),new("0","Off"),new("2","Off except alerts")])]),
        new("IPV4DHCPRANGE_ALL","IPV4DHCPRANGE","IPv4 DHCP",[
            Text("IPV4START_ADDRESS","First DHCP address","ipv4"),Text("IPV4END_ADDRESS","Last DHCP address","ipv4"),
            Text("IPV4LEASE_TIME","Lease duration (seconds)","lease")],false,"DHCP changes affect address assignment. Existing devices may keep their lease until renewal.",["IDX","NETWORK","SUBNET_MASK"]),
        new("IPV6DHCPRANGE_ALL","IPV6DHCPRANGE","IPv6 DHCP",[
            Text("IPV6START_ADDRESS","First DHCP address","ipv6"),Text("IPV6END_ADDRESS","Last DHCP address","ipv6"),
            Text("IPV6LEASE_TIME","Lease duration (seconds)","lease")]),
        new("STATICIP_ALL","STATICIP","Address reservation",[
            Toggle("ENABLE","Enabled"),Text("ALIAS","Device name"),Text("MACADDRESS","Device MAC address","mac"),Text("IPV4ADDRESS","Reserved IPv4 address","ipv4")],true,"The device may need to renew its DHCP lease.",["IDX"]),
        new("PORTFORWARDING_ALL","PORTFORWARDING","Port forwarding rule",[
            Toggle("ENABLE","Enabled"),Text("DESCRIPTION","Rule name"),Text("INTERNALCLIENT","Destination IPv4 address","ipv4"),
            new("PROTOCOL","Protocol","choice",[new("TCP","TCP"),new("UDP","UDP"),new("TCP/UDP","TCP and UDP")]),
            Text("EXTERNALPORT","First external port","port"),Text("EXTERNALPORTENDRANGE","Last external port","port"),
            Text("INTERNALPORT","First internal port","port"),Text("INTERNALPORTENDRANGE","Last internal port","port")],true,"Enabled forwarding rules allow incoming connections to the selected device.",["IDX","CONTAINER","ALLINTERFACES","INTERFACE","APP"]),
        new("DEVICEPRIORITY_ALL","DEVICEPRIORITY","Device priority",[Toggle("ENABLE","Enabled"),Text("MACADDRESS","Device MAC address","mac"),Text("DURATION","Duration","positive")],false,"Changes affect device traffic prioritization.",["CONTAINER"]),
        new("PARENTAL_CONTROL_ALL","PARENTAL_CONTROL","Parental controls",[Toggle("ENABLE","Enable parental controls")]),
        new("QoS_ALL","QoS","Quality of service",[Toggle("ENABLE","Enable quality of service")]),
        Wifi("WIFI_24G","Main Wi-Fi · 2.4 GHz",false),Wifi("WIFI_5G","Main Wi-Fi · 5 GHz",false),
        Wifi("WIFI_GUEST_24G","Guest Wi-Fi · 2.4 GHz",true),Wifi("WIFI_GUEST_5G","Guest Wi-Fi · 5 GHz",true)
    ];
    static SettingDefinition Wifi(string container,string title,bool guest)=>new(container+"_ALL",container,title,
        guest ? [Toggle("ENABLE","Guest Wi-Fi enabled"),Text("SSIDNAME","Network name","ssid"),Text("PASSPHRASE","New Wi-Fi password","password","Leave blank to keep the current password.")]
        : [Text("SSIDNAME","Network name","ssid"),Text("PASSPHRASE","New Wi-Fi password","password","Leave blank to keep the current password."),
            Toggle("AUTOCHANNEL","Choose channel automatically"),Text("CHANNEL","Radio channel","channel"),Toggle("SSIDADVERTISEMENTENABLED","Broadcast network name")],
        false,"Changing Wi-Fi may disconnect this PC and other wireless devices.",["IDX","SECURITYMODE","SECURITYMETHOD","SECURITYENC"]);

    public static JsonObject Filter(SettingDefinition def,JsonObject source)
    {
        var result=new JsonObject();
        foreach(var key in def.Fields.Select(f=>f.Key).Concat(def.Metadata??[]))
            if(source.TryGetPropertyValue(key,out var value))result[key]=value?.DeepClone();
        if(def.Container=="LED_DISPLAY" && !result.ContainsKey("DISPLAY") && source.ContainsKey("LED_DISPLAY"))result["DISPLAY"]=source["LED_DISPLAY"]?.DeepClone();
        return result;
    }
    public static List<SettingSection> Sections(Snapshot snapshot)
    {
        var result=new List<SettingSection>();
        foreach(var def in Definitions.Where(d=>snapshot.Data.ContainsKey(d.Key)))
        {
            var node=snapshot.Data[def.Key][def.Container];
            if(node is JsonObject o)result.Add(new(def,def.Container,0,Catalog.Text(o["IDX"]),Filter(def,o),def.Title));
            if(node is JsonArray array)for(var i=0;i<array.Count;i++)if(array[i] is JsonObject item)
            {
                var name=Catalog.Text(item["ALIAS"]??item["DESCRIPTION"]??item["SSIDNAME"]);
                result.Add(new(def,def.Container,i,Catalog.Text(item["IDX"]),Filter(def,item),def.Title+(name.Length>0?" · "+name:array.Count>1?" · "+(i+1):"")));
            }
        }
        return result;
    }
    public static JsonObject? Locate(JsonObject response,string container,int index,string identity)
    {
        var node=response[container];
        if(index<0)return new JsonObject();
        if(node is JsonObject obj)return obj;
        if(node is JsonArray array)
        {
            if(identity.Length>0)return array.OfType<JsonObject>().FirstOrDefault(o=>Catalog.Text(o["IDX"])==identity);
            return index>=0 && index<array.Count ? array[index] as JsonObject:null;
        }
        return null;
    }
    public static bool SameValues(JsonObject? current,JsonObject baseline)=>current is not null && baseline.All(kv=>
        JsonNode.DeepEquals(current[kv.Key]??(kv.Key=="DISPLAY" ? current["LED_DISPLAY"]:null),kv.Value));
    public static bool VerifyValues(JsonObject? actual,JsonObject expected)=>actual is not null && expected.All(kv=>
    {
        var value=Catalog.Text(actual[kv.Key]??(kv.Key=="DISPLAY" ? actual["LED_DISPLAY"]:null));
        if(Catalog.Secret.IsMatch(kv.Key) && (value.Length==0 || Regex.IsMatch(value,"^[*•]+$")))return false;
        return value.Trim().Equals(Catalog.Text(kv.Value).Trim(),kv.Key=="MACADDRESS" ? StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
    });
    public static JsonObject? FindMatch(JsonObject response,string container,JsonObject expected)
    {
        var node=response[container];return node is JsonArray a ? a.OfType<JsonObject>().FirstOrDefault(o=>VerifyValues(o,expected)) : node as JsonObject;
    }
    static void Validate(SettingField field,string value)
    {
        if(value.Any(c=>char.IsControl(c)))throw new RouterException(field.Label+" contains control characters.");
        if(value.Length>256)throw new RouterException(field.Label+" is too long.");
        if(field.Kind=="choice" && !(field.Choices?.Any(c=>c.Value==value)??false))throw new RouterException("Choose a valid "+field.Label.ToLowerInvariant()+".");
        if(field.Kind=="toggle" && value is not ("0" or "1"))throw new RouterException("Choose on or off for "+field.Label.ToLowerInvariant()+".");
        if(field.Kind=="ssid" && (Encoding.UTF8.GetByteCount(value) is <1 or >32))throw new RouterException("A Wi-Fi name must contain 1–32 UTF-8 bytes.");
        if(field.Kind=="password" && !(value.Length is >=8 and <=63 || Regex.IsMatch(value,"^[A-Fa-f0-9]{64}$")))throw new RouterException("Use an 8–63 character Wi-Fi password or a 64-digit hexadecimal key.");
        if(field.Kind is "ipv4" or "ipv6")CheckIp(value,field.Kind=="ipv4"?AddressFamily.InterNetwork:AddressFamily.InterNetworkV6,field.Label);
        if(field.Kind=="mac" && !Regex.IsMatch(value,"^(?:[A-Fa-f0-9]{2}:){5}[A-Fa-f0-9]{2}$"))throw new RouterException("Use a MAC address such as 00:11:22:33:44:55.");
        if(field.Kind is "port" or "lease" or "positive" or "channel")
        {
            var maximum=field.Kind=="port" ? 65535L:field.Kind=="channel" ? 233L:int.MaxValue;
            if(!long.TryParse(value,out var number) || number<1 || number>maximum)throw new RouterException(field.Label+" must be between 1 and "+maximum+".");
        }
    }
    static void CheckIp(string value,AddressFamily family,string label)
    {
        if(!IPAddress.TryParse(value,out var ip) || ip.AddressFamily!=family || value.Contains('%') ||
            ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(ip) ||
            (family==AddressFamily.InterNetwork && (ip.GetAddressBytes()[0]>=224 || ip.GetAddressBytes()[0]==0)))
            throw new RouterException("Enter a valid "+label.ToLowerInvariant()+".");
    }
    public static PendingChange Prepare(SettingDefinition def,string container,JsonObject? current,
        Dictionary<string,string> edits,string operation="MODIFY",int index=0,string identity="")
    {
        if(def.Container!=container || !Definitions.Contains(def))throw new RouterException("Unknown settings section.");
        if(operation is not ("MODIFY" or "ADD" or "DELETE") || operation!="MODIFY" && !def.Collection)throw new RouterException("Unsupported settings operation.");
        if(operation!="ADD" && current is null)throw new RouterException("The selected setting no longer exists. Refresh first.");
        var before=Filter(def,current??new());var payload=(JsonObject)before.DeepClone();var expected=new JsonObject();var changes=new List<string>();
        foreach(var edit in edits)
        {
            var field=def.Fields.FirstOrDefault(f=>f.Key==edit.Key)??throw new RouterException("This field is not editable.");
            var value=edit.Value.Trim();if(field.Kind=="password")value=edit.Value;
            if(field.Kind=="password" && value.Length==0)continue;
            var oldValue=Catalog.Text(before[field.Key]);
            if(field.Kind is "dns4" or "dns6")
            {
                value=string.Join(',',value.Split(',').Select(v=>v.Trim()).Where(v=>v.Length>0));
                oldValue=string.Join(',',oldValue.Split(',').Select(v=>v.Trim()).Where(v=>v.Length>0));
            }
            if(value==oldValue && operation!="ADD")continue;
            if(field.Kind is not ("dns4" or "dns6"))Validate(field,value);
            payload[field.Key]=value;expected[field.Key]=value;
            var old=Catalog.Secret.IsMatch(field.Key) ? "[hidden]":Catalog.Text(before[field.Key]);
            changes.Add(field.Label+": "+(operation=="ADD"?"":old+" → ")+(Catalog.Secret.IsMatch(field.Key)?"[new password]":value));
        }
        // Never resend a masked password as if it were the actual Wi-Fi key.
        foreach(var secret in payload.Where(kv=>Catalog.Secret.IsMatch(kv.Key)).Select(kv=>kv.Key).ToArray())
            if(Regex.IsMatch(Catalog.Text(payload[secret]),"^[*•]+$"))payload.Remove(secret);
        if(container=="NETWORKDNS")foreach(var family in new[]{"4","6"})
        {
            var mode=Catalog.Text(payload["DYNAMICV"+family]);var servers="DNSIPV"+family+"SERVERS";
            if(mode=="0")
            {
                var entries=Catalog.Text(payload[servers]).Split(',').Select(s=>s.Trim()).Where(s=>s.Length>0).ToArray();
                if(entries.Length is <1 or >2)throw new RouterException("Enter one or two IPv"+family+" DNS servers.");
                foreach(var entry in entries)CheckIp(entry,family=="4"?AddressFamily.InterNetwork:AddressFamily.InterNetworkV6,"IPv"+family+" DNS address");
                payload[servers]=string.Join(',',entries);
                if(expected.ContainsKey(servers))expected[servers]=payload[servers]!.DeepClone();
            }
            else if(mode is not ("0" or "1"))throw new RouterException("The router did not return a recognized DNS mode. Refresh before changing DNS.");
            // APK DNSConfigurationPresenter writes IPV4ADDRESS/IPV6ADDRESS;
            // DNSIPV4SERVERS/DNSIPV6SERVERS are the read-back fields.
            payload["IPV"+family+"ADDRESS"]=mode=="1" ? (family=="4"?"0.0.0.0,0.0.0.0":"::,::") : payload[servers]!.DeepClone();
            payload.Remove(servers);
        }
        if(container=="PORTFORWARDING")
        {
            foreach(var prefix in new[]{"EXTERNAL","INTERNAL"})
                if(int.TryParse(Catalog.Text(payload[prefix+"PORT"]),out var first) && int.TryParse(Catalog.Text(payload[prefix+"PORTENDRANGE"]),out var last) && last<first)
                    throw new RouterException("The last port cannot be lower than the first port.");
            if(operation=="ADD"){payload["ALLINTERFACES"]="1";payload["ENABLE"]="1";payload["APP"]=payload["DESCRIPTION"]?.DeepClone()??JsonValue.Create("");}
        }
        if(container=="STATICIP" && operation=="ADD")payload["ENABLE"]="1";
        if(container=="DEVICEOPERMODE" && Catalog.Text(payload["MODE"]) is not ("DUAL" or "IPV4ONLY" or "BRIDGE"))throw new RouterException("Unrecognized routing mode.");
        if(container=="DEVICEOPERMODE" && Catalog.Text(payload["MODE"])=="BRIDGE")changes.Add("WARNING: Enabling bypass may disconnect this app and make the router unreachable. Turning bypass off requires management access. Access and recovery in bridge mode have not been verified; a physical reset may be needed.");
        if(operation=="DELETE")
        {
            if(identity.Length==0)throw new RouterException("The router did not provide a rule identifier; removal is unavailable.");
            payload=new JsonObject{["IDX"]=identity};expected=new JsonObject();changes=["Remove "+def.Title.ToLowerInvariant()+"."];
        }
        if(changes.Count==0)throw new RouterException("No settings have changed.");
        if(operation=="ADD")
        {
            var required=container=="STATICIP"?new[]{"MACADDRESS","IPV4ADDRESS"}:new[]{"INTERNALCLIENT","PROTOCOL","EXTERNALPORT","EXTERNALPORTENDRANGE","INTERNALPORT","INTERNALPORTENDRANGE"};
            foreach(var key in required)if(Catalog.Text(payload[key]).Length==0)throw new RouterException("Complete all required fields before adding a rule.");
        }
        var body=new JsonObject{["REQUEST"]=new JsonObject{["OPERTYPE"]=operation,[container]=payload}}.ToJsonString();
        ValidateEnvelope(body);
        return new(def,container,index,identity,before,expected,body,string.Join("\n",changes)+(def.Impact.Length>0?"\n\n"+def.Impact:""),operation);
    }
    public static void ValidateEnvelope(string body)
    {
        var root=JsonNode.Parse(body) as JsonObject;
        if(root?.Count!=1 || root["REQUEST"] is not JsonObject request || request.Count!=2)throw new RouterException("Unsupported settings payload.");
        var operation=Catalog.Text(request["OPERTYPE"]);var entry=request.FirstOrDefault(kv=>kv.Key!="OPERTYPE");
        var def=Definitions.FirstOrDefault(d=>d.Container==entry.Key)??throw new RouterException("This operation is not available in this build.");
        if(entry.Value is not JsonObject fields || operation is not ("MODIFY" or "ADD" or "DELETE") || operation!="MODIFY" && !def.Collection)
            throw new RouterException("Unsupported settings operation.");
        var allowed=def.Fields.Select(f=>f.Key).Concat(def.Metadata??[]).ToHashSet(StringComparer.Ordinal);
        foreach(var kv in fields)if(!allowed.Contains(kv.Key) || kv.Value is not JsonValue)
            throw new RouterException("Unexpected settings field; request refused.");
        if(entry.Key=="DEVICEOPERMODE" && Catalog.Text(fields["MODE"]) is not ("DUAL" or "IPV4ONLY" or "BRIDGE"))throw new RouterException("Unrecognized routing mode.");
    }
}
