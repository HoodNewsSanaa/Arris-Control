using System.Text.Json.Nodes;

namespace ArrisControl;

// Invoked only by the explicit CI test intent; uses fictional credentials and an emulator-host fixture.
internal static class RouterNetworkSelfTests
{
    public static async Task RunAsync(CancellationToken token)
    {
        const string host="10.0.2.2";
        const string mac="001122334455";
        AndroidNetwork.UseLocal();
        // The probe has a 3 s budget for TLS plus one API read; a cold CI emulator can miss it, so allow retries.
        var endpoints=await RouterDiscovery.DiscoverApisAsync([host],null,token);
        for(var attempt=1;endpoints.Count==0 && attempt<3;attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2),token);
            endpoints=await RouterDiscovery.DiscoverApisAsync([host],null,token);
        }
        if(endpoints.Count!=1 || endpoints[0].Mac!=mac || endpoints[0].Status!="SessionTimeout" || !CertificatePin.IsValid(endpoints[0].Pin))
            throw new InvalidOperationException("Anonymous ARRIS API discovery failed against the TLS fixture: "+
                (endpoints.Count==0?"no endpoint":string.Join("; ",endpoints.Select(e=>$"{e.Address} mac={e.Mac} status={e.Status} pinLength={e.Pin.Length} pinValid={CertificatePin.IsValid(e.Pin)}"))));
        var pin=endpoints[0].Pin;
        var directory=Path.Combine(Path.GetTempPath(),"router-network-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? updatedPath=null, preservedPath=null, pinnedPath=null, legacyPreserved=null, offlinePath=null;
        try
        {
            var device=new CloudDevice("Fixture mAX","TESTSERIAL123",mac);
            var login=new RouterCredential("test@example.com","FictionalLocalPassword123!");
            var saved=CredentialStore.SaveVerified(login,device,"10.0.2.3",pin,directory);
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(saved.Path)));
            preservedPath=Path.Combine(CredentialStore.UserDirectory,$"loaded-{saved.Serial}-{saved.Mac}-{hash}.json");
            // This is the same address override the Android saved-router action now supplies.
            using(var first=new RouterClient(saved with { Address=host }))
            {
                await first.ConnectAsync(token);
                var page=await first.ReadPageAsync(new Page("fixture","Fixture","",["DEVICEOPERMODE_ALL"]),token);
                if(Catalog.Text(page.Data["DEVICEOPERMODE_ALL"]["DEVICEOPERMODE"]?["MODE"])!="BRIDGE")
                    throw new InvalidOperationException("The first real HTTPS login/cookie/protected read failed.");
                saved=first.Record; updatedPath=saved.Path;
                if(saved.Address!=host || CredentialStore.Inspect(saved.Path).Address!=host)
                    throw new InvalidOperationException("Verified address override was not persisted.");
            }
            using(var second=new RouterClient(saved))
            {
                await second.ConnectAsync(token);
                var page=await second.ReadPageAsync(new Page("fixture","Fixture","",["DEVICEOPERMODE_ALL"]),token);
                if(page.Reads.Count!=1 || !page.Reads[0].Readable)
                    throw new InvalidOperationException("Reconnecting from the encrypted saved credential failed.");
            }
            // An impostor: same MAC common name, different key. The TLS check must fail before any login is sent.
            var wrongPin=Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("not the fixture key"u8.ToArray()));
            using(var impostor=new LocalWire(saved with { Address=host },wrongPin))
            {
                var refused=false;
                try{await impostor.SendAsync("/loginAction",RouterClient.LoginBody(login.UserName,login.Password,"pin-test",0),token);}
                // Any transport failure counts; a router reply (RouterException) or success means TLS was accepted.
                catch(Exception ex) when(ex is not RouterException and not InvalidOperationException){refused=true;}
                if(!refused)throw new InvalidOperationException("A certificate that does not match the pin was accepted.");
            }
            // A credential saved before pinning connects only at its saved address, then upgrades to a pinned version.
            var payload=new JsonObject{["UserName"]=login.UserName,["Password"]=login.Password}.ToJsonString();
            var cipher=CredentialProtection.Protect(System.Text.Encoding.UTF8.GetBytes(payload),System.Text.Encoding.UTF8.GetBytes($"ArrisLocalCredential.v1|{device.Serial}|{mac}"));
            var legacyFile=Path.Combine(directory,"legacy.json");
            File.WriteAllText(legacyFile,new JsonObject{["Format"]="ARRIS-LocalCredential",["Version"]=1,["TargetSerial"]=device.Serial,["ExpectedMac"]=mac,
                ["RouterAddress"]=host,["Ciphertext"]=Convert.ToBase64String(cipher)}.ToJsonString());
            legacyPreserved=Path.Combine(CredentialStore.UserDirectory,$"loaded-{device.Serial}-{mac}-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(legacyFile)))}.json");
            using(var legacy=new RouterClient(CredentialStore.Inspect(legacyFile)))
            {
                await legacy.ConnectAsync(token);
                pinnedPath=legacy.Record.Path;
                if(pinnedPath==legacyFile || CredentialStore.Load(legacy.Record,false).CertificatePin!=pin)
                    throw new InvalidOperationException("The legacy credential was not upgraded to the fixture's certificate pin.");
            }
            // A first-time offline draft must verify through local HTTPS alone, with no
            // account protocol reference, cloud API instance or cloud sign-in involved.
            var draft=new OfflineSetupDraft(device,login,"Fixture Wi-Fi");
            var pending=OfflineSetupStore.Save(draft,directory);
            var restored=OfflineSetupStore.Load(pending);
            var verified=await CloudAcquisition.VerifyAndSaveAsync(new(restored.Credential,[restored.Device]),
                restored.Device,host,token,pin,"Offline setup fixture, authenticated local verification");
            offlinePath=verified.Path;
            if(CredentialStore.Load(verified,false).CertificatePin!=pin ||
                !File.ReadAllText(verified.Path).Contains("Offline setup fixture"))
                throw new InvalidOperationException("The pending offline login did not verify and pin its local router.");
            File.Delete(pending);
            Console.WriteLine("PASS: anonymous scan with pin, native Android HTTPS login, cookie handling, verified address update, reconnect, impostor refusal, legacy pin upgrade and pending offline-login verification without cloud.");
        }
        finally
        {
            if(updatedPath is not null && File.Exists(updatedPath)) File.Delete(updatedPath);
            if(preservedPath is not null && File.Exists(preservedPath)) File.Delete(preservedPath);
            if(pinnedPath is not null && File.Exists(pinnedPath)) File.Delete(pinnedPath);
            if(legacyPreserved is not null && File.Exists(legacyPreserved)) File.Delete(legacyPreserved);
            if(offlinePath is not null && File.Exists(offlinePath)) File.Delete(offlinePath);
            foreach(var path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
