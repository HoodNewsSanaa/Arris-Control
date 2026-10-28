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
        var endpoints=await RouterDiscovery.DiscoverApisAsync([host],null,token);
        if(endpoints.Count!=1 || endpoints[0].Mac!=mac || endpoints[0].Status!="SessionTimeout")
            throw new InvalidOperationException("Anonymous ARRIS API discovery failed against the TLS fixture.");
        var directory=Path.Combine(Path.GetTempPath(),"router-network-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? updatedPath=null;
        try
        {
            var device=new CloudDevice("Fixture mAX","TESTSERIAL123",mac);
            var saved=CredentialStore.SaveVerified(new("test@example.com","FictionalLocalPassword123!"),device,"10.0.2.3",directory);
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
            Console.WriteLine("PASS: anonymous scan, native Android HTTPS login, cookie handling, verified address update and reconnect.");
        }
        finally
        {
            if(updatedPath is not null && File.Exists(updatedPath)) File.Delete(updatedPath);
            foreach(var path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
