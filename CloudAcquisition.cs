using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArrisControl;
internal sealed record CloudDevice(string Model,string Serial,string Mac)
{
    public override string ToString()=>$"{Model} · {Serial} · {Mac}";
}
internal sealed record AcquiredCredential(RouterCredential Credential,List<CloudDevice> Devices)
{
    public override string ToString()=>"Acquired router credential (values withheld)";
}
internal interface ICloudApi : IDisposable
{
    Task<JsonObject> CognitoAsync(string operation,JsonObject body,CancellationToken token);
    Task<JsonNode> AccountAsync(string resource,string idToken,CancellationToken token);
}
internal sealed class CloudApi : ICloudApi
{
    readonly HttpClient http=new(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false}){Timeout=TimeSpan.FromSeconds(25)};
    async Task<JsonNode> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        using(request)using(var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token))
        {
            await using var input=await response.Content.ReadAsStreamAsync(token);using var memory=new MemoryStream();var buffer=new byte[32768];int count;
            while((count=await input.ReadAsync(buffer,token))>0){if(memory.Length+count>2*1024*1024)throw new RouterException("Cloud response exceeded the metadata limit.");await memory.WriteAsync(buffer.AsMemory(0,count),token);}
            JsonNode? json=null;try{json=JsonNode.Parse(memory.ToArray());}catch(System.Text.Json.JsonException){}
            if(!response.IsSuccessStatusCode)
            {
                var code=json is JsonObject o?Catalog.Text(o["__type"]):"";
                if(!Regex.IsMatch(code,@"^[A-Za-z0-9_#.-]{1,100}$"))code="HTTP "+(int)response.StatusCode;
                throw new RouterException("Cloud authentication/read failed: "+code+". No router setting was changed.");
            }
            return json??throw new RouterException("The cloud returned unexpected metadata.");
        }
    }
    public async Task<JsonObject> CognitoAsync(string operation,JsonObject body,CancellationToken token)
    {
        if(operation is not ("InitiateAuth" or "RespondToAuthChallenge"))throw new RouterException("This tool supports sign-in only; account modifications are not available.");
        var request=new HttpRequestMessage(HttpMethod.Post,"https://cognito-idp.us-east-1.amazonaws.com/");request.Headers.Add("X-Amz-Target","AWSCognitoIdentityProviderService."+operation);
        request.Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/x-amz-json-1.1");
        return await SendAsync(request,token) as JsonObject??throw new RouterException("Unexpected authentication response.");
    }
    public Task<JsonNode> AccountAsync(string resource,string idToken,CancellationToken token)
    {
        if(resource is not ("info" or "cpedevices"))throw new RouterException("Unknown account read.");
        var request=new HttpRequestMessage(HttpMethod.Get,"https://api1.surfboardconnect.com/account/account-service/"+resource);
        request.Headers.Authorization=new("Bearer",idToken);return SendAsync(request,token);
    }
    public void Dispose()=>http.Dispose();
}
internal static class CloudAcquisition
{
    internal static string SecretHash(string user,CloudConfiguration config)=>Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(config.ClientSecret),Encoding.UTF8.GetBytes(user+config.ClientId)));
    public static async Task<AcquiredCredential> AcquireAsync(string email,string password,CloudConfiguration config,ICloudApi api,CancellationToken token=default)
    {
        config.Validate();if(!System.Net.Mail.MailAddress.TryCreate(email,out var address) || address.Address!=email || password.Length==0)throw new RouterException("Enter your ARRIS account email and current cloud-account password.");
        var srp=new CognitoSrp(config.Pool);
        var start=await api.CognitoAsync("InitiateAuth",new JsonObject{
            ["ClientId"]=config.ClientId,["AuthFlow"]="USER_SRP_AUTH",["AuthParameters"]=new JsonObject{
                ["USERNAME"]=email,["SRP_A"]=srp.PublicA,["SECRET_HASH"]=SecretHash(email,config)}},token);
        if(Catalog.Text(start["ChallengeName"])!="PASSWORD_VERIFIER" || start["ChallengeParameters"] is not JsonObject challenge)
            throw new RouterException("The account requires a different authentication challenge. No router login was attempted.");
        string Required(string name){var value=Catalog.Text(challenge[name]);if(value.Length is <1 or >8192)throw new RouterException("The authentication challenge is incomplete.");return value;}
        var userId=Required("USER_ID_FOR_SRP");var canonical=Catalog.Text(challenge["USERNAME"]);if(canonical.Length==0)canonical=userId;
        var secretBlock=Required("SECRET_BLOCK");var timestamp=DateTime.UtcNow.ToString("ddd MMM d HH:mm:ss 'UTC' yyyy",CultureInfo.InvariantCulture);
        var signature=srp.Sign(userId,password,Required("SRP_B"),Required("SALT"),secretBlock,timestamp);
        var request=new JsonObject{["ClientId"]=config.ClientId,["ChallengeName"]="PASSWORD_VERIFIER",["ChallengeResponses"]=new JsonObject{
            ["USERNAME"]=canonical,["PASSWORD_CLAIM_SECRET_BLOCK"]=secretBlock,["PASSWORD_CLAIM_SIGNATURE"]=signature,["TIMESTAMP"]=timestamp,["SECRET_HASH"]=SecretHash(canonical,config)}};
        if(start["Session"] is not null)request["Session"]=start["Session"]!.DeepClone();
        var finish=await api.CognitoAsync("RespondToAuthChallenge",request,token);var idToken=Catalog.Text(finish["AuthenticationResult"]?["IdToken"]);
        if(idToken.Length==0)throw new RouterException("Cloud authentication is incomplete. No credential was saved.");
        var info=await api.AccountAsync("info",idToken,token);var inventory=await api.AccountAsync("cpedevices",idToken,token);
        var sso=FindSso(info)??throw new RouterException("The account has no existing SSO credential. No provisioning or password change was requested.");
        var local=Decrypt(sso.Key,sso.Encrypted);var devices=Devices(inventory).Concat(Devices(info)).DistinctBy(d=>(d.Serial,d.Mac)).ToList();
        return new(new RouterCredential(email,local),devices);
    }
    internal static string Decrypt(string key,string encrypted)
    {
        byte[]? plain=null;byte[]? keyBytes=null;byte[]? cipher=null;
        try
        {
            if(encrypted.TrimStart().StartsWith('{'))encrypted=Catalog.Text(JsonNode.Parse(encrypted)?["password"]?["value"]);
            keyBytes=Encoding.UTF8.GetBytes(key);cipher=Convert.FromBase64String(encrypted);using var aes=Aes.Create();aes.Key=keyBytes;aes.IV=Encoding.UTF8.GetBytes("ARRISMAXMANAGERM");aes.Mode=CipherMode.CBC;aes.Padding=PaddingMode.PKCS7;
            plain=aes.DecryptCbc(cipher,aes.IV,PaddingMode.PKCS7);var password=new UTF8Encoding(false,true).GetString(plain);
            if(password.Length==0 || password.Contains('\0'))throw new CryptographicException();return password;
        }
        catch(Exception ex) when(ex is CryptographicException or FormatException or ArgumentException or System.Text.Json.JsonException)
        {throw new RouterException("The account's existing router credential could not be decrypted. No router login was attempted.");}
        finally{if(plain is not null)CryptographicOperations.ZeroMemory(plain);if(keyBytes is not null)CryptographicOperations.ZeroMemory(keyBytes);if(cipher is not null)CryptographicOperations.ZeroMemory(cipher);}
    }
    static IEnumerable<JsonObject> Objects(JsonNode? node,int depth=0)
    {
        if(depth>12 || node is null)yield break;
        if(node is JsonObject obj)
        {
            yield return obj;
            foreach(var kv in obj.Where(kv=>!Catalog.Secret.IsMatch(kv.Key)))foreach(var child in Objects(kv.Value,depth+1))yield return child;
        }
        else if(node is JsonArray array)foreach(var item in array.Take(2000))foreach(var child in Objects(item,depth+1))yield return child;
        else if(node is JsonValue val && val.TryGetValue<string>(out var text) && text.Length<2*1024*1024 && (text.TrimStart().StartsWith('{')||text.TrimStart().StartsWith('[')))
        {
            JsonNode? decoded=null;try{decoded=JsonNode.Parse(text);}catch(System.Text.Json.JsonException){}
            foreach(var child in Objects(decoded,depth+1))yield return child;
        }
    }
    internal static (string Key,string Encrypted)? FindSso(JsonNode info)
    {
        foreach(var obj in Objects(info))
        {
            var key=Catalog.Text(obj.FirstOrDefault(kv=>kv.Key.Equals("SSOKey",StringComparison.OrdinalIgnoreCase)).Value);
            var cipher=Catalog.Text(obj.FirstOrDefault(kv=>kv.Key.Equals("SSOEncryptedPassword",StringComparison.OrdinalIgnoreCase)).Value);
            if(key.Length>0 && cipher.Length>0)return (key,cipher);
        }
        return null;
    }
    internal static List<CloudDevice> Devices(JsonNode inventory)
    {
        var result=new List<CloudDevice>();
        foreach(var obj in Objects(inventory).Take(10000))
        {
            string Get(params string[] names)=>Catalog.Text(obj.FirstOrDefault(kv=>names.Contains(Regex.Replace(kv.Key,"[^A-Za-z0-9]","").ToLowerInvariant())).Value);
            var serial=Get("serialnumber","serial","deviceserialnumber","sn").ToUpperInvariant();
            var mac=Regex.Replace(Get("macaddress","wanmacaddress","mac","wanmac","lanmacaddress","routermacaddress"),"[^A-Fa-f0-9]","").ToUpperInvariant();
            if(Regex.IsMatch(serial,"^[A-Z0-9]{12,24}$")&&Regex.IsMatch(mac,"^[A-F0-9]{12}$"))result.Add(new(Get("model","devicemodel","modelname","modelnumber"),serial,mac));
        }
        return result.DistinctBy(d=>(d.Serial,d.Mac)).ToList();
    }
    public static async Task<CredentialRecord> VerifyAndSaveAsync(AcquiredCredential acquired,CloudDevice selected,string address,CancellationToken token=default)
    {
        if(!acquired.Devices.Contains(selected))throw new RouterException("Select a device returned by this account's inventory.");
        var record=new CredentialRecord("",selected.Serial,selected.Mac,address);using var wire=new LocalWire(record);
        await wire.SendAsync("/loginAction",RouterClient.LoginBody(acquired.Credential.UserName,acquired.Credential.Password,"abcxyz123_windows_"+Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),token);
        var identity=await wire.SendAsync("/getData?key=DEVICE_INFO_ALL",null,token);
        if(Catalog.Text(identity["DEVICE_INFO"]?["SERIAL_KEY"])!=selected.Serial)throw new RouterException("The local serial does not match the selected account device. No credential was saved.");
        var mode=await wire.SendAsync("/getData?key=DEVICEOPERMODE_ALL",null,token);
        if(Catalog.Text(mode["DEVICEOPERMODE"]?["MODE"]) is not ("DUAL" or "IPV4ONLY" or "BRIDGE"))throw new RouterException("No recognized protected mode was returned. No credential was saved.");
        return CredentialStore.SaveVerified(acquired.Credential,selected,address);
    }
}
