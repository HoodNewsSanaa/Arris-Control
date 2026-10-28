using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArrisControl;
internal static class CloudSelfTests
{
    static void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static readonly CloudConfiguration Config=new("us-east-1_TQzd61rzQ","7kvme7eu7mpiihfc4pai87g5k2","fictional_secret_123","https://api1.surfboardconnect.com");
    static readonly string[] Names=["AWS_USER_POOL_ID_MEGHA","AWS_USER_CLIENT_ID_MEGHA","AWS_USER_CLIENT_SECRET_MEGHA","AWS_USER_ECO_URL_MEGHA"];
    static readonly string[] Values=[Config.Pool,Config.ClientId,Config.ClientSecret,Config.AccountBase];
    static string Encrypted()
    {
        using var aes=Aes.Create();aes.Key=Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");aes.IV=Encoding.UTF8.GetBytes("ARRISMAXMANAGERM");
        var cipher=aes.EncryptCbc(Encoding.UTF8.GetBytes("FictionalLocalPassword123!"),aes.IV,PaddingMode.PKCS7);return new JsonObject{["password"]=new JsonObject{["value"]=Convert.ToBase64String(cipher)}}.ToJsonString();
    }
    sealed class FakeCloud : ICloudApi
    {
        public readonly List<string> Operations=[];public string? Challenge;public bool MissingSso;
        public Task<JsonObject> CognitoAsync(string operation,JsonObject body,CancellationToken token)
        {
            Operations.Add(operation);
            if(operation=="InitiateAuth")
            {
                Assert(Catalog.Text(body["AuthFlow"])=="USER_SRP_AUTH" && Catalog.Text(body["AuthParameters"]?["SRP_A"]).Length>0,"SRP initiation lost its public value.");
                return Task.FromResult(new JsonObject{["ChallengeName"]=Challenge??"PASSWORD_VERIFIER",["Session"]="fictional-session",["ChallengeParameters"]=new JsonObject{
                    ["USER_ID_FOR_SRP"]="fictional-user",["USERNAME"]="canonical-fictional-user",["SRP_B"]="deadbeef",["SALT"]="80abcd",["SECRET_BLOCK"]=Convert.ToBase64String(Encoding.UTF8.GetBytes("fictional-block"))}});
            }
            Assert(operation=="RespondToAuthChallenge" && Catalog.Text(body["Session"])=="fictional-session","Unexpected auth operation or session loss.");
            var answers=body["ChallengeResponses"]!;
            Assert(Catalog.Text(answers["USERNAME"])=="canonical-fictional-user" && Catalog.Text(answers["SECRET_HASH"])==CloudAcquisition.SecretHash("canonical-fictional-user",Config),"Canonical challenge username was not used.");
            return Task.FromResult(new JsonObject{["AuthenticationResult"]=new JsonObject{["IdToken"]="fictional-id-token"}});
        }
        public Task<JsonNode> AccountAsync(string resource,string idToken,CancellationToken token)
        {
            Operations.Add(resource);Assert(idToken=="fictional-id-token","Wrong account-service token.");
            if(resource=="info")return Task.FromResult<JsonNode>(MissingSso?new JsonObject():new JsonObject{["SSOKey"]="0123456789abcdef0123456789abcdef",["SSOEncryptedPassword"]=Encrypted()});
            Assert(resource=="cpedevices","Unexpected account resource.");
            return Task.FromResult<JsonNode>(JsonNode.Parse("{\"body\":\"[{\\\"model\\\":\\\"W30\\\",\\\"serialNumber\\\":\\\"TESTSERIAL123\\\",\\\"macAddress\\\":\\\"00:11:22:33:44:55\\\"}]\"}")!);
        }
        public void Dispose(){}
    }
    public static async Task RunAsync()
    {
        var assembly=Assembly.GetExecutingAssembly();using var vectorStream=assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n=>n.EndsWith("srp-test-vector.json",StringComparison.Ordinal)))!;
        var vector=JsonNode.Parse(vectorStream)!;string V(string key)=>Catalog.Text(vector[key]);var srp=new CognitoSrp(V("Pool"),V("PrivateA"));
        Assert(srp.PublicA==V("PublicA") && srp.Sign(V("User"),V("Password"),V("ServerB"),V("Salt"),V("SecretBlock"),V("Timestamp"))==V("Signature"),"Embedded SRP regression vector failed.");
        Assert(CloudAcquisition.Decrypt("0123456789abcdef0123456789abcdef",Encrypted())=="FictionalLocalPassword123!","Account SSO decryption mapping failed.");
        using var api=new FakeCloud();var acquired=await CloudAcquisition.AcquireAsync("fictional@example.com","FictionalCloudPassword123!",Config,api);
        Assert(acquired.Credential.Password=="FictionalLocalPassword123!" && acquired.Devices.Single().Mac=="001122334455","Acquired credential/inventory parsing failed.");
        var encoded=JsonSerializer.SerializeToUtf8Bytes(acquired);
        try{var decoded=JsonSerializer.Deserialize<AcquiredCredential>(encoded);Assert(decoded?.Credential==acquired.Credential && decoded.Devices.SequenceEqual(acquired.Devices),"Encrypted acquisition payload serialization is incompatible.");}
        finally{CryptographicOperations.ZeroMemory(encoded);}
        Assert(api.Operations.SequenceEqual(new[]{"InitiateAuth","RespondToAuthChallenge","info","cpedevices"}),"Acquisition issued unexpected operations.");
        bool foreignRefused=false;try{await CloudAcquisition.VerifyAndSaveAsync(acquired,new CloudDevice("W30","WRONGSERIAL12","001122334466"),"192.168.7.1");}catch(RouterException){foreignRefused=true;}
        Assert(foreignRefused,"A device outside the authenticated inventory was allowed into local verification.");
        Assert(!acquired.ToString().Contains("Password123") && !acquired.Credential.ToString().Contains("Password123") && !Config.ToString().Contains(Config.ClientSecret),"Sensitive record ToString leaked credentials.");
        using var extraChallenge=new FakeCloud{Challenge="SMS_MFA"};bool refused=false;try{await CloudAcquisition.AcquireAsync("fictional@example.com","FictionalCloudPassword123!",Config,extraChallenge);}catch(RouterException){refused=true;}
        Assert(refused && extraChallenge.Operations.Count==1,"Unsupported challenge continued into account/router access.");
        using var missing=new FakeCloud{MissingSso=true};refused=false;try{await CloudAcquisition.AcquireAsync("fictional@example.com","FictionalCloudPassword123!",Config,missing);}catch(RouterException){refused=true;}
        Assert(refused,"Missing account credential was fabricated or accepted.");
        var directory=Path.Combine(Path.GetTempPath(),"cloud-selftest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var java=Path.Combine(directory,"BuildConfig.java");File.WriteAllText(java,string.Join('\n',Names.Select((n,i)=>$"public static final String {n} = \"{Values[i]}\";")));
            Assert(CloudProtocol.ReadReference(java)==Config,"Java protocol constant extraction failed.");
            using var apkMemory=new MemoryStream();using(var apk=new ZipArchive(apkMemory,ZipArchiveMode.Create,true)){using var stream=apk.CreateEntry("classes.dex").Open();stream.Write(DexFixture());}
            var apkBytes=apkMemory.ToArray();var apkFile=Path.Combine(directory,"reference.apk");File.WriteAllBytes(apkFile,apkBytes);Assert(CloudProtocol.ReadReference(apkFile)==Config,"DEX protocol extraction failed.");
            var xapkFile=Path.Combine(directory,"reference.xapk");using(var file=File.Create(xapkFile))using(var zip=new ZipArchive(file,ZipArchiveMode.Create)){using var stream=zip.CreateEntry("com.arris.sbc.apk").Open();stream.Write(apkBytes);}
            Assert(CloudProtocol.ReadReference(xapkFile)==Config,"Nested XAPK protocol extraction failed.");
            TestPersistence(acquired,directory);
        }
        finally{foreach(var file in Directory.EnumerateFiles(directory))File.Delete(file);Directory.Delete(directory,false);}
    }
    static void TestPersistence(AcquiredCredential acquired,string directory)
    {
        // Use fictional data only. Restricted tool profiles may lack DPAPI;
        // a successful Windows profile must pass actual disk round-trips.
        try{var probe=CredentialProtection.Protect([1,2,3],null);CryptographicOperations.ZeroMemory(probe);}
        catch(CryptographicException){Console.WriteLine("SKIP: DPAPI persistence fixture unavailable in this restricted Windows profile; no plaintext fallback or file written.");return;}
        var device=acquired.Devices.Single();var first=CredentialStore.SaveVerified(acquired.Credential,device,"192.168.7.1",directory);
        var second=CredentialStore.SaveVerified(acquired.Credential,device,"192.168.7.1",directory);
        Assert(first.Path!=second.Path && File.Exists(first.Path)&&File.Exists(second.Path),"Credential versions were overwritten.");
        var loaded=CredentialStore.Load(first,false);Assert(loaded==acquired.Credential,"Protected credential disk round-trip failed.");
        Assert(!File.ReadAllText(first.Path).Contains(acquired.Credential.Password),"Plaintext router password written to disk.");
        var pending=AcquisitionStore.Save(acquired,directory);var restored=AcquisitionStore.Load(pending,false);
        Assert(restored.Credential==acquired.Credential && restored.Devices.SequenceEqual(acquired.Devices),"Acquisition disk round-trip failed.");
        Assert(!File.ReadAllText(pending).Contains(acquired.Credential.Password),"Plaintext acquired credential written to disk.");
        Console.WriteLine("PASS: encrypted disk round-trips and retention of distinct credential versions (fictional data only).");
    }
    static byte[] DexFixture()
    {
        var strings=new[]{"Lcom/arris/sbc/BuildConfig;"}.Concat(Names).Concat(Values).ToArray();
        const int stringOffset=112,typeOffset=148,fieldOffset=152,classOffset=184,classData=216,values=228;
        using var memory=new MemoryStream();memory.SetLength(237);memory.Position=237;var offsets=new List<int>();
        foreach(var text in strings){offsets.Add((int)memory.Position);var length=text.Length;do{var b=length&127;length>>=7;memory.WriteByte((byte)(b|(length>0?128:0)));}while(length>0);memory.Write(Encoding.UTF8.GetBytes(text));memory.WriteByte(0);}
        var data=memory.ToArray();Encoding.ASCII.GetBytes("dex\n035\0").CopyTo(data,0);
        void Write32(int offset,int value)=>System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset,4),value);
        Write32(32,data.Length);Write32(36,112);Write32(56,strings.Length);Write32(60,stringOffset);Write32(64,1);Write32(68,typeOffset);Write32(80,4);Write32(84,fieldOffset);Write32(96,1);Write32(100,classOffset);
        for(var i=0;i<strings.Length;i++)Write32(stringOffset+i*4,offsets[i]);Write32(typeOffset,0);
        for(var i=0;i<4;i++)Write32(fieldOffset+i*8+4,i+1);
        Write32(classOffset+24,classData);Write32(classOffset+28,values);data[classData]=4;
        for(var i=0;i<4;i++){data[classData+4+i*2]=(byte)(i==0?0:1);data[classData+5+i*2]=0x19;}
        data[values]=4;for(var i=0;i<4;i++){data[values+1+i*2]=0x17;data[values+2+i*2]=(byte)(5+i);}return data;
    }
}
