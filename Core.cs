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
// CertificatePin lives only inside the encrypted payload, so editing the plaintext
// record metadata (for example RouterAddress) cannot redirect a login elsewhere.
internal record RouterCredential(string UserName, string Password, string? CertificatePin=null)
{
    public override string ToString()=>"Router credential (values withheld)";
}
internal class RouterException(string message, string code = "") : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// SHA-256 pin of the router's TLS public key (SubjectPublicKeyInfo, base64).
/// The certificate CN is the router MAC. Any LAN client can read it from the real router
/// and copy it into a self-signed certificate, so the CN alone does not identify the unit.
/// </summary>
internal static class CertificatePin
{
    public static string Compute(X509Certificate2 certificate)=>
        Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    public static bool IsValid(string? pin)=>pin is not null && Regex.IsMatch(pin,"^[A-Za-z0-9+/]{43}=$");
    /// <summary>MAC CN and validity dates; when a pin is known, also an exact public-key match.</summary>
    public static bool Accepts(X509Certificate2 certificate,string mac,string? pin,DateTime now)
    {
        if(!RouterDiscovery.CertificateMatches(certificate,mac,now))return false;
        if(pin is null)return true;
        if(!IsValid(pin))return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(certificate)),Encoding.ASCII.GetBytes(pin));
    }
    /// <summary>Short, human-comparable form for display.</summary>
    public static string Fingerprint(string pin)
    {
        var hex=Convert.ToHexString(Convert.FromBase64String(pin));
        return string.Join(":",Enumerable.Range(0,8).Select(i=>hex.Substring(i*2,2)));
    }
}

internal static class Catalog
{
    public static readonly Page[] Pages = [
        new("overview","Overview","Router status, firmware, Internet connection, and DNS.",["DEVICEOPERMODE_ALL","DEVICESTATUS_ALL","FIRMWARE_ALL","NETWORKDNS_ALL"]),
        new("mesh","Mesh and devices","Topology, connected devices, radio channels, and backhaul information.",["NETWORKMAP_ALL","HOSTS_ALL","LINKSPEEDBH_ALL","WIFI_BH_ALL","SPEEDTESTBH_ALL","WIFI_TRIBAND_ALL","HNC_CLIENT_STEERING_ALL"]),
        new("wifi","Wi-Fi","Main and guest Wi-Fi settings. Saving Wi-Fi changes may disconnect this PC.",["WIFI_24G_ALL","WIFI_5G_ALL","WIFI_GUEST_24G_ALL","WIFI_GUEST_5G_ALL","WIFI_FH_BANDSTEERING_ALL","WIFI_FH_DFS_ALL","WIFI_FAST_LANE_ALL","WIFISETTINGSFEATURE_ALL"]),
        new("internet","Internet and DNS","DNS servers, WAN information, and IPv6 routing.",["NETWORKDNS_ALL","NETWORKIP_ALL","DEVICEOPERMODE_ALL","WANSTATICIPV4_ALL","WANDYNAMICIPV6_ALL","WANSTATICIPV6_ALL","WANLAG_ALL","WANSETTINGSFEATURE_ALL"]),
        new("lan","LAN and DHCP","DHCP addressing, leases, subnets, and UPnP.",["IPV4DHCPRANGE_ALL","IPV6DHCPRANGE_ALL","IPV4SUBNET_ALL","IPV6SUBNET_ALL","NETWORKMISC_ALL","LANSETTINGSFEATURE_ALL"]),
        new("ports","Reservations and ports","Address reservations and port forwarding rules.",["STATICIP_ALL","PORTFORWARDING_ALL","NETWORKMISC_ALL"]),
        new("priority","Device priority","Priority settings and advertised QoS capabilities.",["DEVICEPRIORITY_ALL","DEVICEPRIORITYFEATURE_ALL","QoS_ALL","QoSFEATURE_ALL"]),
        new("parental","Parental controls","Parental control state, profiles, and device groups.",["PARENTAL_CONTROL_ALL","PC_PROFILE_ALL","PARENT_GROUP_DEVICES_ALL","PARENT_BLOCK_DEVICES_ALL","PC_URLS_BLOCKED_ALL","PARENT_URL_FILTER_ALL","PARENT_KEYWORD_FILTER_ALL","PARENTALCONTROLFEATURE_ALL"]),
        new("device","Device and firmware","Router details, installed firmware, and status light. Firmware downloads are paused.",["DEVICE_INFO_ALL","FIRMWARE_ALL","DEVICESTATUS_ALL","LED_DISPLAY_ALL","LEDRING_ALL","TIME_ALL","DEVICESETTINGSFEATURE_ALL"]),
        new("tools","Tools and tests","Run a speed test from the router and renew its Internet connection.",["SPEEDTESTWAN_ALL","WANSPEEDTESTFEATURE_ALL","WANSETTINGSFEATURE_ALL"]),
        new("capabilities","Capabilities","Feature information returned by this firmware.",["CAPABILITIES_ALL","FEATURECAPABILITIES_ALL","DEVICESETTINGSFEATURE_ALL","WIFISETTINGSFEATURE_ALL","LANSETTINGSFEATURE_ALL","WANSETTINGSFEATURE_ALL","PARENTALCONTROLFEATURE_ALL","CLOUDSETTINGSFEATURE_ALL"]),
        Diagnostics.Page
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
            plain=CredentialProtection.Unprotect(cipher,entropy);
            using var doc=JsonDocument.Parse(plain);
            var user=doc.RootElement.GetProperty("UserName").GetString();var pass=doc.RootElement.GetProperty("Password").GetString();
            if(string.IsNullOrEmpty(user)||string.IsNullOrEmpty(pass)) throw new RouterException("Empty local credential.");
            string? pin=null;
            if(doc.RootElement.TryGetProperty("CertificatePin",out var pinned) && pinned.ValueKind!=JsonValueKind.Null)
            {
                pin=pinned.ValueKind==JsonValueKind.String?pinned.GetString():null;
                if(!CertificatePin.IsValid(pin))throw new RouterException("This credential contains an invalid router certificate pin. Acquire the credential again.");
            }
            if(preserve)PreserveLoaded(r);
            return new(user,pass,pin);
        }
        catch(CryptographicException){throw new RouterException("This device could not open this credential. Use the device and account that created this credential. Windows DPAPI files cannot be opened on Android.");}
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
        using var input=File.OpenRead(record.Path);
        var hash=Convert.ToHexString(SHA256.HashData(input));
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
    /// <summary>Saves a credential verified by local login. The certificate pin observed during
    /// that login is encrypted with the credential and required on every later connection.</summary>
    public static CredentialRecord SaveVerified(RouterCredential credential,CloudDevice device,string address,string certificatePin,string? directory=null,
        string verifiedVia="Cloud account SSO then direct local login and protected serial/mode reads")
    {
        if(!IsPrivate(address) || !Regex.IsMatch(device.Serial,"^[A-Z0-9]{12,24}$") || !Regex.IsMatch(device.Mac,"^[A-F0-9]{12}$"))throw new RouterException("Invalid target identity for credential storage.");
        if(!CertificatePin.IsValid(certificatePin))throw new RouterException("The router certificate could not be pinned. No credential was saved.");
        var plain=JsonSerializer.SerializeToUtf8Bytes(credential with{CertificatePin=certificatePin});var entropy=Encoding.UTF8.GetBytes($"ArrisLocalCredential.v1|{device.Serial}|{device.Mac}");byte[]? check=null;string? temporary=null;
        try
        {
            var cipher=CredentialProtection.Protect(plain,entropy);
            check=CredentialProtection.Unprotect(cipher,entropy);
            if(!CryptographicOperations.FixedTimeEquals(plain,check))throw new RouterException("Credential-protection verification failed. No record was saved.");
            var record=new{Format="ARRIS-LocalCredential",Version=1,TargetSerial=device.Serial,ExpectedMac=device.Mac,RouterAddress=address,
                CreatedAtUTC=DateTimeOffset.UtcNow,Protection=CredentialProtection.Description,LocalAuthenticationVerified=true,
                CredentialVerifiedVia=verifiedVia,CertificatePinned=true,CloudTokensSaved=false,Ciphertext=Convert.ToBase64String(cipher)};
            var folder=directory??UserDirectory;Directory.CreateDirectory(folder);var target=Path.Combine(folder,$"{device.Serial}-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.json");
            temporary=target+".tmp";File.WriteAllText(temporary,JsonSerializer.Serialize(record,new JsonSerializerOptions{WriteIndented=true}));File.Move(temporary,target);temporary=null;
            return Inspect(target);
        }
        catch(CryptographicException){throw new RouterException("This device could not protect this credential for the current user. Nothing was saved; plaintext storage is not used.");}
        finally{CryptographicOperations.ZeroMemory(plain);CryptographicOperations.ZeroMemory(entropy);if(check is not null)CryptographicOperations.ZeroMemory(check);if(temporary is not null && File.Exists(temporary))File.Delete(temporary);}
    }
    /// <summary>Upgrades a credential saved before pinning existed: re-encrypts it with the pin
    /// observed during a successful login at its saved address. The original record is kept.</summary>
    internal static CredentialRecord SavePinned(CredentialRecord original,RouterCredential credential,string address,string certificatePin,string? directory=null)
    {
        var verified=Inspect(original.Path);
        if(verified.Serial!=original.Serial || verified.Mac!=original.Mac)throw new RouterException("Credential metadata changed during verification.");
        return SaveVerified(credential,new CloudDevice("",original.Serial,original.Mac),address,certificatePin,directory,
            "Earlier verified credential; certificate pinned at a local login with authenticated serial/mode reads");
    }
}

internal interface IRouterWire : IDisposable
{
    Task<JsonObject> SendAsync(string path, string? body=null, CancellationToken token=default);
}
internal sealed class LocalWire : IRouterWire
{
    readonly HttpClient client;readonly Uri origin;string? cookie;readonly object gate=new();string? observedPin;
    /// <summary>Public-key pin of the certificate this connection accepted. With no expected pin
    /// (first verification), the first certificate seen is locked in for the wire's lifetime.</summary>
    public string? ObservedPin{get{lock(gate)return observedPin;}}
    public LocalWire(CredentialRecord r,string? expectedPin=null)
    {
        if(!CredentialStore.IsPrivate(r.Address))throw new RouterException("A private router IPv4 address is required.");
        if(expectedPin is not null && !CertificatePin.IsValid(expectedPin))throw new RouterException("Invalid router certificate pin.");
#if ANDROID
        AndroidNetwork.UseLocal();
#endif
        origin=new Uri($"https://{r.Address}:50011/");
        var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false};
        handler.ServerCertificateCustomValidationCallback=(_,cert,_,_)=>
        {
            if(cert is null || !CertificatePin.Accepts(cert,r.Mac,expectedPin,DateTime.Now))return false;
            var pin=CertificatePin.Compute(cert);
            lock(gate)
            {
                if(observedPin is null){observedPin=pin;return true;}
                return observedPin==pin;
            }
        };
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
    CredentialRecord record;IRouterWire wire;bool authenticated;readonly bool testTransport;string? pin;
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
#if ANDROID
        AndroidNetwork.UseLocal();
#endif
        if(authenticated && pin is not null)
        {
            Progress?.Invoke("Locating the selected router on local networks…");
            if(await RouterDiscovery.ResolveAsync(record,pin,token)==record.Address)return;
            authenticated=false;
        }
        var previous=CredentialStore.Inspect(record.Path);
        if(previous.Serial!=record.Serial || previous.Mac!=record.Mac)throw new RouterException("Credential identity changed during discovery.");
        // The pin comes from the encrypted payload, never from editable record metadata.
        var credential=CredentialStore.Load(previous);pin=credential.CertificatePin;
        Progress?.Invoke(pin is null?"Checking the saved router address (certificate not yet pinned)…":"Locating the selected router on local networks…");
        var address=await RouterDiscovery.ResolveAsync(record,pin,token);
        wire.Dispose();var local=new LocalWire(record with{Address=address},pin);wire=local;
        Progress?.Invoke("Verifying local router identity at "+address+"…");
        await wire.SendAsync("/loginAction",LoginBody(credential.UserName,credential.Password,"abcxyz123_windows_"+Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),token);
        await VerifyIdentityAsync(token);_ = await ReadAsync("DEVICEOPERMODE_ALL",token);authenticated=true;
        record=record with{Address=address};
        try
        {
            if(pin is null)
            {
                var observed=local.ObservedPin;
                if(CertificatePin.IsValid(observed))
                {
                    pin=observed; // keep this session pinned even if saving the upgraded record fails
                    record=CredentialStore.SavePinned(previous,credential,address,observed!);
                    Progress?.Invoke("Router certificate pinned ("+CertificatePin.Fingerprint(observed!)+"). Future connections require this exact certificate.");
                }
            }
            else if(address!=previous.Address)record=CredentialStore.SaveVerifiedAddress(previous,address);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or RouterException)
        {Progress?.Invoke("Connected at "+address+"; the updated profile could not be saved.");}
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
            catch(HttpRequestException ex){reads.Add(new(key,null,RouterDiscovery.ConnectionFailure(ex,record.Address),DateTimeOffset.Now,false));}
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
    /// <summary>Sends one allowlisted action (renew WAN, start speed test) after re-verifying the router. Never retried.</summary>
    public async Task RunActionAsync(string container,Dictionary<string,string> values,CancellationToken token=default)
    {
        var body=Settings.Action(container,values);
        await ConnectAsync(token);await VerifyIdentityAsync(token);
        try{await wire.SendAsync("/setData",body,token);}
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException)
        {throw new RouterException("The request was sent, but its result is unknown. Refresh before trying again; it was not retried.");}
    }
    public void Dispose()=>wire.Dispose();
}

/// <summary>
/// Read-only feature check. Queries a fixed list of endpoints, then reports which ones answered
/// and the field names they returned, with anything identifying replaced. Never writes.
/// Credential, onboarding and cloud-configuration endpoints are deliberately excluded.
/// </summary>
internal static class Diagnostics
{
    public static readonly string[] Keys=[
        "DEVICE_INFO_ALL","FIRMWARE_ALL","DEVICEOPERMODE_ALL","DEVICESTATUS_ALL","CAPABILITIES_ALL","FEATURECAPABILITIES_ALL",
        "WIFISETTINGSFEATURE_ALL","WANSETTINGSFEATURE_ALL","LANSETTINGSFEATURE_ALL","DEVICESETTINGSFEATURE_ALL","PARENTALCONTROLFEATURE_ALL",
        "QoSFEATURE_ALL","DEVICEPRIORITYFEATURE_ALL","WANSPEEDTESTFEATURE_ALL","WIFISPEEDTESTFEATURE_ALL","SUBSCRIPTIONSFEATURE_ALL",
        "WIFI_24G_ALL","WIFI_5G_ALL","WIFI_GUEST_24G_ALL","WIFI_GUEST_5G_ALL","WIFI_FH_BANDSTEERING_ALL","WIFI_FH_DFS_ALL",
        "WIFI_FAST_LANE_ALL","WIFI_TRIBAND_ALL","HNC_CLIENT_STEERING_ALL","WIFI_BH_ALL","LINKSPEEDBH_ALL","SPEEDTESTBH_ALL",
        "NETWORKMAP_ALL","PLACEMENT_ALL","WPS","NETWORKDNS_ALL","NETWORKIP_ALL","NETWORKMISC_ALL","DNSPROXY_ALL",
        "WANSTATICIPV4_ALL","WANDYNAMICIPV6_ALL","WANSTATICIPV6_ALL","WANLAG_ALL","SPEEDTESTWAN_ALL",
        "IPV4DHCPRANGE_ALL","IPV6DHCPRANGE_ALL","IPV4SUBNET_ALL","IPV6SUBNET_ALL","STATICIP_ALL","PORTFORWARDING_ALL",
        "DEVICEPRIORITY_ALL","QoS_ALL","PARENTAL_CONTROL_ALL","PC_PROFILE_ALL","PC_URLS_BLOCKED_ALL","PARENT_URL_FILTER_ALL",
        "PARENT_KEYWORD_FILTER_ALL","TRUSTED_DEVICE_ALL","LED_DISPLAY_ALL","LEDRING_ALL","TIME_ALL","UPDATE_ALL"];
    public static readonly Page Page=new("diagnostics","Diagnostics",
        "Read-only check of which router features this firmware supports. Nothing is changed. Values that could identify you are replaced.",Keys);
    const int MaxLinesPerKey=60;
    static readonly Regex Personal=new("SSID|NAME|ALIAS|HOST|URL|DESCRIPTION|SERIAL|FRIENDLY|TEXT|LABEL|EMAIL|USER|ADDRESS|MAC|BSSID|KEYWORD|ACCOUNT|ID$|IPV4|IPV6|GATEWAY|DNS",RegexOptions.IgnoreCase);
    static readonly Regex Ipv4=new(@"^\d{1,3}(?:\.\d{1,3}){3}$");
    static readonly Regex Mac=new("^(?:[0-9A-Fa-f]{2}[:-]?){5}[0-9A-Fa-f]{2}$");

    /// <summary>Describes one value without revealing anything identifying.</summary>
    internal static string Value(string field,string value)
    {
        if(Catalog.Secret.IsMatch(field))return "[hidden]";
        if(value.Length==0)return "(empty)";
        if(value.Split(',').All(v=>Ipv4.IsMatch(v.Trim())))return value.Contains(',')?"<ipv4 list>":"<ipv4>";
        if(value.Contains(':') && value.Split(',').All(v=>IPAddress.TryParse(v.Trim(),out var ip) && ip.AddressFamily==AddressFamily.InterNetworkV6))
            return value.Contains(',')?"<ipv6 list>":"<ipv6>";
        if(Mac.IsMatch(value))return "<mac>";
        if(Regex.IsMatch(field,"MODEL|VERSION",RegexOptions.IgnoreCase) && Regex.IsMatch(value,"^[A-Za-z0-9_.+ -]{1,40}$"))return value;
        if(Personal.IsMatch(field))return $"<text, {value.Length} chars>";
        if(Regex.IsMatch(value,"^[A-Za-z0-9_.+-]{1,20}$"))return value;
        return $"<text, {value.Length} chars>";
    }
    /// <summary>Field paths of a response with described values; lists show their length and first two items.</summary>
    internal static List<string> Describe(JsonNode node)
    {
        var lines=new List<string>();
        void Visit(JsonNode? n,string path,string leaf,int depth)
        {
            if(lines.Count>=MaxLinesPerKey || depth>12)return;
            if(n is JsonObject o)
            {
                foreach(var kv in o)if(!(path.Length==0 && kv.Key=="STATUS"))Visit(kv.Value,path.Length==0?kv.Key:path+"."+kv.Key,kv.Key,depth+1);
                if(o.Count==0)lines.Add(path+" = {}");
            }
            else if(n is JsonArray a)
            {
                lines.Add($"{path} = list of {a.Count}");
                for(var i=0;i<Math.Min(a.Count,2);i++)Visit(a[i],$"{path}[{i}]",leaf,depth+1);
            }
            else lines.Add(path+" = "+Value(leaf,n is null?"null":Catalog.Text(n)));
        }
        Visit(node,"","",0);
        if(lines.Count>=MaxLinesPerKey)lines.Add("… more fields not shown");
        return lines;
    }
    public static string Report(Snapshot snapshot,string platform,string version)
    {
        string Pick(string key,string leafPattern)
        {
            if(!snapshot.Data.TryGetValue(key,out var data))return "unknown";
            var row=Catalog.Flatten(data,key).FirstOrDefault(r=>Regex.IsMatch(r.Field.Split('.').Last(),leafPattern,RegexOptions.IgnoreCase) && r.Value!="[hidden]");
            return row is null ? "unknown" : Value(row.Field.Split('.').Last(),row.Value);
        }
        var sb=new StringBuilder();
        sb.AppendLine($"Arris Control diagnostics · {platform} {version} · {snapshot.Time:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Router model {Pick("DEVICE_INFO_ALL","MODEL")} · firmware {Pick("FIRMWARE_ALL","VERSION")} · mode {Pick("DEVICEOPERMODE_ALL","^MODE$")}");
        sb.AppendLine($"{snapshot.Reads.Count(r=>r.Readable)}/{snapshot.Reads.Count} endpoints answered · read-only, nothing was changed");
        foreach(var read in snapshot.Reads)
        {
            sb.AppendLine();
            sb.AppendLine(read.Readable ? $"[OK] {read.Key}" : $"[--] {read.Key}  ({read.Status})");
            if(read.Readable && snapshot.Data.TryGetValue(read.Key,out var data))foreach(var line in Describe(data))sb.AppendLine("     "+line);
        }
        return sb.ToString();
    }
}
