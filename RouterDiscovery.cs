using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ArrisControl;

internal static class RouterDiscovery
{
    internal static IEnumerable<string> SubnetCandidates(string address,string mask)
    {
        if(!CredentialStore.IsPrivate(address) || !IPAddress.TryParse(mask,out var subnet))yield break;
        var bytes=IPAddress.Parse(address).GetAddressBytes();var masks=subnet.GetAddressBytes();
        if(masks.Length!=4)yield break;
        uint host=((uint)bytes[0]<<24)|((uint)bytes[1]<<16)|((uint)bytes[2]<<8)|bytes[3];
        uint bits=((uint)masks[0]<<24)|((uint)masks[1]<<16)|((uint)masks[2]<<8)|masks[3];
        var inverse=~bits;if((inverse&(inverse+1))!=0)yield break;
        // Bound large networks to this host's /24; never expand beyond 1022 hosts per adapter.
        if(inverse>1023){bits=0xffffff00;inverse=255;}
        var network=host&bits;
        for(uint i=1;i<inverse;i++)
        {
            var value=network+i;if(value==host)continue;
            var candidate=new IPAddress(new byte[]{(byte)(value>>24),(byte)(value>>16),(byte)(value>>8),(byte)value}).ToString();
            if(CredentialStore.IsPrivate(candidate))yield return candidate;
        }
    }
    static string[] Candidates(string saved)
    {
        var list=new List<string>{saved};
        foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up &&
            n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet))
        {
            var properties=adapter.GetIPProperties();
            list.AddRange(properties.GatewayAddresses.Select(g=>g.Address.ToString()).Where(CredentialStore.IsPrivate));
            foreach(var unicast in properties.UnicastAddresses.Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork))
                list.AddRange(SubnetCandidates(unicast.Address.ToString(),unicast.IPv4Mask.ToString()));
        }
        return list.Where(CredentialStore.IsPrivate).Distinct(StringComparer.Ordinal).Take(1024).ToArray();
    }
    internal static bool CertificateMatches(X509Certificate2 certificate,string mac,DateTime now)=>
        certificate.GetNameInfo(X509NameType.SimpleName,false).Equals(mac,StringComparison.OrdinalIgnoreCase) &&
        now>=certificate.NotBefore && now<=certificate.NotAfter;
    internal static async Task<bool> MatchesAsync(string address,string mac,CancellationToken token)
    {
        if(!CredentialStore.IsPrivate(address))return false;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var socket=new TcpClient();
        try
        {
            await socket.ConnectAsync(address,50011,timeout.Token);
            using var ssl=new SslStream(socket.GetStream(),false,(_,cert,_,_)=>cert is not null && CertificateMatches(new X509Certificate2(cert),mac,DateTime.Now));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost=address},timeout.Token);
            return true;
        }
        catch(Exception ex) when(ex is SocketException or System.IO.IOException or AuthenticationException or OperationCanceledException)
        {token.ThrowIfCancellationRequested();return false;}
    }
    internal static async Task<string> ResolveAsync(string saved,string mac,IEnumerable<string> candidates,
        Func<string,string,CancellationToken,Task<bool>> probe,CancellationToken token)
    {
        if(await probe(saved,mac,token))return saved;
        var matches=new ConcurrentBag<string>();
        await Parallel.ForEachAsync(candidates.Where(CredentialStore.IsPrivate).Where(a=>a!=saved).Distinct().Take(1024),
            new ParallelOptions{MaxDegreeOfParallelism=32,CancellationToken=token},async(address,ct)=>
            {if(await probe(address,mac,ct))matches.Add(address);});
        var found=matches.Order(StringComparer.Ordinal).ToArray();
        return found.Length switch{
            1=>found[0],
            0=>throw new RouterException("Could not find the selected router on the connected local networks. Check the connection and refresh; no cloud fallback or settings change was sent."),
            _=>throw new RouterException("Multiple addresses presented the selected router certificate. Automatic selection stopped; no credential was sent to the discovered addresses.")};
    }
    public static Task<string> ResolveAsync(CredentialRecord record,CancellationToken token)=>
        ResolveAsync(record.Address,record.Mac,Candidates(record.Address),MatchesAsync,token);
}
