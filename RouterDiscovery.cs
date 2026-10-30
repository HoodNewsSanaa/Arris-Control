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
#if ANDROID
        list.AddRange(AndroidNetwork.Candidates());
#else
        foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up &&
            n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet))
        {
            var properties=adapter.GetIPProperties();
            list.AddRange(properties.GatewayAddresses.Select(g=>g.Address.ToString()).Where(CredentialStore.IsPrivate));
            foreach(var unicast in properties.UnicastAddresses.Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork))
                list.AddRange(SubnetCandidates(unicast.Address.ToString(),unicast.IPv4Mask.ToString()));
        }
#endif
        return list.Where(CredentialStore.IsPrivate).Distinct(StringComparer.Ordinal).Take(1024).ToArray();
    }
    internal static bool CertificateMatches(X509Certificate2 certificate,string mac,DateTime now)=>
        certificate.GetNameInfo(X509NameType.SimpleName,false).Equals(mac,StringComparison.OrdinalIgnoreCase) &&
        now>=certificate.NotBefore && now<=certificate.NotAfter;
    internal static async Task<bool> MatchesAsync(string address,string mac,string? pin,CancellationToken token)
    {
        if(!CredentialStore.IsPrivate(address))return false;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var socket=new TcpClient();
        try
        {
            await socket.ConnectAsync(address,50011,timeout.Token);
            using var ssl=new SslStream(socket.GetStream(),false,(_,cert,_,_)=>
            {
                if(cert is null)return false;
                using var certificate=new X509Certificate2(cert);
                return CertificatePin.Accepts(certificate,mac,pin,DateTime.Now);
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost=address},timeout.Token);
            return true;
        }
        catch(Exception ex) when(ex is SocketException or System.IO.IOException or AuthenticationException or OperationCanceledException)
        {token.ThrowIfCancellationRequested();return false;}
    }
    internal static async Task<string> ResolveAsync(string saved,string mac,IEnumerable<string> candidates,
        Func<string,string,CancellationToken,Task<bool>> probe,CancellationToken token,bool savedOnly=false)
    {
        if(await probe(saved,mac,token))return saved;
        // Without a pinned certificate, any LAN host could copy the MAC CN, so never scan.
        if(savedOnly)throw new RouterException($"This credential was saved before router certificates were pinned, so it only connects at its saved address ({saved}). The router did not answer there. Connect once at the router's current address, or acquire the credential again; no credential was sent.");
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
    public static string ConnectionFailure(System.Net.Http.HttpRequestException error, string address)
    {
        Exception? current = error;
        for(var i=0; current is not null && i<8; i++,current=current.InnerException)
        {
            if(current is SocketException socket)
                return socket.SocketErrorCode switch {
                    SocketError.ConnectionRefused => $"{address}:50011 refused the connection. No management listener is reachable at that address; use Scan for ARRIS routers.",
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable => $"{address}:50011 is unreachable from this network. Check the Wi-Fi connection or scan the local subnet.",
                    _ => $"Connection to {address}:50011 failed ({socket.SocketErrorCode})."
                };
            if(current is AuthenticationException)
                return $"TLS verification failed at {address}:50011. The certificate must match the selected router's MAC and valid dates.";
        }
        return $"Connection to {address}:50011 failed ({error.HttpRequestError}). Use Scan for ARRIS routers to check the address.";
    }
    internal static async Task<RouterApiEndpoint?> ProbeApiAsync(string address, CancellationToken token)
    {
        if(!CredentialStore.IsPrivate(address)) return null;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(3));
        string? mac=null,pin=null;
        try
        {
            using(var socket=new TcpClient())
            {
                await socket.ConnectAsync(address,50011,timeout.Token);
                using var ssl=new SslStream(socket.GetStream(),false,(_,cert,_,_)=>
                {
                    if(cert is null) return false;
                    using var certificate=new X509Certificate2(cert);
                    var name=certificate.GetNameInfo(X509NameType.SimpleName,false).ToUpperInvariant();
                    if(!System.Text.RegularExpressions.Regex.IsMatch(name,"^[A-F0-9]{12}$") ||
                       !certificate.Subject.Contains("ARRIS",StringComparison.OrdinalIgnoreCase) ||
                       !CertificateMatches(certificate,name,DateTime.Now)) return false;
                    mac=name;pin=CertificatePin.Compute(certificate);return true;
                });
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost=address},timeout.Token);
            }
            if(mac is null || pin is null) return null;
            // A certificate alone is not enough: make one fixed, anonymous, read-only API query,
            // requiring the same certificate that answered the handshake above.
            using var wire=new LocalWire(new CredentialRecord("","",mac,address),pin);
            try
            {
                await wire.SendAsync("/getData?key=DEVICEOPERMODE_ALL",null,timeout.Token);
                return new(address,mac,"OK",pin);
            }
            catch(RouterException ex) when(ex.Code=="SessionTimeout") { return new(address,mac,"SessionTimeout",pin); }
        }
        catch(Exception ex) when(ex is SocketException or System.IO.IOException or AuthenticationException or System.Net.Http.HttpRequestException or OperationCanceledException or RouterException)
        { token.ThrowIfCancellationRequested(); }
        return null;
    }
    internal static async Task<List<RouterApiEndpoint>> DiscoverApisAsync(IEnumerable<string> addresses,
        Func<string,CancellationToken,Task<RouterApiEndpoint?>> probe, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var candidates=addresses.Where(CredentialStore.IsPrivate).Distinct().Take(1024).ToArray();
        var found=new ConcurrentBag<RouterApiEndpoint>();var count=0;
        await Parallel.ForEachAsync(candidates,new ParallelOptions{MaxDegreeOfParallelism=24,CancellationToken=token},async(address,ct)=>
        {
            var endpoint=await probe(address,ct);
            if(endpoint is not null) found.Add(endpoint);
            progress?.Report(new(Interlocked.Increment(ref count),candidates.Length));
        });
        return found.OrderBy(r=>r.Address,StringComparer.Ordinal).ToList();
    }
    public static Task<List<RouterApiEndpoint>> DiscoverApisAsync(IEnumerable<string> addresses,IProgress<ScanProgress>? progress,CancellationToken token) =>
        DiscoverApisAsync(addresses,ProbeApiAsync,progress,token);
    /// <summary>With a pin, scans connected subnets for exactly that certificate. Without one
    /// (credential saved before pinning), only the saved or user-entered address is tried.</summary>
    public static Task<string> ResolveAsync(CredentialRecord record,string? pin,CancellationToken token)=>pin is null
        ? ResolveAsync(record.Address,record.Mac,[],(a,m,ct)=>MatchesAsync(a,m,null,ct),token,savedOnly:true)
        : ResolveAsync(record.Address,record.Mac,Candidates(record.Address),(a,m,ct)=>MatchesAsync(a,m,pin,ct),token);
}

internal sealed record RouterApiEndpoint(string Address, string Mac, string Status, string Pin="")
{
    public string Fingerprint => CertificatePin.IsValid(Pin) ? CertificatePin.Fingerprint(Pin) : "unknown";
}
internal sealed record ScanProgress(int Checked, int Total);
