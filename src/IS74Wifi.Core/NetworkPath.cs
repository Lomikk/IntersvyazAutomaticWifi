using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IS74Wifi.Core;

public sealed record NetworkPathIdentity(
    string AdapterId,
    NetworkInterfaceType InterfaceType,
    string NetworkDiscriminator);

/// <summary>
/// Read-only view of one locally observable network path. It intentionally stops
/// at the first gateway: an upstream device/NAT after that gateway is not visible
/// and must not be guessed.
/// </summary>
public sealed record NetworkPathSnapshot(
    string AdapterId,
    string Name,
    NetworkInterfaceType InterfaceType,
    bool IsUp,
    int InterfaceIndex,
    IPAddress? SourceIPv4,
    IPAddress? GatewayIPv4,
    IReadOnlyList<IPAddress> DnsServers,
    string? Ssid,
    bool LooksVirtual)
{
    public bool IsWifi => InterfaceType == NetworkInterfaceType.Wireless80211;
    public bool IsPhysicalCandidate =>
        !LooksVirtual &&
        InterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.FastEthernetFx;

    public bool CanProbe => IsPhysicalCandidate && IsUp && InterfaceIndex > 0 && SourceIPv4 is not null;

    public NetworkPathIdentity Identity => new(
        AdapterId,
        InterfaceType,
        CreateNetworkDiscriminator(IsWifi, Ssid, GatewayIPv4));

    internal PhysicalAdapter ToPhysicalAdapter()
    {
        if (!IsPhysicalCandidate)
        {
            throw new DirectNetworkUnavailableException("Сетевой путь не является физическим кандидатом для прямой авторизации.");
        }

        return new PhysicalAdapter(
            AdapterId,
            Name,
            InterfaceType,
            IsUp,
            InterfaceIndex,
            SourceIPv4,
            DnsServers,
            GatewayIPv4 is not null,
            Ssid,
            LooksVirtual)
        {
            GatewayIPv4 = GatewayIPv4
        };
    }

    public static NetworkPathSnapshot FromAdapter(PhysicalAdapter adapter) => new(
        adapter.Id,
        adapter.Name,
        adapter.Type,
        adapter.IsUp,
        adapter.IPv4Index,
        adapter.SourceIPv4,
        adapter.GatewayIPv4,
        adapter.DnsServers,
        adapter.Ssid,
        adapter.LooksVirtual);

    internal static string CreateNetworkDiscriminator(bool isWifi, string? ssid, IPAddress? gateway)
    {
        if (isWifi && !string.IsNullOrWhiteSpace(ssid))
        {
            return "ssid:" + ssid.Trim().ToUpperInvariant();
        }

        if (gateway is not null)
        {
            return "gateway:" + gateway;
        }

        // Unknown is intentionally conservative: a later known SSID/gateway creates
        // a different identity and therefore forces a fresh probe rather than reusing
        // state from a network that merely happened to use the same adapter.
        return "network:unknown";
    }
}

public sealed class NetworkPathEnumerator(Func<IReadOnlyList<PhysicalAdapter>>? enumerate = null)
{
    private readonly Func<IReadOnlyList<PhysicalAdapter>> enumerateAdapters =
        enumerate ?? PhysicalAdapterSelection.Enumerate;

    public IReadOnlyList<NetworkPathSnapshot> EnumerateAll() =>
        enumerateAdapters().Select(NetworkPathSnapshot.FromAdapter).ToArray();

    public IReadOnlyList<NetworkPathSnapshot> EnumeratePhysical() =>
        EnumerateAll().Where(path => path.IsPhysicalCandidate).ToArray();
}

public sealed record SystemRouteSnapshot(
    IPAddress RemoteAddress,
    IPAddress? SourceAddress,
    NetworkPathSnapshot? LocalPath);

/// <summary>
/// Diagnoses the route Windows would select for a concrete IPv4 destination.
/// UDP Connect performs only local route selection; no datagram is sent.
/// </summary>
public sealed class SystemNetworkRouteResolver(Func<IPAddress, IPAddress?>? resolveSource = null)
{
    private readonly Func<IPAddress, IPAddress?> sourceResolver = resolveSource ?? ResolveSourceAddress;

    public SystemRouteSnapshot Resolve(
        IPAddress remoteAddress,
        IReadOnlyList<NetworkPathSnapshot> knownPaths)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        ArgumentNullException.ThrowIfNull(knownPaths);

        if (remoteAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Диагностика сетевого пути поддерживает только IPv4.", nameof(remoteAddress));
        }

        var source = sourceResolver(remoteAddress);
        var path = source is null
            ? null
            : knownPaths.FirstOrDefault(candidate => candidate.SourceIPv4?.Equals(source) == true);
        return new SystemRouteSnapshot(remoteAddress, source, path);
    }

    private static IPAddress? ResolveSourceAddress(IPAddress remoteAddress)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(remoteAddress, 9));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}

public enum NetworkPathProbeStatus
{
    Internet,
    Captive,
    Unreachable,
    Ambiguous
}

public sealed record NetworkPathProbeResult(
    NetworkPathIdentity Identity,
    NetworkPathProbeStatus Status,
    InternetProbeResult InternetProbe);

/// <summary>
/// Safe read-only probe for one explicit physical path. It never calls stepOne or
/// stepTwo and never falls back to a VPN/system TCP route.
/// </summary>
public sealed class NetworkPathProbe(
    Func<NetworkPathSnapshot, TimeSpan, CancellationToken, Task<InternetProbeResult>>? probeOverride = null)
{
    private readonly Func<NetworkPathSnapshot, TimeSpan, CancellationToken, Task<InternetProbeResult>>? probe = probeOverride;

    public async Task<NetworkPathProbeResult> ProbeAsync(
        NetworkPathSnapshot path,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.IsPhysicalCandidate)
        {
            throw new DirectNetworkUnavailableException("VPN/виртуальный интерфейс не является путем captive-авторизации.");
        }
        if (!path.CanProbe)
        {
            return new NetworkPathProbeResult(
                path.Identity,
                NetworkPathProbeStatus.Unreachable,
                new InternetProbeResult(
                    Online: false,
                    HttpResponseReceived: false,
                    StatusCode: null,
                    Body: null,
                    FailureKind: TransportFailureKind.DirectRouteUnavailable,
                    Elapsed: TimeSpan.Zero));
        }

        var result = probe is null
            ? await ProbeBoundAsync(path, timeout, cancellationToken).ConfigureAwait(false)
            : await probe(path, timeout, cancellationToken).ConfigureAwait(false);

        return new NetworkPathProbeResult(path.Identity, Classify(result), result);
    }

    public static NetworkPathProbeStatus Classify(InternetProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Online)
        {
            return NetworkPathProbeStatus.Internet;
        }
        if (!result.HttpResponseReceived)
        {
            return NetworkPathProbeStatus.Unreachable;
        }
        if (result.StatusCode == HttpStatusCode.OK)
        {
            return NetworkPathProbeStatus.Captive;
        }
        return NetworkPathProbeStatus.Ambiguous;
    }

    private static async Task<InternetProbeResult> ProbeBoundAsync(
        NetworkPathSnapshot path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var connector = new DirectNetworkConnector(path);
        using var http = HttpClientProfiles.CreateInternetProbeClient(connector);
        return await new InternetConnectivityProbe(new HttpTransport(http))
            .ProbeAsync(timeout, cancellationToken)
            .ConfigureAwait(false);
    }
}
