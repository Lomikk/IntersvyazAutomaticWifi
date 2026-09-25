using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windows.Networking.Connectivity;

namespace IS74Wifi.Core;

/// <summary>Only physical-network candidates are eligible for direct authorization traffic.</summary>
public sealed record PhysicalAdapter(
    string Id,
    string Name,
    NetworkInterfaceType Type,
    bool IsUp,
    int IPv4Index,
    IPAddress? SourceIPv4,
    IReadOnlyList<IPAddress> DnsServers,
    bool HasGateway,
    string? Ssid,
    bool LooksVirtual = false)
{
    public bool CanConnect => IsUp && IPv4Index > 0 && SourceIPv4 is not null;
    public bool IsWifi => Type == NetworkInterfaceType.Wireless80211;
    public bool IsCampus => IsWifi && Ssid is not null && SsidPolicy.IsTarget(Ssid);
}

public static class PhysicalAdapterSelection
{
    // "system" is an explicit legacy-route choice, never an automatic fallback.
    public const string SystemRoute = "system";

    private static readonly string[] VirtualMarkers =
    [
        "vpn", "wireguard", "wintun", "openvpn", "tap-windows", "tunnel",
        "tailscale", "zerotier", "hamachi", "hyper-v", "vmware", "virtualbox",
        "virtual ethernet", "loopback", "nordlynx"
    ];

    public static bool IsPhysicalCandidate(NetworkInterfaceType type, string name, string description) =>
        (type is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or
                NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or
                NetworkInterfaceType.FastEthernetFx) &&
        !VirtualMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                                      description.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pure selection logic: a missing manually chosen adapter is never replaced.</summary>
    public static PhysicalAdapter? Select(IReadOnlyList<PhysicalAdapter> candidates, string? preferredId)
    {
        if (string.Equals(preferredId, SystemRoute, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var usable = candidates.Where(adapter => adapter.CanConnect).ToArray();
        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            // A manual selection may override a false-positive virtual-adapter heuristic.
            return usable.FirstOrDefault(adapter =>
                string.Equals(adapter.Id, preferredId, StringComparison.OrdinalIgnoreCase));
        }
        return usable.Where(adapter => !adapter.LooksVirtual)
            .OrderByDescending(adapter => adapter.IsCampus)
            .ThenByDescending(adapter => adapter.HasGateway)
            .ThenByDescending(adapter => adapter.IsWifi)
            .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static IReadOnlyList<PhysicalAdapter> Enumerate()
    {
        var ssidsByAdapter = new Dictionary<Guid, string>();
        try
        {
            foreach (var profile in NetworkInformation.GetConnectionProfiles())
            {
                var ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
                if (!string.IsNullOrWhiteSpace(ssid) && profile.NetworkAdapter is { } adapter)
                {
                    ssidsByAdapter[adapter.NetworkAdapterId] = ssid;
                }
            }
        }
        catch
        {
            // NetworkInformation may be unavailable under some Windows policies;
            // interface enumeration and a manual selection must still work.
        }

        var result = new List<PhysicalAdapter>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.NetworkInterfaceType is not (NetworkInterfaceType.Wireless80211 or
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or
                NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx))
            {
                continue;
            }
            try
            {
                var properties = network.GetIPProperties();
                var ipv4 = properties.UnicastAddresses
                    .Select(address => address.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                               !IPAddress.IsLoopback(address) &&
                                               !address.Equals(IPAddress.Any));
                var index = properties.GetIPv4Properties()?.Index ?? 0;
                var dns = properties.DnsAddresses
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                      !IPAddress.IsLoopback(address) &&
                                      !address.Equals(IPAddress.Any))
                    .Distinct()
                    .ToArray();
                var gateway = properties.GatewayAddresses.Any(address =>
                    address.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !address.Address.Equals(IPAddress.Any));
                string? ssid = null;
                if (Guid.TryParse(network.Id, out var guid))
                {
                    ssidsByAdapter.TryGetValue(guid, out ssid);
                }
                result.Add(new PhysicalAdapter(network.Id, network.Name, network.NetworkInterfaceType,
                    network.OperationalStatus == OperationalStatus.Up, index, ipv4, dns, gateway, ssid,
                    LooksVirtual: !IsPhysicalCandidate(network.NetworkInterfaceType, network.Name, network.Description)));
            }
            catch (NetworkInformationException)
            {
                // An unplugged adapter can disappear while enumerating it.
            }
        }
        return result;
    }
}
