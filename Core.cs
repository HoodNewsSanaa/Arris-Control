using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArrisControl;

internal record Page(string Id, string Name, string Description, string[] Keys);
internal record FieldRow(string Field, string Value, string Source);
internal record ReadResult(string Key, int? Http, string Status, DateTimeOffset Time, bool Readable);
internal sealed record Snapshot(Page Page, Dictionary<string, JsonObject> Data, List<ReadResult> Reads,
    List<FieldRow> Rows, DateTimeOffset Time);
internal record CredentialRecord(string Path, string Serial, string Mac, string Address, DateTimeOffset Created=default)
{
    public override string ToString() => $"{Address} · {Serial}";
}
internal record RouterCredential(string UserName, string Password)
{
    public override string ToString()=>"Router credential (values withheld)";
}
internal class RouterException(string message, string code = "") : Exception(message)
{
    public string Code { get; } = code;
}

internal static class Catalog
{
    public static readonly Page[] Pages = [
        new("overview","Overview","Router status, firmware, Internet connection, and DNS.",["DEVICEOPERMODE_ALL","DEVICESTATUS_ALL","FIRMWARE_ALL","NETWORKDNS_ALL"]),
        new("mesh","Mesh and devices","Topology, connected devices, radio channels, and backhaul information.",["NETWORKMAP_ALL","HOSTS_ALL","LINKSPEEDBH_ALL"]),
        new("wifi","Wi-Fi","Main and guest Wi-Fi settings. Saving Wi-Fi changes may disconnect this PC.",["WIFI_24G_ALL","WIFI_5G_ALL","WIFI_GUEST_24G_ALL","WIFI_GUEST_5G_ALL","WIFI_FH_BANDSTEERING_ALL","WIFISETTINGSFEATURE_ALL"]),
        new("internet","Internet and DNS","DNS servers, WAN information, and IPv6 routing.",["NETWORKDNS_ALL","NETWORKIP_ALL","DEVICEOPERMODE_ALL","WANSETTINGSFEATURE_ALL"]),
        new("lan","LAN and DHCP","DHCP addressing, leases, subnets, and UPnP.",["IPV4DHCPRANGE_ALL","IPV6DHCPRANGE_ALL","IPV4SUBNET_ALL","IPV6SUBNET_ALL","NETWORKMISC_ALL","LANSETTINGSFEATURE_ALL"]),
        new("ports","Reservations and ports","Address reservations and port forwarding rules.",["STATICIP_ALL","PORTFORWARDING_ALL","NETWORKMISC_ALL"]),
        new("priority","Device priority","Priority settings and advertised QoS capabilities.",["DEVICEPRIORITY_ALL","DEVICEPRIORITYFEATURE_ALL","QoS_ALL","QoSFEATURE_ALL"]),
        new("parental","Parental controls","Parental control state, profiles, and device groups.",["PARENTAL_CONTROL_ALL","PC_PROFILE_ALL","PARENT_GROUP_DEVICES_ALL","PARENT_BLOCK_DEVICES_ALL","PARENTALCONTROLFEATURE_ALL"]),
        new("device","Device and firmware","Router details, installed firmware, and status light. Firmware downloads are paused.",["DEVICE_INFO_ALL","FIRMWARE_ALL","DEVICESTATUS_ALL","LED_DISPLAY_ALL","DEVICESETTINGSFEATURE_ALL"]),
        new("capabilities","Capabilities","Feature information returned by this firmware.",["CAPABILITIES_ALL","FEATURECAPABILITIES_ALL","DEVICESETTINGSFEATURE_ALL","WIFISETTINGSFEATURE_ALL","LANSETTINGSFEATURE_ALL","WANSETTINGSFEATURE_ALL","PARENTALCONTROLFEATURE_ALL","CLOUDSETTINGSFEATURE_ALL"])
    ];
    public static readonly HashSet<string> ReadKeys = Pages.SelectMany(p => p.Keys).ToHashSet(StringComparer.Ordinal);
    public static readonly Regex Secret = new("password|passwd|passphrase|psk|secret|token|credential|authorization|private.?key|sso.?key|wpa.*key|wep.*key|(^|_)pin($|_)|(^|_)key($|_)",RegexOptions.IgnoreCase);
    public static string Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";
    public static List<FieldRow> Flatten(JsonNode node, string source)
    {
        var rows = new List<FieldRow>();
        void Visit(JsonNode? n, string path, string leaf, int depth)
        {
            if (depth > 24 || rows.Count >= 5000) return;
            if (Secret.IsMatch(leaf)) {rows.Add(new(path,"[hidden]",source));return;}
            if (n is JsonObject o)
            {
                foreach (var kv in o) if (!(path == "" && kv.Key == "STATUS"))
                    Visit(kv.Value,path.Length == 0 ? kv.Key : path+"."+kv.Key,kv.Key,depth+1);
                if (o.Count == 0) rows.Add(new(path,"(empty object)",source));
            }
            else if (n is JsonArray a)
            {
                for (var i=0;i<a.Count && rows.Count<5000;i++) Visit(a[i],$"{path}[{i}]",leaf,depth+1);
                if (a.Count==0) rows.Add(new(path,"(empty list)",source));
            }
            else
            {
                var text = n is null ? "(null)" : Text(n);
                if (Regex.IsMatch(text,"\"(?:password|token|secret|passphrase|psk)\"\\s*:",RegexOptions.IgnoreCase)) text="[hidden]";
                rows.Add(new(path,text.Length>2000 ? text[..2000]+" [shortened]" : text,source));
            }
        }
        Visit(node,"","",0);if(rows.Count>=5000)rows.Add(new("[Display limit]","Additional fields were not rendered after 5,000 entries.",source));return rows;
    }
}

internal static class CredentialStore
{
    public static readonly string UserDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ArrisControl","Credentials");
    public static bool IsPrivate(string address)
    {
        if (!IPAddress.TryParse(address,out var ip) || ip.AddressFamily!=AddressFamily.InterNetwork) return false;
        var b=ip.GetAddressBytes();return b[0]==10 || (b[0]==192 && b[1]==168) || (b[0]==172 && b[1]>=16 && b[1]<=31);
    }
    public static CredentialRecord Inspect(string path)
    {
        if (new FileInfo(path).Length>131072) throw new RouterException("The credential record is too large.");
        var r=JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new RouterException("Invalid credential record.");
        var serial=Catalog.Text(r["TargetSerial"]);var mac=Catalog.Text(r["ExpectedMac"]);var address=Catalog.Text(r["RouterAddress"]);
        if (Catalog.Text(r["Format"])!="ARRIS-LocalCredential" || Catalog.Text(r["Version"])!="1" ||
            !Regex.IsMatch(serial,"^[A-Z0-9]{12,24}$") || !Regex.IsMatch(mac,"^[A-F0-9]{12}$") || !IsPrivate(address))
            throw new RouterException("Unsupported credential format or router address.");
        var created=DateTimeOffset.TryParse(Catalog.Text(r["CreatedAtUTC"]),out var timestamp)?timestamp:new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        return new(path,serial,mac,address,created);
    }
    public static List<CredentialRecord> Find(bool history=false)
    {
        var dirs=new HashSet<string>(StringComparer.OrdinalIgnoreCase){UserDirectory};
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        for(var i=0;i<8 && dir is not null;i++,dir=dir.Parent) dirs.Add(Path.Combine(dir.FullName,".local-credentials"));
        var list=new List<CredentialRecord>();
        foreach(var d in dirs.Where(Directory.Exists)) foreach(var p in Directory.EnumerateFiles(d,"*.json"))
            try{list.Add(Inspect(p));}catch(Exception ex) when(ex is IOException or JsonException or RouterException){}
        var sorted=list.OrderByDescending(r=>r.Created).ThenBy(r=>r.Path.StartsWith(UserDirectory,StringComparison.OrdinalIgnoreCase)?0:1).ToList();
        return history?sorted:sorted.DistinctBy(r=>(r.Serial,r.Mac)).ToList();
    }
    public static RouterCredential Load(CredentialRecord r,bool preserve=true)
    {
        var verified=Inspect(r.Path);
        if(verified.Serial!=r.Serial || verified.Mac!=r.Mac || verified.Address!=r.Address) throw new RouterException("Credential metadata changed. Select the router again.");
        var root=JsonNode.Parse(File.ReadAllText(r.Path))!;
        var cipher=Convert.FromBase64String(Catalog.Text(root["Ciphertext"]));
        var entropy=Encoding.UTF8.GetBytes($"ArrisLocalCredential.v1|{r.Serial}|{r.Mac}");byte[]? plain=null;
        try
        {
            plain=ProtectedData.Unprotect(cipher,entropy,DataProtectionScope.CurrentUser);
            using var doc=JsonDocument.Parse(plain);
            var user=doc.RootElement.GetProperty("UserName").GetString();var pass=doc.RootElement.GetProperty("Password").GetString();
            if(string.IsNullOrEmpty(user)||string.IsNullOrEmpty(pass)) throw new RouterException("Empty local credential.");
            if(preserve)PreserveLoaded(r);
            return new(user,pass);
        }
        catch(CryptographicException){throw new RouterException("Windows could not open this credential. Use the same Windows account that created it, or import a credential created on this PC.");}
        finally{CryptographicOperations.ZeroMemory(cipher);CryptographicOperations.ZeroMemory(entropy);if(plain is not null) CryptographicOperations.ZeroMemory(plain);}
    }
    public static CredentialRecord Import(string path)
    {
        var r=Inspect(path);_ = Load(r);Directory.CreateDirectory(UserDirectory);
        var target=Path.Combine(UserDirectory,$"{r.Serial}-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.json");
        File.Copy(path,target,false);return Inspect(target);
    }
    static void PreserveLoaded(CredentialRecord record)
    {
        if(Path.GetFullPath(record.Path).StartsWith(Path.GetFullPath(UserDirectory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return;
        Directory.CreateDirectory(UserDirectory);
        var hash=ReleaseUpdater.Hash(record.Path);
        var destination=Path.Combine(UserDirectory,$"loaded-{record.Serial}-{record.Mac}-{hash}.json");
        if(!File.Exists(destination))File.Copy(record.Path,destination,false);
    }
    internal static CredentialRecord SaveVerifiedAddress(CredentialRecord original,string address,string? directory=null)
    {
        if(!IsPrivate(address))throw new RouterException("A private router address is required.");
        var verified=Inspect(original.Path);
        if(verified.Serial!=original.Serial || verified.Mac!=original.Mac || verified.Address!=original.Address)throw new RouterException("Credential metadata changed during discovery.");
        var root=JsonNode.Parse(File.ReadAllText(original.Path))!.AsObject();
        root["RouterAddress"]=address;root["CreatedAtUTC"]=DateTimeOffset.UtcNow.ToString("o");root["LocalAuthenticationVerified"]=true;
        root["AddressVerification"]="Local certificate, authenticated exact serial and protected mode read";
        var folder=directory??UserDirectory;Directory.CreateDirectory(folder);
        var target=Path.Combine(folder,$"{original.Serial}-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.json");
        var temporary=target+".tmp";
        try{File.WriteAllText(temporary,root.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));File.Move(temporary,target);return Inspect(target);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static CredentialRecord SaveVerified(RouterCredential credential,CloudDevice device,string address,string? directory=null)
    {
        if(!IsPrivate(address) || !Regex.IsMatch(device.Serial,"^[A-Z0-9]{12,24}$") || !Regex.IsMatch(device.Mac,"^[A-F0-9]{12}$"))throw new RouterException("Invalid target identity for credential storage.");
        var plain=JsonSerializer.SerializeToUtf8Bytes(credential);var entropy=Encoding.UTF8.GetBytes($"ArrisLocalCredential.v1|{device.Serial}|{device.Mac}");byte[]? check=null;string? temporary=null;
        try
        {
            var cipher=ProtectedData.Protect(plain,entropy,DataProtectionScope.CurrentUser);
            check=ProtectedData.Unprotect(cipher,entropy,DataProtectionScope.CurrentUser);
            if(!CryptographicOperations.FixedTimeEquals(plain,check))throw new RouterException("Windows credential-protection verification failed. No record was saved.");
            var record=new{Format="ARRIS-LocalCredential",Version=1,TargetSerial=device.Serial,ExpectedMac=device.Mac,RouterAddress=address,
                CreatedAtUTC=DateTimeOffset.UtcNow,Protection="Windows DPAPI CurrentUser",LocalAuthenticationVerified=true,
                CredentialVerifiedVia="Cloud account SSO then direct local login and protected serial/mode reads",CloudTokensSaved=false,Ciphertext=Convert.ToBase64String(cipher)};
            var folder=directory??UserDirectory;Directory.CreateDirectory(folder);var target=Path.Combine(folder,$"{device.Serial}-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.json");
            temporary=target+".tmp";File.WriteAllText(temporary,JsonSerializer.Serialize(record,new JsonSerializerOptions{WriteIndented=true}));File.Move(temporary,target);temporary=null;
            return Inspect(target);
        }
        catch(CryptographicException){throw new RouterException("Windows could not protect this credential for the current user. Nothing was saved; plaintext storage is not used.");}
        finally{CryptographicOperations.ZeroMemory(plain);CryptographicOperations.ZeroMemory(entropy);if(check is not null)CryptographicOperations.ZeroMemory(check);if(temporary is not null && File.Exists(temporary))File.Delete(temporary);}
    }
}

internal interface IRouterWire : IDisposable
{
    Task<JsonObject> SendAsync(string path, string? body=null, CancellationToken token=default);
}
internal sealed class LocalWire : IRouterWire
{
    readonly HttpClient client;readonly Uri origin;string? cookie;
    public LocalWire(CredentialRecord r)
    {
        if(!CredentialStore.IsPrivate(r.Address))throw new RouterException("A private router IPv4 address is required.");
        origin=new Uri($"https://{r.Address}:50011/");
        var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false};
        handler.ServerCertificateCustomValidationCallback=(_,cert,_,_)=>cert is not null &&
            cert.GetNameInfo(X509NameType.SimpleName,false).Equals(r.Mac,StringComparison.OrdinalIgnoreCase) &&
            DateTime.Now>=cert.NotBefore && DateTime.Now<=cert.NotAfter;
        client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(15)};
    }
    public async Task<JsonObject> SendAsync(string path,string? body=null,CancellationToken token=default)
    {
        var allowed=body is null ? path.StartsWith("/getData?key=",StringComparison.Ordinal) && Catalog.ReadKeys.Contains(path[13..]) : path is "/loginAction" or "/setData";
        if(!allowed) throw new RouterException("Request is outside the fixed local management API.");
        if(path=="/setData") Settings.ValidateEnvelope(body!);
        using var request=new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,new Uri(origin,path));
        if(cookie is not null)request.Headers.TryAddWithoutValidation("Cookie",cookie);
        if(body is not null)request.Content=new StringContent(body,Encoding.UTF8,path=="/loginAction" ? "application/x-www-form-urlencoded" : "application/json");
        using var reply=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        if(path=="/loginAction" && reply.Headers.TryGetValues("Set-Cookie",out var cookies))
            foreach(var value in cookies)
            {
                var match=Regex.Match(value,@"(?:^|,\s*)MOBAPPSESSID=([^;\r\n]*)");
                if(match.Success && Regex.IsMatch(match.Groups[1].Value,@"^[A-Za-z0-9._~,+/=:\-]{1,512}$"))cookie="MOBAPPSESSID="+match.Groups[1].Value;
            }
        if(reply.StatusCode!=HttpStatusCode.OK)throw new RouterException($"Router returned HTTP {(int)reply.StatusCode}.","HTTP"+(int)reply.StatusCode);
        await using var input=await reply.Content.ReadAsStreamAsync(token);
        using var memory=new MemoryStream();var buffer=new byte[65536];int count;
        while((count=await input.ReadAsync(buffer,token))>0){if(memory.Length+count>8*1024*1024)throw new RouterException("Router response exceeded the display limit.");await memory.WriteAsync(buffer.AsMemory(0,count),token);}
        return ParseResponse(memory.ToArray(),path=="/loginAction");
    }
    internal static JsonObject ParseResponse(byte[] body,bool login=false)
    {
        JsonObject? response;
        try{response=JsonNode.Parse(body)?["RESPONSE"] as JsonObject;}catch(JsonException){throw new RouterException("Router returned an unexpected response.");}
        var status=Catalog.Text(response?["STATUS"]);
        if(response is null || !(status=="OK" || login && status=="Login-OK"))throw new RouterException("Router status: "+(Regex.IsMatch(status,"^[A-Za-z][A-Za-z0-9_. -]{0,79}$")?status:"unrecognized"),status);
        return response;
    }
    public void Dispose()=>client.Dispose();
}

internal sealed class RouterClient : IDisposable
{
    CredentialRecord record;IRouterWire wire;bool authenticated;readonly bool testTransport;
    public CredentialRecord Record=>record;
    public Action<string>? Progress {get;set;}
    public RouterClient(CredentialRecord r,IRouterWire? testWire=null){record=r;wire=testWire??new LocalWire(r);authenticated=testWire is not null;testTransport=testWire is not null;}
    static string EncodeAndroid(string s)
    {
        const string safe="abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-!.~'()*";
        var sb=new StringBuilder();foreach(var b in Encoding.UTF8.GetBytes(s))if(safe.Contains((char)b))sb.Append((char)b);else sb.Append('%').Append(b.ToString("X2"));return sb.ToString();
    }
    internal static string LoginBody(string user,string password,string id,long time)
    {
        var bytes=Encoding.UTF8.GetBytes(EncodeAndroid(user)+":"+EncodeAndroid(password));
        try
        {
            var b64=Convert.ToBase64String(bytes);var wrapped=string.Join("\n",Enumerable.Range(0,(b64.Length+75)/76).Select(i=>b64.Substring(i*76,Math.Min(76,b64.Length-i*76))));
            return $"arg={wrapped}&_n=08292&_={time}&_uuid={id}&_content=json";
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    public async Task ConnectAsync(CancellationToken token=default)
    {
        if(testTransport && authenticated)return;
        Progress?.Invoke("Locating the selected router on local networks…");
        var address=await RouterDiscovery.ResolveAsync(record,token);
        if(address!=record.Address)authenticated=false;
        if(authenticated)return;
        wire.Dispose();wire=new LocalWire(record with{Address=address});
        var previous=CredentialStore.Inspect(record.Path);
        if(previous.Serial!=record.Serial || previous.Mac!=record.Mac)throw new RouterException("Credential identity changed during discovery.");
        // The encrypted record still contains its old address until serial/mode verification succeeds.
        var credential=CredentialStore.Load(previous);
        Progress?.Invoke("Verifying local router identity at "+address+"…");
        await wire.SendAsync("/loginAction",LoginBody(credential.UserName,credential.Password,"abcxyz123_windows_"+Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),token);
        await VerifyIdentityAsync(token);_ = await ReadAsync("DEVICEOPERMODE_ALL",token);authenticated=true;
        record=record with{Address=address};
        if(address!=previous.Address)
        {
            try{record=CredentialStore.SaveVerifiedAddress(previous,address);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Progress?.Invoke("Connected at "+address+"; the updated profile could not be saved.");}
        }
    }
    internal async Task VerifyIdentityAsync(CancellationToken token=default)
    {
        var info=await ReadAsync("DEVICE_INFO_ALL",token);
        if(Catalog.Text(info["DEVICE_INFO"]?["SERIAL_KEY"])!=record.Serial)throw new RouterException("The returned router serial does not match the selected credential. No setting was changed.");
    }
    internal Task<JsonObject> ReadAsync(string key,CancellationToken token=default)
    {
        if(!Catalog.ReadKeys.Contains(key))throw new RouterException("Unknown read query.");return wire.SendAsync("/getData?key="+key,null,token);
    }
    public async Task<Snapshot> ReadPageAsync(Page page,CancellationToken token=default)
    {
        await ConnectAsync(token);
        var data=new Dictionary<string,JsonObject>();var reads=new List<ReadResult>();var rows=new List<FieldRow>();
        foreach(var key in page.Keys)
        {
            try{var value=await ReadAsync(key,token);data[key]=value;reads.Add(new(key,200,"OK",DateTimeOffset.Now,true));rows.AddRange(Catalog.Flatten(value,key));}
            catch(RouterException ex){reads.Add(new(key,null,ex.Code.Length>0?ex.Code:ex.Message,DateTimeOffset.Now,false));if(ex.Code=="SessionTimeout"){authenticated=false;break;}}
            catch(HttpRequestException){reads.Add(new(key,null,"Connection failed",DateTimeOffset.Now,false));}
            catch(TaskCanceledException) when(!token.IsCancellationRequested){reads.Add(new(key,null,"Timed out",DateTimeOffset.Now,false));}
        }
        return new(page,data,reads,rows,DateTimeOffset.Now);
    }
    public async Task<PendingChange> PrepareAsync(SettingSection section,Dictionary<string,string> edits,string operation="MODIFY",CancellationToken token=default)
    {
        await ConnectAsync(token);await VerifyIdentityAsync(token);
        var fresh=await ReadAsync(section.Definition.Key,token);
        var current=Settings.Locate(fresh,section.Container,section.Index,section.Identity);
        if(operation!="ADD" && !Settings.SameValues(current,section.Baseline))throw new RouterException("These settings changed since the page was loaded. Refresh before saving.");
        return Settings.Prepare(section.Definition,section.Container,current,edits,operation,section.Index,section.Identity);
    }
    public async Task<string> ApplyAsync(PendingChange plan,CancellationToken token=default)
    {
        Settings.ValidateEnvelope(plan.Body);await ConnectAsync(token);await VerifyIdentityAsync(token);
        var fresh=await ReadAsync(plan.Definition.Key,token);
        if(plan.Operation!="ADD" && !Settings.SameValues(Settings.Locate(fresh,plan.Container,plan.Index,plan.Identity),plan.Before))
            throw new RouterException("Settings changed after the preview. Refresh and review them again; no update was sent.");
        // Exactly one modifying request. A lost reply must never cause an automatic retry.
        try{await wire.SendAsync("/setData",plan.Body,token);}
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException)
        {throw new RouterException("The save request was sent, but its result is unknown. Reconnect if necessary and refresh; do not assume it failed.");}
        try
        {
            var after=await ReadAsync(plan.Definition.Key,token);
            var actual=Settings.Locate(after,plan.Container,plan.Index,plan.Identity);
            if(plan.Operation=="DELETE")
            {
                if(actual is null)return "Rule removed and verified by reading the router.";
                return "Router accepted the request, but removal was not verified. Refresh before another change.";
            }
            if(plan.Operation=="ADD")actual=Settings.FindMatch(after,plan.Container,plan.Expected);
            var verification=Settings.VerifyValues(actual,plan.Expected);
            return verification ? "Saved and verified by reading the router." : "Router accepted the request, but not all changed fields were verified. Refresh before another change.";
        }
        catch(Exception ex) when(ex is RouterException or HttpRequestException or TaskCanceledException)
        {return "Router accepted the save, but read-back failed. Reconnect if necessary and refresh. No save was retried.";}
    }
    public void Dispose()=>wire.Dispose();
}
