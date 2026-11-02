using Android.Widget;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArrisControl;
public sealed partial class MainActivity
{
    void ShowWobaStart()
    {
        ble?.Dispose();ble=null;
        var previous=address?.Text?.Trim()??"192.168.7.1";
        client?.Dispose();client=null;acquired=null;
        Shell("WOBA local setup");backAction=BackToStart;
        var intro=Card("Local onboarding access","Use this after Bluetooth bring-up makes the local HTTPS API reachable. WOBA needs incomplete onboarding. This does not reset a configured router, install root or contact ARRIS.");
        var target=Input(intro,"Router IPv4 address",previous);
        var knownPins=new Dictionary<string,string>(scannedPins,StringComparer.Ordinal);
        Action(intro,"Find routers on this Wi-Fi",async()=>
        {
            AndroidNetwork.UseLocal();var results=await RouterDiscovery.DiscoverApisAsync(AndroidNetwork.Candidates(),null,lifetime.Token);
            if(results.Count==0)throw new RouterException("No local admin API answered. A factory-reset unit may need Bluetooth bring-up first.");
            var choice=await Choose("Select your router",results.Select(r=>r.Address+" · key "+r.Fingerprint).ToArray());
            if(choice<0)return;target.Text=results[choice].Address;knownPins[results[choice].Address]=results[choice].Pin;
        });
        Action(intro,"Inspect WOBA access (no settings changes)",async()=>
        {
            AndroidNetwork.UseLocal();
            var endpoint=await RouterDiscovery.ProbeApiAsync(target.Text?.Trim()??"",lifetime.Token)??throw new RouterException("The local ARRIS API did not answer with a valid device certificate. Check Wi-Fi and Bluetooth bring-up.");
            if(knownPins.TryGetValue(endpoint.Address,out var expectedPin) && endpoint.Pin!=expectedPin)throw new RouterException("The certificate key changed since discovery. No WOBA login was sent.");
            knownPins[endpoint.Address]=endpoint.Pin;
            using var session=new WobaSession(endpoint){Progress=m=>status.Text=m};
            var inspection=await session.InspectAsync(lifetime.Token);
            ShowWobaInspection(endpoint,inspection);
        },Tone.Primary);
        Action(content,"Bluetooth bring-up / inspection",BleScanAndPick);
        Action(content,"Back to connections",BackToStart);
    }
    void ShowWobaInspection(RouterApiEndpoint endpoint,WobaInspection inspection)
    {
        Shell("WOBA inspection");backAction=()=>{ShowWobaStart();return Task.CompletedTask;};
        var identity=Card("Selected router",inspection.Device.ToString(),chipText:inspection.Incomplete?"Onboarding incomplete":"State unverified");
        identity.AddView(Label("Local address: "+inspection.Address+"\nRole: "+inspection.Role+"\nFirmware: "+inspection.Firmware+"\nCertificate key: "+endpoint.Fingerprint,14));
        var diagnostics=Card("Onboarding diagnostics","Identity, role, WAN, update progress and supported setup fields. Passwords, physical-access hashes and cloud configuration are excluded.");
        var fields=inspection.Data.SelectMany(kv=>Catalog.Flatten(kv.Value,kv.Key)).ToArray();
        var text=Label(string.Join("\n\n",fields.Select(f=>f.Field+"\n"+f.Value)),13);text.SetTextIsSelectable(true);diagnostics.AddView(text);
        var unavailable=inspection.Reads.Where(r=>!r.Readable).Select(r=>r.Key+": "+r.Status).ToArray();
        if(unavailable.Length>0)diagnostics.AddView(Label("Unavailable reads\n"+string.Join("\n",unavailable),12));
        Action(diagnostics,"Copy redacted WOBA report",()=>
        {
            var report=WobaProtocol.Report(inspection);
            ((Android.Content.ClipboardManager)GetSystemService(ClipboardService)!).PrimaryClip=Android.Content.ClipData.NewPlainText("WOBA diagnostics",report);
            status.Text="Redacted onboarding report copied.";return Task.CompletedTask;
        });
        string? blocked=null;try{WobaSession.CheckWritable(inspection);}catch(RouterException ex){blocked=ex.Message;}
        if(blocked is not null)content.AddView(Label("Local admin setup unavailable: "+blocked,14));
        else
        {
            var form=Card("Establish a local admin login","This replaces the local admin credential. It leaves Wi-Fi credentials and mesh membership alone. Review the target before applying.");
            var user=Input(form,"New local username","admin");
            var pass=Input(form,"New local admin password (12–64 characters)",secret:true);
            var again=Input(form,"Confirm the new password",secret:true);
            Action(form,"Review local admin setup",async()=>
            {
                if(pass.Text!=again.Text)throw new RouterException("The new passwords do not match.");
                var credential=new RouterCredential(user.Text?.Trim()??"",pass.Text??"");
                using var session=new WobaSession(endpoint){Progress=m=>status.Text=m};
                var plan=session.Prepare(inspection,credential);
                if(!await Confirm("Set this router's local admin credential?",plan.Preview,"Set admin credential once"))return;
                string? pending=null;
                try
                {
                    await session.ApplyAsync(plan,draft=>pending=OfflineSetupStore.Save(draft),lifetime.Token);
                    pass.Text="";again.Text="";
                    var saved=OfflineSetupStore.Load(pending!);
                    ShowOfflineVerification(pending!,saved);
                    await VerifyOfflineDraft(pending!,saved,saved.Address,saved.Credential.CertificatePin);
                }
                catch(Exception ex) when(ex is RouterException or System.Net.Http.HttpRequestException or OperationCanceledException or IOException)
                {
                    pass.Text="";again.Text="";
                    if(pending is not null && File.Exists(pending))
                    {
                        ShowOfflineVerification(pending,OfflineSetupStore.Load(pending));
                        status.Text=ex is RouterException?ex.Message:"Admin setup/verification is unconfirmed. The pending recovery login was retained. Verify it before repeating setup.";
                    }
                    else throw;
                }
            },Tone.Primary);
        }
        Action(content,"Refresh WOBA inspection",async()=>
        {
            using var session=new WobaSession(endpoint){Progress=m=>status.Text=m};ShowWobaInspection(endpoint,await session.InspectAsync(lifetime.Token));
        });
        Action(content,"Back to connections",BackToStart);
    }
}
