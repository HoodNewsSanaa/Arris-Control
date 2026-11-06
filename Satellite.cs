using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;

namespace ArrisControl;

/// <summary>A satellite selected from a fresh main-node mesh map. Serial and address come from the
/// main router's NETWORKMAP; every write re-checks the serial the satellite itself reports.</summary>
internal sealed record SatelliteTarget(string Serial,string Mac,string Address,string Name)
{
    public static SatelliteTarget? From(MeshRouter node)
    {
        if(node.Controller || !Regex.IsMatch(node.Id,"^[A-Z0-9]{12,24}$") || !SatelliteForwarding.ValidAddress(node.Address))return null;
        return new(node.Id,node.Mac,node.Address,node.Name);
    }
}

/// <summary>
/// The main node's local API forwards a fixed set of requests to a satellite: GET adds
/// <c>&amp;ip=</c>, SET adds REQUEST.HNEIPADDRESS (W30 MobAppOperation.php, OPMODE_PGET/PSET).
/// Only the features the firmware forwards are admitted. DNS, DHCP, Wi-Fi, DFS and every other
/// setting stay main-node only. Reboot is fixed to SIMPLE_RESET; FACTORY_RESET/WIFI_RESET are refused.
/// </summary>
internal static class SatelliteForwarding
{
    public static readonly string[] ReadKeys=["DEVICE_INFO_ALL","FIRMWARE_ALL","DEVICESTATUS_ALL","LEDRING_ALL","LED_DISPLAY_ALL"];
    public static readonly string[] WriteContainers=["LEDRING","LED_DISPLAY"];
    public const string RebootMode="SIMPLE_RESET";

    public static bool ValidAddress(string? address)=>address is not null && Regex.IsMatch(address,@"^\d{1,3}(?:\.\d{1,3}){3}$") &&
        IPAddress.TryParse(address,out var ip) && ip.ToString()==address && CredentialStore.IsPrivate(address);

    public static string ReadPath(string key,string address)
    {
        if(!ReadKeys.Contains(key))throw new RouterException("This information is not available for satellites.");
        if(!ValidAddress(address))throw new RouterException("A private satellite IPv4 address is required.");
        return "/getData?key="+key+"&ip="+address;
    }
    public static bool IsReadPath(string path)
    {
        var match=Regex.Match(path,@"^/getData\?key=([A-Z_]+)&ip=([0-9.]{7,15})$");
        return match.Success && ReadKeys.Contains(match.Groups[1].Value) && ValidAddress(match.Groups[2].Value);
    }
    /// <summary>Adds the satellite address to an already-validated main-node settings request.</summary>
    public static string Forward(string body,string address)
    {
        if(!ValidAddress(address))throw new RouterException("A private satellite IPv4 address is required.");
        var request=(JsonNode.Parse(body) as JsonObject)?["REQUEST"] as JsonObject??throw new RouterException("Unsupported settings payload.");
        var entry=request.FirstOrDefault(kv=>kv.Key!="OPERTYPE");
        if(!WriteContainers.Contains(entry.Key))throw new RouterException("This setting cannot be changed on a satellite.");
        var forwarded=new JsonObject{["OPERTYPE"]=request["OPERTYPE"]?.DeepClone(),["HNEIPADDRESS"]=address,[entry.Key]=entry.Value?.DeepClone()};
        var result=new JsonObject{["REQUEST"]=forwarded}.ToJsonString();
        Settings.ValidateEnvelope(result);
        return result;
    }
    public static string Reboot(string address)
    {
        if(!ValidAddress(address))throw new RouterException("A private satellite IPv4 address is required.");
        var body=new JsonObject{["REQUEST"]=new JsonObject{["OPERTYPE"]="MODIFY",["HNEIPADDRESS"]=address,
            ["DEVICERESET"]=new JsonObject{["RESETMODE"]=RebootMode,["DORESET"]="true"}}}.ToJsonString();
        Settings.ValidateEnvelope(body);
        return body;
    }
    /// <summary>Called by <see cref="Settings.ValidateEnvelope"/> for any request carrying HNEIPADDRESS.</summary>
    internal static void ValidateEnvelope(JsonObject request)
    {
        if(request.Count!=3 || Catalog.Text(request["OPERTYPE"])!="MODIFY" || request["HNEIPADDRESS"] is not JsonValue ip ||
            !ip.TryGetValue<string>(out var address) || !ValidAddress(address))
            throw new RouterException("Unsupported satellite request.");
        var entry=request.First(kv=>kv.Key is not ("OPERTYPE" or "HNEIPADDRESS"));
        if(entry.Key=="DEVICERESET")
        {
            if(entry.Value is not JsonObject reset || reset.Count!=2 || Catalog.Text(reset["RESETMODE"])!=RebootMode || Catalog.Text(reset["DORESET"])!="true" ||
                reset["RESETMODE"] is not JsonValue || reset["DORESET"] is not JsonValue)
                throw new RouterException("Only a plain satellite restart is allowed. Factory and Wi-Fi resets are refused.");
            return;
        }
        if(!WriteContainers.Contains(entry.Key))throw new RouterException("This setting cannot be changed on a satellite.");
        // The remaining body must also pass the normal per-container field allowlist.
        Settings.ValidateEnvelope(new JsonObject{["REQUEST"]=new JsonObject{["OPERTYPE"]="MODIFY",[entry.Key]=entry.Value?.DeepClone()}}.ToJsonString());
    }
}

internal sealed record SatelliteRadio(string Serial,bool? Dfs,string DfsRaw,string Channel,string Bandwidth,string AutoChannel)
{
    public string Summary=>$"DFS {(Dfs is null?"unknown ("+(DfsRaw.Length>0?DfsRaw:"not reported")+")":Dfs.Value?"on":"off")} · 5 GHz channel {Or(Channel)} · width {Or(Bandwidth)}";
    static string Or(string v)=>v.Length>0?v:"not reported";
}

/// <summary>
/// EXPERIMENTAL. The satellite's mesh agent (hne_cgi behind lighttpd on port 8445) is the channel the
/// main node's controller and forwarding library (libarris_hnereq) use: HTTPS POST /hne_config.htm reads
/// and PUT writes TR-181 values in an &lt;X_ARRIS_COM&gt;&lt;tr181i2&gt; document. The main node never pushes the
/// fronthaul DFS flag to satellites, so this sets the same parameter the main node's own DFS setter uses.
/// Allowlisted to a few identity/radio reads and one DFS write. Not an official app path.
/// </summary>
internal static class MeshAgentProtocol
{
    public const int Port=8445;
    public const string Path="/hne_config.htm";
    public const string Serial="Device.DeviceInfo.SerialNumber";
    public const string Dfs="Device.WiFi.Radio.2.X_ARRIS_COM.DFS";
    public const string Channel="Device.WiFi.Radio.2.Channel";
    public const string Bandwidth="Device.WiFi.Radio.2.OperatingChannelBandwidth";
    public const string AutoChannel="Device.WiFi.Radio.2.AutoChannelEnable";
    public static readonly string[] ReadParams=[Serial,Dfs,Channel,Bandwidth,AutoChannel];
    const int MaxResponse=64*1024;

    /// <summary>Stock DFS setter values: on=2 (DFS re-entry), off=0.</summary>
    public static string DfsValue(bool enable)=>enable?"2":"0";
    public static bool? DfsEnabled(string value)=>value.Trim().ToLowerInvariant() switch{"0" or "false"=>false,"1" or "2" or "true"=>true,_=>null};

    public static string Body(IEnumerable<KeyValuePair<string,string>> values)
    {
        var sb=new StringBuilder("<?xml version=\"1.0\" encoding=\"US-ASCII\"?><X_ARRIS_COM><tr181i2>");
        foreach(var (name,value) in values)
        {
            if(!Regex.IsMatch(name,@"^Device(?:\.[A-Za-z0-9_]+)+$"))throw new RouterException("Invalid mesh parameter name.");
            if(value.Any(c=>c>127 || char.IsControl(c)))throw new RouterException("Invalid mesh parameter value.");
            sb.Append('<').Append(name).Append('>').Append(SecurityElementEscape(value)).Append("</").Append(name).Append('>');
        }
        return sb.Append("</tr181i2></X_ARRIS_COM>").ToString();
    }
    static string SecurityElementEscape(string v)=>v.Replace("&","&amp;").Replace("<","&lt;").Replace(">","&gt;").Replace("\"","&quot;").Replace("'","&apos;");
    public static string ReadBody(IEnumerable<string> names)
    {
        var list=names.ToArray();
        if(list.Length==0 || list.Any(n=>!ReadParams.Contains(n)))throw new RouterException("Mesh read is outside the satellite allowlist.");
        return Body(list.Select(n=>new KeyValuePair<string,string>(n,"")));
    }
    public static string DfsBody(bool enable)=>Body([new(Dfs,DfsValue(enable))]);

    public static Dictionary<string,string> Parse(byte[] body,IReadOnlyCollection<string> expected)
    {
        if(body.Length>MaxResponse)throw new RouterException("Satellite response exceeded the limit.");
        var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,IgnoreComments=true,IgnoreWhitespace=true,MaxCharactersInDocument=MaxResponse};
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        try
        {
            using var reader=XmlReader.Create(new MemoryStream(body),settings);
            reader.MoveToContent();
            if(reader.Name!="X_ARRIS_COM")throw new RouterException("Satellite returned an unexpected response.");
            if(!reader.ReadToDescendant("tr181i2"))throw new RouterException("Satellite returned no parameter list.");
            if(reader.IsEmptyElement)return result;
            reader.Read();
            while(reader.NodeType==XmlNodeType.Element)
            {
                var name=reader.Name;
                if(!expected.Contains(name) || result.ContainsKey(name))throw new RouterException("Satellite returned an unexpected parameter.");
                result[name]=reader.ReadElementContentAsString().Trim();
            }
        }
        catch(XmlException){throw new RouterException("Satellite returned malformed XML.");}
        return result;
    }
}

internal interface IMeshAgentWire : IDisposable
{
    /// <summary>POST reads, PUT writes. Returns the raw XML response body.</summary>
    Task<byte[]> SendAsync(HttpMethod method,string body,CancellationToken token);
}

internal sealed class MeshAgentWire : IMeshAgentWire
{
    readonly HttpClient client;readonly Uri uri;readonly object gate=new();string? observedPin;
    public MeshAgentWire(SatelliteTarget target)
    {
        if(!SatelliteForwarding.ValidAddress(target.Address))throw new RouterException("A private satellite IPv4 address is required.");
#if ANDROID
        AndroidNetwork.UseLocal();
#endif
        uri=new Uri($"https://{target.Address}:{MeshAgentProtocol.Port}{MeshAgentProtocol.Path}");
        var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false};
        // The agent's certificate is issued for the controller's private CA, which the phone does not have.
        // Lock the first key seen for this wire's lifetime; identity is proven by the serial read over it.
        handler.ServerCertificateCustomValidationCallback=(_,cert,_,_)=>
        {
            if(cert is null)return false;
            var pin=CertificatePin.Compute(cert);
            lock(gate){observedPin??=pin;return observedPin==pin;}
        };
        client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(15)};
    }
    public async Task<byte[]> SendAsync(HttpMethod method,string body,CancellationToken token)
    {
        if(method!=HttpMethod.Post && method!=HttpMethod.Put)throw new RouterException("Unsupported mesh request.");
        using var request=new HttpRequestMessage(method,uri){Content=new StringContent(body,Encoding.ASCII,"text/xml")};
        using var reply=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        if(reply.StatusCode!=HttpStatusCode.OK)throw new RouterException($"Satellite mesh agent returned HTTP {(int)reply.StatusCode}.","HTTP"+(int)reply.StatusCode);
        await using var input=await reply.Content.ReadAsStreamAsync(token);
        using var memory=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=await input.ReadAsync(buffer,token))>0){if(memory.Length+count>64*1024)throw new RouterException("Satellite response exceeded the limit.");await memory.WriteAsync(buffer.AsMemory(0,count),token);}
        return memory.ToArray();
    }
    public void Dispose()=>client.Dispose();
}

internal static class MeshAgent
{
    public static async Task<SatelliteRadio> ReadRadioAsync(IMeshAgentWire wire,SatelliteTarget target,CancellationToken token)
    {
        var values=MeshAgentProtocol.Parse(await wire.SendAsync(HttpMethod.Post,MeshAgentProtocol.ReadBody(MeshAgentProtocol.ReadParams),token),MeshAgentProtocol.ReadParams);
        var serial=values.GetValueOrDefault(MeshAgentProtocol.Serial,"");
        if(serial!=target.Serial)throw new RouterException(serial.Length==0
            ? "The satellite mesh agent did not report its serial. Nothing was changed."
            : "The device at "+target.Address+" reports a different serial than the mesh map. Nothing was changed.");
        var dfs=values.GetValueOrDefault(MeshAgentProtocol.Dfs,"");
        return new(serial,MeshAgentProtocol.DfsEnabled(dfs),dfs,values.GetValueOrDefault(MeshAgentProtocol.Channel,""),
            values.GetValueOrDefault(MeshAgentProtocol.Bandwidth,""),values.GetValueOrDefault(MeshAgentProtocol.AutoChannel,""));
    }
    /// <summary>Identity check, one PUT (never retried), then read-back over the same pinned connection.</summary>
    public static async Task<string> SetDfsAsync(IMeshAgentWire wire,SatelliteTarget target,bool enable,CancellationToken token)
    {
        var before=await ReadRadioAsync(wire,target,token);
        if(before.Dfs==enable)return "The satellite already reports DFS "+(enable?"on":"off")+". Nothing was sent. "+before.Summary;
        try{await wire.SendAsync(HttpMethod.Put,MeshAgentProtocol.DfsBody(enable),token);}
        catch(RouterException ex) when(ex.Code.StartsWith("HTTP",StringComparison.Ordinal))
        {throw new RouterException("The satellite refused the DFS change ("+ex.Message+"). Its mesh agent may only accept the main router. Nothing else was tried.",ex.Code);}
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException)
        {throw new RouterException("The DFS request was sent, but its result is unknown. Read the satellite again before another attempt; it was not retried.");}
        try
        {
            var after=await ReadRadioAsync(wire,target,token);
            return after.Dfs==enable
                ? "Satellite DFS "+(enable?"on":"off")+" verified by reading it back. The radio may take a moment to pick a new channel. "+after.Summary
                : "The satellite accepted the request, but still reports "+after.Summary+". It was not retried.";
        }
        catch(Exception ex) when(ex is RouterException or HttpRequestException or TaskCanceledException)
        {return "The satellite accepted the DFS request, but read-back failed. Read it again before another attempt.";}
    }
}
