using Android.Content;
using Android.Net;
using System.Net;

namespace ArrisControl;

internal static class AndroidNetwork
{
    static ConnectivityManager Manager => (ConnectivityManager)Android.App.Application.Context.GetSystemService(Context.ConnectivityService)!;
    static Network? LocalNetwork => Manager.GetAllNetworks().FirstOrDefault(n =>
        Manager.GetNetworkCapabilities(n) is { } c && (c.HasTransport(TransportType.Wifi) || c.HasTransport(TransportType.Ethernet)));
    public static bool HasLocalNetwork => LocalNetwork is not null;
    public static void UseLocal()
    {
        var network = LocalNetwork;
        if (network is null || !Manager.BindProcessToNetwork(network))
            throw new RouterException("Connect this phone to the Arris Wi-Fi or its local Ethernet network first. If Android says there is no Internet, choose to stay connected. The app cannot repair Wi-Fi authentication or DHCP failures.");
    }
    public static void UseInternet() => Manager.BindProcessToNetwork(null);
    public static string Status()
    {
        var network = LocalNetwork;
        if (network is null) return "Join your Arris Wi-Fi to use local control.";
        var caps = Manager.GetNetworkCapabilities(network);
        var addresses = Manager.GetLinkProperties(network)?.LinkAddresses?.Select(a => a.Address?.HostAddress).Where(a => a is not null && CredentialStore.IsPrivate(a)) ?? [];
        return "WI-FI / " + string.Join(", ", addresses) + "\n" +
            (caps?.HasCapability(NetCapability.Validated) == true ? "Internet available · local control ready" : "No validated Internet · local control available");
    }
    public static IEnumerable<string> Candidates()
    {
        var network = LocalNetwork;
        if (network is null) yield break;
        var props = Manager.GetLinkProperties(network);
        foreach (var route in props?.Routes ?? [])
        {
            var gateway = route.Gateway?.HostAddress;
            if (gateway is not null && CredentialStore.IsPrivate(gateway)) yield return gateway;
        }
        foreach (var address in props?.LinkAddresses ?? [])
        {
            var host = address.Address?.HostAddress;
            if (host is null || !CredentialStore.IsPrivate(host)) continue;
            var prefix = address.PrefixLength;
            if (prefix < 0 || prefix > 32) continue;
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var text = new IPAddress(new byte[] {(byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask}).ToString();
            foreach (var candidate in RouterDiscovery.SubnetCandidates(host, text)) yield return candidate;
        }
    }
}
