using System.Collections.Concurrent;
using System.IO;
using System.Text.Json.Nodes;

namespace ArrisControl;
internal static class DiscoverySelfTests
{
    static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    public static async Task RunAsync()
    {
        var subnet=RouterDiscovery.SubnetCandidates("192.168.10.7","255.255.255.0").ToArray();
        Check(subnet.Length==253 && subnet.Contains("192.168.10.104") && !subnet.Contains("192.168.10.7") && !subnet.Contains("192.168.10.255"),"Subnet discovery boundary failed.");
        Check(!RouterDiscovery.SubnetCandidates("8.8.8.8","255.255.255.0").Any(),"Public subnet scanned.");
        Check(!RouterDiscovery.SubnetCandidates("192.168.1.2","255.0.255.0").Any(),"Noncontiguous mask accepted.");
        Check(RouterDiscovery.SubnetCandidates("10.2.3.4","255.0.0.0").Count()==253,"Large-network scan was not bounded.");
        var calls=new ConcurrentBag<string>();
        Task<bool> Saved(string a,string m,CancellationToken ct){calls.Add(a);return Task.FromResult(a=="192.168.7.1");}
        var result=await RouterDiscovery.ResolveAsync("192.168.7.1","001122334455",["192.168.1.100"],Saved,default);
        Check(result=="192.168.7.1" && calls.Count==1,"Saved-address fast path scanned unnecessarily.");
        calls.Clear();
        Task<bool> Moved(string a,string m,CancellationToken ct){calls.Add(a);return Task.FromResult(a=="192.168.1.100" && m=="001122334455");}
        result=await RouterDiscovery.ResolveAsync("192.168.7.1","001122334455",["192.168.1.9","192.168.1.100","192.168.1.100","8.8.8.8"],Moved,default);
        Check(result=="192.168.1.100" && calls.Count==3 && !calls.Contains("8.8.8.8"),"Moved-router selection/deduplication failed.");
        foreach(var matches in new[]{Array.Empty<string>(),new[]{"192.168.1.9","192.168.1.100"}})
        {
            bool refused=false;try{await RouterDiscovery.ResolveAsync("192.168.7.1","001122334455",["192.168.1.9","192.168.1.100"],(a,_,_)=>Task.FromResult(matches.Contains(a)),default);}catch(RouterException){refused=true;}
            Check(refused,"Missing or ambiguous router was selected.");
        }
        using(var cancel=new CancellationTokenSource())
        {
            cancel.Cancel();bool cancelled=false;
            try{await RouterDiscovery.ResolveAsync("192.168.7.1","001122334455",["192.168.1.100"],(a,m,ct)=>Task.FromCanceled<bool>(ct),cancel.Token);}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled,"Discovery cancellation was lost.");
        }
        var scanned=new ConcurrentBag<string>();
        Task<RouterApiEndpoint?> Probe(string a,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();scanned.Add(a);
            return Task.FromResult<RouterApiEndpoint?>(a=="192.168.1.104"?new(a,"001122334455","SessionTimeout"):null);
        }
        var apis=await RouterDiscovery.DiscoverApisAsync(["192.168.1.110","192.168.1.104","192.168.1.104","8.8.8.8"],Probe,null,default);
        Check(apis.Count==1 && apis[0].Address=="192.168.1.104" && scanned.Count==2 && !scanned.Contains("8.8.8.8"),"API scan did not enforce private addresses and deduplication.");
        scanned.Clear();
        await RouterDiscovery.DiscoverApisAsync(Enumerable.Range(0,2000).Select(i=>$"10.0.{i/256}.{i%256}"),Probe,null,default);
        Check(scanned.Count==1024,"API scan exceeded its host bound.");
        var connectionError=new System.Net.Http.HttpRequestException(System.Net.Http.HttpRequestError.ConnectionError,"fictional",new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        Check(RouterDiscovery.ConnectionFailure(connectionError,"192.168.1.110").Contains("refused"),"Connection refusal was hidden by a generic error.");
        var folder=Path.Combine(Path.GetTempPath(),"discovery-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var file=Path.Combine(folder,"original.json");
            File.WriteAllText(file,new JsonObject{["Format"]="ARRIS-LocalCredential",["Version"]=1,["TargetSerial"]="TESTSERIAL123",["ExpectedMac"]="001122334455",["RouterAddress"]="192.168.7.1",["Ciphertext"]="RklDVElPTkFM"}.ToJsonString());
            var old=CredentialStore.Inspect(file);var next=CredentialStore.SaveVerifiedAddress(old,"192.168.1.100",folder);
            Check(next.Address=="192.168.1.100" && next.Path!=file && CredentialStore.Inspect(file).Address=="192.168.7.1","Address update overwrote old profile.");
            Check(JsonNode.Parse(File.ReadAllText(next.Path))!["Ciphertext"]!.GetValue<string>()=="RklDVElPTkFM","Address update changed encrypted payload.");
            bool refused=false;try{CredentialStore.SaveVerifiedAddress(old,"8.8.8.8",folder);}catch(RouterException){refused=true;}Check(refused,"Public profile destination accepted.");
        }
        finally{foreach(var path in Directory.EnumerateFiles(folder))File.Delete(path);Directory.Delete(folder);}
        Console.WriteLine("PASS: offline discovery selection, network bounds, ambiguity, cancellation, and encrypted profile address retention. No sockets or real credentials.");
    }
}
