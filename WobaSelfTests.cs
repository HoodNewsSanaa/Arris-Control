using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
namespace ArrisControl;
internal static class WobaSelfTests
{
    static JsonObject Obj(string text)=>JsonNode.Parse(text)!.AsObject();
    static void Check(bool yes,string message){if(!yes)throw new InvalidOperationException(message);}
    static readonly string Pin=Convert.ToBase64String(SHA256.HashData("fictional woba key"u8.ToArray()));
    static RouterApiEndpoint Endpoint=>new("192.168.7.1","001122334455","SessionTimeout",Pin);
    sealed class Fake : IRouterWire
    {
        public int Writes,Logins;public bool Complete,Changed,Busy,Lost,WrongSerial,WrongMac;public string Role="MAINAP",Model="W30",Firmware="AXR.0814.240522.10";
        public readonly List<string> Calls=[];
        public Task<JsonObject> SendAsync(string path,string? body=null,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();Calls.Add(path);
            if(path==WobaProtocol.Login){Logins++;if(Complete)throw new RouterException("Complete","LoginNotApplicable");return Task.FromResult(Obj("""{"STATUS":"OK","OBASTATUS":"1","CREDENTIAL":"MUST_NOT_RETAIN"}"""));}
            if(path==WobaProtocol.Set)
            {
                WobaProtocol.ValidateBody(body!);Writes++;if(Lost)throw new HttpRequestException("Fictional lost response");return Task.FromResult(Obj("""{"STATUS":"SUCCESS"}"""));
            }
            var key=path.Split('=').Last();
            return Task.FromResult(key switch{
                "OB_DEVICEINFO_ALL"=>Obj(("""{"OB_DEVICEINFO":{"MAX_SERIAL_NO":"TESTSERIAL123","MAX_WAN_MAC":"00:11:22:33:44:55","MAX_MODEL_NO":"WR11AXER1","DUID":"PRIVATE_HASH","PASSWORD":"PRIVATE_PASSWORD"}}""").Replace("TESTSERIAL123",WrongSerial&&Logins>1?"OTHERSERIAL123":"TESTSERIAL123").Replace("00:11:22:33:44:55",WrongMac&&Logins>1?"00:11:22:33:44:99":"00:11:22:33:44:55")),
                "OB_DEVICEMODE_ALL"=>new JsonObject{["OB_DEVICEMODE"]=new JsonObject{["DEVICE_MODE"]=Changed&&Logins>1?"EXTENDER":Role}},
                "OB_CAPABILITIES_ALL"=>new JsonObject{["CAPABILITIES"]=new JsonArray(new JsonObject{["MODEL"]=Model,["FW_VERSION"]=Firmware,["CLIENT_SECRET"]="PRIVATE_CLIENT_SECRET"})},
                "OB_SWUPDATESTATUS_ALL"=>new JsonObject{["OB_SWUPDATESTATUS"]=new JsonObject{["UPGRADE_STATUS"]=Busy?"InProcess":"LatestSoftwareInstalled",["PROGRESS_PERCENTAGE"]=Busy?"50":"0"}},
                "OB_WANSTATUS_ALL"=>Obj("""{"OB_WANSTATUS":{"WAN_STATUS":"Down"}}"""),
                "OB_DEVICESTATUS_ALL"=>Obj("""{"OB_DEVICESTATUS":{"MAINAP_STATE":"Waiting","NUM_SATELLITES":"0"}}"""),
                "OB_POSTRESET_SETTINGS_ALL"=>throw new RouterException("Unsupported","InvalidOption"),
                _=>throw new InvalidOperationException("Unexpected WOBA request "+path)
            });
        }
        public void Dispose(){}
    }
    public static async Task RunAsync()
    {
        var credential=new RouterCredential("admin","FictionalStrongPassword123!");
        foreach(var path in new[]{"/setData","/getData?key=DEVICE_INFO_ALL","/woba_getData?key=OB_PHYACCSSHASH_ALL","/woba_getData?key=OB_FHWIFI_ALL"})
            Check(!WobaProtocol.Allows(path,null),"WOBA transport admitted an unrelated/secret read.");
        Check(!WobaProtocol.Allows("/woba_getData?key=OB_DEVICEINFO_ALL&key=UPDATE_ALL",null),"WOBA query smuggling accepted.");
        foreach(var body in new[]{"{}","{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"OB_RESET\":{}}}","{\"REQUEST\":{\"OPERTYPE\":\"MODIFY\",\"OB_LCAINFO\":{\"LCA_USERNAME\":\"admin\",\"LCA_PASSWORD\":\"short\"}}}"})
        {var refused=false;try{WobaProtocol.ValidateBody(body);}catch(RouterException){refused=true;}Check(refused,"Unreviewed WOBA body admitted.");}
        using(var fake=new Fake())using(var session=new WobaSession(Endpoint,fake))
        {
            var inspection=await session.InspectAsync(default);
            var data=string.Join("\n",inspection.Data.Values.Select(v=>v.ToJsonString()));
            var report=WobaProtocol.Report(inspection);
            Check(!report.Contains("TESTSERIAL123")&&!report.Contains("001122334455")&&report.Contains("W30"),"WOBA report disclosed identity or omitted model.");
            Check(!data.Contains("PRIVATE_")&&!data.Contains("MUST_NOT_RETAIN") && inspection.Reads.Any(r=>!r.Readable),"WOBA inspection retained secrets or hid unsupported queries.");
            var folder=Path.Combine(Path.GetTempPath(),"woba-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            try
            {
                var plan=session.Prepare(inspection,credential);Check(!plan.Preview.Contains(credential.Password)&&!plan.ToString().Contains(credential.Password),"WOBA preview exposed a credential.");
                var file=await session.ApplyAsync(plan,draft=>OfflineSetupStore.Save(draft,folder),default);
                var recovery=OfflineSetupStore.Load(file);
                Check(fake.Writes==1&&recovery.Source=="WOBA"&&recovery.Address==Endpoint.Address&&recovery.Credential.CertificatePin==Pin,"Pending WOBA credential was not retained with its pin.");
                Check(!File.ReadAllText(file).Contains(credential.Password),"WOBA credential saved in plaintext.");
                var refused=false;try{await session.ApplyAsync(plan,_=>"unused",default);}catch(RouterException){refused=true;}
                Check(refused&&fake.Writes==1,"WOBA write was reused.");
            }
            finally{Directory.Delete(folder,true);}
        }
        using(var fake=new Fake{Complete=true})using(var session=new WobaSession(Endpoint,fake))
        {
            var refused=false;try{await session.InspectAsync(default);}catch(RouterException ex){refused=ex.Code=="LoginNotApplicable";}
            Check(refused&&fake.Writes==0,"Completed onboarding was bypassed.");
        }
        foreach(var setup in new[]{"busy","changed","serial","mac","preserve","lost","cancel","W31","role"})
        {
            using var fake=new Fake();using var session=new WobaSession(Endpoint,fake);
            var inspection=await session.InspectAsync(default);
            if(setup=="W31")inspection=inspection with{Device=inspection.Device with{Model="W31"}};
            if(setup=="role")inspection=inspection with{Role="EXTENDER"};
            var refused=false;var preserved=0;
            try
            {
                var plan=session.Prepare(inspection,credential);
                if(setup=="serial")fake.WrongSerial=true;if(setup=="mac")fake.WrongMac=true;
                if(setup=="busy")fake.Busy=true;if(setup=="changed")fake.Changed=true;if(setup=="lost")fake.Lost=true;
                using var cancellation=new CancellationTokenSource();if(setup=="cancel")cancellation.Cancel();
                await session.ApplyAsync(plan,_=>{preserved++;if(setup=="preserve")throw new IOException("Fictional storage failure");return "fictional-recovery-record";},cancellation.Token);
            }
            catch(Exception ex) when(ex is RouterException or IOException or OperationCanceledException){refused=true;}
            Check(refused&&fake.Writes==(setup=="lost"?1:0),"WOBA guard/no-retry failed: "+setup);
            if(setup=="lost")Check(preserved==1,"Uncertain write lacked a recovery record.");
        }
        var projected=WobaProtocol.Project("OB_DEVICESTATUS_ALL",Obj("""{"OB_DEVICESTATUS":{"SAT_LIST":[{"MAX_SERIAL_NO":"FICTITIOUS001","SAT_STATE":"Waiting","PHY_ACCESS_HASH":"PRIVATE_HASH"}]}}"""));
        Check(projected.ToJsonString().Contains("Waiting")&&!projected.ToJsonString().Contains("PRIVATE_HASH"),"Node onboarding status projection leaked a hash.");
        var folderLegacy=Path.Combine(Path.GetTempPath(),"woba-legacy-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folderLegacy);
        try
        {
            var legacy=System.Text.Encoding.UTF8.GetBytes("""{"Device":{"Model":"W30","Serial":"TESTSERIAL123","Mac":"001122334455"},"Credential":{"UserName":"admin","Password":"FictionalStrongPassword123!"},"Ssid":"Fictional WiFi"}""");
            var cipher=CredentialProtection.Protect(legacy,System.Text.Encoding.UTF8.GetBytes("ArrisControl.OfflineSetup.v1"));
            var path=Path.Combine(folderLegacy,"pending-legacy.json");File.WriteAllText(path,new JsonObject{["Format"]="ARRIS-OfflineSetup",["Version"]=1,["Ciphertext"]=Convert.ToBase64String(cipher)}.ToJsonString());
            Check(OfflineSetupStore.Load(path).Source=="Bluetooth","A pre-WOBA pending Bluetooth credential lost compatibility.");
        }
        finally{Directory.Delete(folderLegacy,true);}
        Console.WriteLine("PASS: WOBA allowlists, secret projection, incomplete-onboarding/identity/update guards, encrypted pinned recovery, bounded one-shot admin write and lost-response refusal. No router or cloud requests.");
    }
}
