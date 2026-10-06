using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IS74Wifi.Core;

public sealed record TelemetryPathContext(
    string PathKind,
    bool DirectCampus,
    bool PreferredPath,
    bool? VpnActive,
    string PathStateBefore,
    bool KnownAuthWindow,
    bool AuthWindowApproximate)
{
    public static TelemetryPathContext Create(
        NetworkPathSnapshot path,
        PathAuthorizationState? state,
        bool preferredPath,
        bool? vpnActive,
        DateTimeOffset nowUtc)
    {
        var directCampus = path.IsWifi && path.Ssid is not null && SsidPolicy.IsTarget(path.Ssid);
        var knownWindow = state?.ExpectedExpiryUtc is { } expiry && expiry > nowUtc;
        return new TelemetryPathContext(
            PathKind(path),
            directCampus,
            preferredPath,
            vpnActive,
            StateName(state?.Status ?? PathAuthorizationStatus.Unknown),
            knownWindow,
            knownWindow && !directCampus);
    }

    internal static string PathKind(NetworkPathSnapshot path) => path.InterfaceType switch
    {
        NetworkInterfaceType.Wireless80211 => "wifi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "ethernet",
        _ => "other_physical"
    };

    internal static string StateName(PathAuthorizationStatus status) => status switch
    {
        PathAuthorizationStatus.Internet => "internet",
        PathAuthorizationStatus.Captive => "captive",
        PathAuthorizationStatus.Unreachable => "unreachable",
        PathAuthorizationStatus.Ambiguous => "ambiguous",
        PathAuthorizationStatus.Disconnected => "disconnected",
        _ => "unknown"
    };

    internal static string ProbeName(NetworkPathProbeStatus status) => status switch
    {
        NetworkPathProbeStatus.Internet => "internet",
        NetworkPathProbeStatus.Captive => "captive",
        NetworkPathProbeStatus.Unreachable => "unreachable",
        _ => "ambiguous"
    };
}

public sealed class NetworkPathTelemetryRecorder(
    string installId,
    TelemetryQueue queue,
    string appVersion)
{
    public void RecordObservation(
        NetworkPathSnapshot path,
        PathAuthorizationState? stateBefore,
        string reason,
        NetworkPathProbeStatus initialResult,
        NetworkPathProbeStatus result,
        bool settlingRetryUsed,
        double durationMs,
        bool preferredPath,
        bool vpnActive,
        DateTimeOffset nowUtc)
    {
        var context = TelemetryPathContext.Create(path, stateBefore, preferredPath, vpnActive, nowUtc);
        var evt = new TelemetryPathObservationEvent
        {
            InstallId = installId,
            AppVersion = appVersion,
            EventId = "event-" + Guid.NewGuid().ToString("N"),
            Reason = reason,
            InitialResult = TelemetryPathContext.ProbeName(initialResult),
            Result = TelemetryPathContext.ProbeName(result),
            PathKind = context.PathKind,
            DirectCampus = context.DirectCampus,
            PreferredPath = context.PreferredPath,
            VpnActive = context.VpnActive == true,
            KnownAuthWindow = context.KnownAuthWindow,
            AuthWindowApproximate = context.AuthWindowApproximate,
            SettlingRetryUsed = settlingRetryUsed,
            DurationMs = Math.Round(Math.Max(0, durationMs), 3, MidpointRounding.AwayFromZero)
        };
        queue.Enqueue([TelemetrySerialization.Serialize(evt)]);
    }
}

public static class NetworkVpnDetector
{
    private static readonly IPAddress RouteProbeAddress = IPAddress.Parse("1.1.1.1");

    public static bool IsActive()
    {
        var source = ResolveSourceAddress(RouteProbeAddress);
        if (source is null) return false;

        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                var properties = network.GetIPProperties();
                if (!properties.UnicastAddresses.Any(address =>
                        address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        address.Address.Equals(source)))
                {
                    continue;
                }

                var index = properties.GetIPv4Properties()?.Index ?? 0;
                return PhysicalAdapterSelection.LooksVirtual(
                    network.NetworkInterfaceType,
                    network.Name,
                    network.Description,
                    index);
            }
            catch (NetworkInformationException)
            {
                // Interface disappeared while the route snapshot was being read.
            }
        }

        return false;
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
