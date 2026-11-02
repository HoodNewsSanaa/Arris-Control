using System.Net.Http;
using System.Text.Json.Nodes;

namespace ArrisControl;

// A separate, fixed onboarding interface. No generic commands, root exploits,
// reset, cloud configuration, firmware actions or Wi-Fi password reads.
internal static class WobaProtocol
{
    public const string Login="/woba_loginAction", Set="/woba_setData";
    internal static readonly Dictionary<string,(string Container,string[] Fields)> Reads=new(StringComparer.Ordinal){
        ["OB_DEVICEINFO_ALL"]=("OB_DEVICEINFO",["MAX_WAN_MAC","MAX_SERIAL_NO","MAX_MODEL_NO","MAX_WiFi_MAC_BH","FH_24G_BSSID","FH_5G_BSSID"]),
        ["OB_DEVICEMODE_ALL"]=("OB_DEVICEMODE",["DEVICE_MODE"]),
        ["OB_CAPABILITIES_ALL"]=("CAPABILITIES",["MODEL","FW_VERSION","LCA_TYPE","COBA_VERSION","WOBA_VERSION","CAPTCHA"]),
        ["OB_WANSTATUS_ALL"]=("OB_WANSTATUS",["WAN_STATUS"]),
        ["OB_SWUPDATESTATUS_ALL"]=("OB_SWUPDATESTATUS",["UPGRADE_STATUS","CURRENT_VERSION","UPGRADE_VERSION","PROGRESS_PERCENTAGE"]),
        ["OB_DEVICESTATUS_ALL"]=("OB_DEVICESTATUS",["MAINAP_STATE","NUM_SATELLITES"]),
        ["OB_POSTRESET_SETTINGS_ALL"]=("OB_POSTRESET_SETTINGS",["FH_ADVERTISEMENT","FHADVERTISEMENT"])
    };
    public static string ReadPath(string key)=>Reads.ContainsKey(key)?"/woba_getData?key="+key:throw new RouterException("Unsupported onboarding query.");
    public static bool Allows(string path,string? body)=>body is null?Reads.Keys.Any(k=>path==ReadPath(k)):path is Login or Set;
    public static JsonObject Project(string key,JsonObject response)
    {
        var (container,fields)=Reads[key];var value=response[container];
        JsonObject? raw=value is JsonArray array?array.OfType<JsonObject>().FirstOrDefault():value as JsonObject;
        var safe=new JsonObject();if(raw is not null)foreach(var name in fields)
            if(raw[name] is JsonValue v && Catalog.Text(v).Length<=512)safe[name]=v.DeepClone();
        if(key=="OB_DEVICESTATUS_ALL" && raw?["SAT_LIST"] is JsonArray satellites)
        {
            var list=new JsonArray();foreach(var row in satellites.OfType<JsonObject>().Take(128))
            {
                var item=new JsonObject();foreach(var name in new[]{"MAX_SERIAL_NO","MAX_MODEL_NO","MAX_WAN_MAC","SAT_STATE"})
                    if(row[name] is JsonValue rowValue && Catalog.Text(rowValue).Length<=512)item[name]=rowValue.DeepClone();
                list.Add(item);
            }
            safe["SAT_LIST"]=list;
        }
        return new JsonObject{[container]=safe};
    }
    public static string Report(WobaInspection inspection)
    {
        var report=new System.Text.StringBuilder();
        report.AppendLine("Arris Control · local WOBA diagnostics · "+inspection.Time.ToString("u"));
        report.AppendLine("Model: "+Diagnostics.Value("MODEL",inspection.Device.Model)+" · Firmware: "+Diagnostics.Value("VERSION",inspection.Firmware)+" · Role: "+Diagnostics.Value("ROLE",inspection.Role));
        report.AppendLine($"{inspection.Reads.Count(r=>r.Readable)}/{inspection.Reads.Count} endpoints answered · read-only inspection");
        foreach(var read in inspection.Reads)
        {
            report.AppendLine();report.AppendLine(read.Readable?"[OK] "+read.Key:"[--] "+read.Key+" ("+read.Status+")");
            if(read.Readable&&inspection.Data.TryGetValue(read.Key,out var data))foreach(var line in Diagnostics.Describe(data))report.AppendLine("  "+line);
        }
        return report.ToString();
    }
    public static void ValidateCredential(RouterCredential credential)
    {
        if(credential.UserName.Length is <1 or >64 || credential.Password.Length is <12 or >64 ||
            (credential.UserName+credential.Password).Any(c=>c<32||c>126))
            throw new RouterException("Use 1–64 printable ASCII characters for the local username and 12–64 for the new admin password.");
    }
    public static string Body(RouterCredential credential)
    {
        ValidateCredential(credential);return new JsonObject{["REQUEST"]=new JsonObject{["OPERTYPE"]="MODIFY",["OB_LCAINFO"]=new JsonObject{
            ["LCA_USERNAME"]=credential.UserName,["LCA_PASSWORD"]=credential.Password}}}.ToJsonString();
    }
    public static void ValidateBody(string body)
    {
        var root=JsonNode.Parse(body) as JsonObject;var req=root?["REQUEST"] as JsonObject;var values=req?["OB_LCAINFO"] as JsonObject;
        if(root?.Count!=1||req?.Count!=2||Catalog.Text(req?["OPERTYPE"])!="MODIFY"||values?.Count!=2 ||
            values["LCA_USERNAME"] is not JsonValue user||!user.TryGetValue<string>(out var username)||
            values["LCA_PASSWORD"] is not JsonValue pass||!pass.TryGetValue<string>(out var password))
            throw new RouterException("Only the reviewed local-admin credential request is permitted here.");
        ValidateCredential(new(username,password));
    }
}
internal sealed record WobaInspection(CloudDevice Device,string Address,string Pin,string Role,string Firmware,bool Incomplete,
    IReadOnlyDictionary<string,JsonObject> Data,IReadOnlyList<ReadResult> Reads,DateTimeOffset Time,string OnboardingStatus="")
{
    public override string ToString()=>"Local onboarding inspection (private identity withheld)";
}
internal sealed class WobaAdminPlan(WobaInspection before,RouterCredential credential)
{
    internal WobaInspection Before{get;}=before;
    internal RouterCredential Credential{get;}=credential;
    public string Preview=>$"Device: {Before.Device}\nLocal address: {Before.Address}\nRole: {Before.Role}\nFirmware: {Before.Firmware}\n\nSet local admin username to {Credential.UserName}; password: [new password hidden].\n\nThis replaces the router's local administrator credential. Other apps using the old credential may need to reconnect. Wi-Fi settings, mesh membership and ARRIS account data are not changed. An encrypted recovery login is saved before the one write; a normal authenticated login must verify success.";
    public override string ToString()=>"WOBA local-admin plan (credential withheld)";
}
internal sealed class WobaSession : IDisposable
{
    readonly IRouterWire wire;readonly string address,mac,pin;int used;
    public Action<string>? Progress{get;set;}
    public WobaSession(RouterApiEndpoint endpoint,IRouterWire? testWire=null)
    {
        if(!CredentialStore.IsPrivate(endpoint.Address)||!CertificatePin.IsValid(endpoint.Pin))throw new RouterException("Discover and select a private local router first.");
        address=endpoint.Address;mac=endpoint.Mac;pin=endpoint.Pin;
        wire=testWire??new LocalWire(new CredentialRecord("","",mac,address),pin,woba:true);
    }
    public async Task<WobaInspection> InspectAsync(CancellationToken token)
    {
        Progress?.Invoke("Checking local WOBA onboarding access…");
        JsonObject login;
        try{login=await wire.SendAsync(WobaProtocol.Login,RouterClient.LoginBody("maxadmin","maxpassword","local_woba_"+Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),token);}
        catch(RouterException ex) when(ex.Code=="LoginNotApplicable")
        {throw new RouterException("This router reports onboarding complete, so WOBA login is unavailable. Use its existing local admin credential; this feature does not reset it.",ex.Code);}
        var data=new Dictionary<string,JsonObject>();var reads=new List<ReadResult>();
        foreach(var key in WobaProtocol.Reads.Keys)
        {
            token.ThrowIfCancellationRequested();Progress?.Invoke("Reading local onboarding: "+key+"…");
            try{data[key]=WobaProtocol.Project(key,await wire.SendAsync(WobaProtocol.ReadPath(key),null,token));reads.Add(new(key,200,"OK",DateTimeOffset.UtcNow,true));}
            catch(RouterException ex) when(ex.Code is "InvalidOption" or "InvalidModel" or "FAILURE" or "ERROR")
            {reads.Add(new(key,null,ex.Code,DateTimeOffset.UtcNow,false));}
        }
        JsonNode? Value(string key,string container)=>data.GetValueOrDefault(key)?[container];
        var identity=Value("OB_DEVICEINFO_ALL","OB_DEVICEINFO");var caps=Value("OB_CAPABILITIES_ALL","CAPABILITIES");
        var serial=Catalog.Text(identity?["MAX_SERIAL_NO"]).Trim().ToUpperInvariant();
        var actualMac=BleSetupProtocol.NormalizeMac(Catalog.Text(identity?["MAX_WAN_MAC"]));
        var model=Catalog.Text(caps?["MODEL"]);if(model.Length==0)model=Catalog.Text(identity?["MAX_MODEL_NO"]);
        var device=new CloudDevice(model,serial,actualMac);
        if(actualMac.Length>0 && actualMac!=mac)throw new RouterException("WOBA identity does not match the discovered certificate MAC. No settings were changed.");
        return new(device,address,pin,Catalog.Text(Value("OB_DEVICEMODE_ALL","OB_DEVICEMODE")?["DEVICE_MODE"]),
            Catalog.Text(caps?["FW_VERSION"]),Catalog.Text(login["OBASTATUS"])=="1",data,reads,DateTimeOffset.UtcNow,Catalog.Text(login["OBASTATUS"]));
    }
    internal static void CheckWritable(WobaInspection inspection)
    {
        BleSetupProtocol.ValidateIdentity(inspection.Device);
        if(!inspection.Incomplete||inspection.Role!="MAINAP")throw new RouterException("Admin setup requires incomplete onboarding and a MAINAP unit. Other roles may only stage credentials instead of activating them.");
        if(inspection.Device.Model!="W30"||inspection.Firmware!="AXR.0814.240522.10")throw new RouterException("This password-setting path is verified only for W30 firmware AXR.0814.240522.10. This unit can be inspected, but its password will not be changed.");
        if(inspection.Data.GetValueOrDefault("OB_SWUPDATESTATUS_ALL")?["OB_SWUPDATESTATUS"] is not JsonObject update ||
            Catalog.Text(update["UPGRADE_STATUS"]) is not ("Idle" or "Unknown" or "Success" or "Completed" or "NoUpdate" or "LatestSoftwareInstalled"))
            throw new RouterException("An idle/completed update state was not reported. Inspect the router before changing its admin login.");
        var progress=Catalog.Text(update["PROGRESS_PERCENTAGE"]);
        if(progress is not ("0" or "100"))throw new RouterException("The router reports incomplete or unknown firmware progress. No admin change is permitted.");
    }
    public WobaAdminPlan Prepare(WobaInspection before,RouterCredential credential)
    {
        if(before.Address!=address||before.Pin!=pin||before.Device.Mac!=mac)throw new RouterException("This inspection belongs to another router.");
        CheckWritable(before);WobaProtocol.ValidateCredential(credential);return new(before,credential);
    }
    public async Task<string> ApplyAsync(WobaAdminPlan plan,Func<OfflineSetupDraft,string> preserve,CancellationToken token)
    {
        if(Interlocked.Exchange(ref used,1)!=0)throw new RouterException("This admin-setup attempt is closed. Inspect again before another attempt.");
        Prepare(plan.Before,plan.Credential);token.ThrowIfCancellationRequested();
        var fresh=await InspectAsync(token);CheckWritable(fresh);
        if(fresh.Device!=plan.Before.Device||fresh.Role!=plan.Before.Role||fresh.Firmware!=plan.Before.Firmware)
            throw new RouterException("Device identity, role or firmware changed since review. Nothing was written.");
        var draft=new OfflineSetupDraft(fresh.Device,plan.Credential with{CertificatePin=pin},"WOBA local admin",Source:"WOBA",Address:address);
        var path=preserve(draft);if(string.IsNullOrWhiteSpace(path))throw new RouterException("Recovery credential could not be retained. Nothing was written.");
        token.ThrowIfCancellationRequested();
        try{await wire.SendAsync(WobaProtocol.Set,WobaProtocol.Body(plan.Credential),token);}
        catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or RouterException)
        {throw new RouterException("One WOBA admin-setup request was attempted; its result is unverified. The pending recovery credential was retained. Try verifying the new local login before repeating setup. No write was retried.");}
        return path;
    }
    public void Dispose()=>wire.Dispose();
}
