using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using IS74Wifi.Core;

internal static class NetworkPathContractTests
{
    public static async Task RunAsync()
    {
        var campus = Adapter(
            id: "wifi-guid",
            type: NetworkInterfaceType.Wireless80211,
            source: "192.168.137.221",
            gateway: "192.168.137.1",
            ssid: "Campus Wi-Fi");
        var usb = Adapter(
            id: "usb-guid",
            type: NetworkInterfaceType.Ethernet,
            source: "10.253.184.3",
            gateway: "10.253.184.224");
        var vpn = Adapter(
            id: "tap-guid",
            type: NetworkInterfaceType.Ethernet,
            source: "10.72.2.153",
            gateway: null,
            looksVirtual: true,
            name: "TAP-Windows Adapter V9");
        var stealthVpn = Adapter(
            id: "stealth-vpn",
            type: NetworkInterfaceType.Ethernet,
            source: "10.99.0.2",
            gateway: null,
            looksVirtual: false,
            name: "Acme Adapter",
            hardware: false);
        var disconnected = Adapter(
            id: "ethernet-guid",
            type: NetworkInterfaceType.Ethernet,
            source: null,
            gateway: null,
            up: false);

        var enumerator = new NetworkPathEnumerator(() => [vpn, stealthVpn, campus, usb, disconnected]);
        var all = enumerator.EnumerateAll();
        var physical = enumerator.EnumeratePhysical();
        var automatic = enumerator.EnumerateAutomaticCandidates();
        Assert(all.Count == 5, "read-only path enumeration lost visible adapters");
        Assert(physical.Select(path => path.AdapterId).SequenceEqual([stealthVpn.Id, campus.Id, usb.Id, disconnected.Id]),
            "read-only physical-shape enumeration unexpectedly hid an unclassified Ethernet adapter");
        Assert(automatic.Select(path => path.AdapterId).SequenceEqual([campus.Id, usb.Id, disconnected.Id]),
            "automatic candidates must require the positive Windows hardware signal and exclude VPN");
        Assert(PhysicalAdapterSelection.Select([stealthVpn, usb], preferredId: null)?.Id == usb.Id,
            "automatic legacy adapter selection accepted an interface Windows marked non-hardware");

        var campusPath = NetworkPathSnapshot.FromAdapter(campus);
        var changedIfIndex = campusPath with
        {
            InterfaceIndex = 77,
            SourceIPv4 = IPAddress.Parse("192.168.137.250")
        };
        Assert(campusPath.Identity == changedIfIndex.Identity,
            "ifIndex/DHCP changes must not rotate the path identity for the same Wi-Fi network");
        Assert(campusPath.Identity != (campusPath with { Ssid = "Laptop-Hotspot" }).Identity,
            "a Wi-Fi SSID change must create a different path identity");

        var usbPath = NetworkPathSnapshot.FromAdapter(usb);
        Assert(usbPath.Identity != (usbPath with { GatewayIPv4 = IPAddress.Parse("10.253.200.1") }).Identity,
            "Ethernet/USB gateway changes must conservatively rotate path identity");
        Assert(!NetworkPathSnapshot.FromAdapter(vpn).IsPhysicalCandidate,
            "VPN adapter was accepted as a physical authorization path");
        Assert(!NetworkPathSnapshot.FromAdapter(disconnected).CanProbe,
            "disconnected/no-IPv4 path must not be probeable");

        var routeResolver = new SystemNetworkRouteResolver(_ => IPAddress.Parse("10.72.2.153"));
        var selected = routeResolver.Resolve(IPAddress.Parse("10.100.12.18"), all);
        Assert(selected.SourceAddress?.Equals(IPAddress.Parse("10.72.2.153")) == true &&
               selected.LocalPath?.AdapterId == vpn.Id && !selected.LocalPath.IsPhysicalCandidate,
            "system route diagnostics must be able to report a VPN path without making it authorizable");

        var fallbackDns = new InterfaceDnsResolver(
            (_, _, _) => throw new IOException("adapter DNS unavailable"),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("198.51.100.20") }));
        NetworkPathSnapshot? dialedPath = null;
        var explicitConnector = new DirectNetworkConnector(
            usbPath,
            fallbackDns,
            (selectedAdapter, address, port, _) =>
            {
                dialedPath = NetworkPathSnapshot.FromAdapter(selectedAdapter);
                Assert(address.Equals(IPAddress.Parse("198.51.100.20")) && port == 80,
                    "explicit path connector changed the DNS-fallback endpoint");
                return ValueTask.FromResult<Stream>(new MemoryStream());
            });
        await using (var stream = await explicitConnector.ConnectHostAsync("w.is74.ru", 80, CancellationToken.None))
        {
        }
        Assert(dialedPath?.AdapterId == usbPath.AdapterId &&
               dialedPath.InterfaceIndex == usbPath.InterfaceIndex &&
               dialedPath.SourceIPv4?.Equals(usbPath.SourceIPv4) == true,
            "explicit connector did not preserve the selected path after DNS fallback");

        var failedPath = new DirectNetworkConnector(
            campusPath,
            new InterfaceDnsResolver(
                (_, _, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.10") }),
                (_, _) => throw new InvalidOperationException("system fallback must not be needed")),
            (_, _, _, _) => throw new SocketException((int)SocketError.NetworkUnreachable));
        try
        {
            await failedPath.ConnectHostAsync("w.is74.ru", 80, CancellationToken.None);
            throw new InvalidOperationException("failed path silently fell back to another route");
        }
        catch (DirectNetworkUnavailableException)
        {
        }

        // A separate path remains independently usable after another path fails.
        var independentCalls = 0;
        var healthyPath = new DirectNetworkConnector(
            usbPath,
            new InterfaceDnsResolver(
                (_, _, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.11") }),
                (_, _) => throw new InvalidOperationException("system fallback must not be needed")),
            (_, _, _, _) =>
            {
                independentCalls++;
                return ValueTask.FromResult<Stream>(new MemoryStream());
            });
        await using (var stream = await healthyPath.ConnectHostAsync("w.is74.ru", 80, CancellationToken.None))
        {
        }
        Assert(independentCalls == 1, "failure of one path contaminated another path connector");

        var probe = new NetworkPathProbe((path, _, _) => Task.FromResult(
            path.AdapterId == campus.Id
                ? Probe(HttpStatusCode.OK, online: false)
                : Probe(HttpStatusCode.Found, online: true,
                    location: InternetConnectivityProbe.ExpectedLocation)));
        Assert((await probe.ProbeAsync(campusPath, TimeSpan.FromSeconds(1))).Status == NetworkPathProbeStatus.Captive,
            "HTTP 200 on the bound SUSU probe must classify as captive");
        Assert((await probe.ProbeAsync(usbPath, TimeSpan.FromSeconds(1))).Status == NetworkPathProbeStatus.Internet,
            "validated 302 on the bound SUSU probe must classify as Internet");

        var unreachable = NetworkPathProbe.Classify(new InternetProbeResult(
            false, false, null, null, TransportFailureKind.DirectRouteUnavailable, TimeSpan.Zero));
        Assert(unreachable == NetworkPathProbeStatus.Unreachable,
            "transport failure must classify as unreachable, not captive");
        var ambiguous = NetworkPathProbe.Classify(Probe(HttpStatusCode.ServiceUnavailable, online: false));
        Assert(ambiguous == NetworkPathProbeStatus.Ambiguous,
            "unexpected HTTP response must remain ambiguous");

        var virtualProbeCalls = 0;
        var virtualProbe = new NetworkPathProbe((_, _, _) =>
        {
            virtualProbeCalls++;
            return Task.FromResult(Probe(HttpStatusCode.OK, online: false));
        });
        try
        {
            await virtualProbe.ProbeAsync(NetworkPathSnapshot.FromAdapter(vpn), TimeSpan.FromSeconds(1));
            throw new InvalidOperationException("VPN path was probed as an authorization candidate");
        }
        catch (DirectNetworkUnavailableException)
        {
        }
        Assert(virtualProbeCalls == 0, "VPN candidate reached the bound probe implementation");
    }

    private static InternetProbeResult Probe(HttpStatusCode status, bool online, Uri? location = null) =>
        new(online, true, status, null, TransportFailureKind.None, TimeSpan.FromMilliseconds(1), location);

    private static PhysicalAdapter Adapter(
        string id,
        NetworkInterfaceType type,
        string? source,
        string? gateway,
        string? ssid = null,
        bool up = true,
        bool looksVirtual = false,
        string? name = null,
        bool hardware = true)
    {
        var adapter = new PhysicalAdapter(
            id,
            name ?? id,
            type,
            up,
            11,
            source is null ? null : IPAddress.Parse(source),
            [IPAddress.Parse("192.0.2.53")],
            gateway is not null,
            ssid,
            looksVirtual)
        {
            GatewayIPv4 = gateway is null ? null : IPAddress.Parse(gateway),
            HardwareInterface = hardware,
            ConnectorPresent = hardware
        };
        return adapter;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
