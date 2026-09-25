using System.Net;
using System.Net.NetworkInformation;
using IS74Wifi.Core;

internal static class DirectNetworkContractTests
{
    public static async Task RunAsync()
    {
        var wifi = Adapter("wifi-a", NetworkInterfaceType.Wireless80211, "Campus Wi-Fi", true);
        var other = Adapter("wifi-b", NetworkInterfaceType.Wireless80211, "Other", true);
        var ethernet = Adapter("eth-a", NetworkInterfaceType.Ethernet, null, true);
        var offline = Adapter("wifi-down", NetworkInterfaceType.Wireless80211, "Campus Wi-Fi", false);
        var virtualNic = Adapter("tunnel-a", NetworkInterfaceType.Ethernet, null, true)
            with { LooksVirtual = true };
        var adapters = new[] { virtualNic, ethernet, other, wifi, offline };
        Assert(PhysicalAdapterSelection.Select(adapters, null)?.Id == wifi.Id,
            "automatic mode must prefer the connected Campus Wi-Fi adapter");
        Assert(PhysicalAdapterSelection.Select(adapters, other.Id)?.Id == other.Id,
            "manual selection must override automatic preferences");
        Assert(PhysicalAdapterSelection.Select(adapters, offline.Id) is null,
            "an offline manual choice must not silently fall back to another interface");
        Assert(PhysicalAdapterSelection.Select(adapters, "unplugged-id") is null,
            "a missing manual adapter must fail closed");
        Assert(PhysicalAdapterSelection.Select(adapters, PhysicalAdapterSelection.SystemRoute) is null,
            "system route must be an explicit opt-out of direct selection");
        Assert(PhysicalAdapterSelection.Select([virtualNic, ethernet], null)?.Id == ethernet.Id,
            "an automatic choice must not prefer a virtual ethernet interface");
        Assert(PhysicalAdapterSelection.Select([virtualNic], null) is null,
            "automatic mode must not silently select VPN-only virtual adapters");
        Assert(PhysicalAdapterSelection.Select([virtualNic], virtualNic.Id)?.Id == virtualNic.Id,
            "explicit manual override must be possible for heuristic false positives");
        Assert(PhysicalAdapterSelection.Select([ethernet], null)?.Id == ethernet.Id,
            "ethernet must be eligible when wireless is unavailable");
        Assert(!PhysicalAdapterSelection.IsPhysicalCandidate(NetworkInterfaceType.Ethernet,
            "WireGuard Tunnel", "Virtual Ethernet Adapter"), "virtual VPN adapter was eligible for automatic routing");
        Assert(!PhysicalAdapterSelection.IsPhysicalCandidate(NetworkInterfaceType.Tunnel,
            "VPN", "VPN"), "a tunnel adapter was eligible for direct routing");

        var query = InterfaceDnsResolver.CreateQuery("api.is74.ru", 0x1234);
        var response = new byte[query.Length + 16];
        Array.Copy(query, response, query.Length);
        response[2] = 0x81; response[3] = 0x80;
        response[6] = 0; response[7] = 1;
        var offset = query.Length;
        response[offset++] = 0xc0; response[offset++] = 0x0c; // answer name = QNAME
        response[offset++] = 0; response[offset++] = 1; // A
        response[offset++] = 0; response[offset++] = 1; // IN
        response[offset++] = 0; response[offset++] = 0;
        response[offset++] = 0; response[offset++] = 30; // TTL
        response[offset++] = 0; response[offset++] = 4; // IPv4 length
        response[offset++] = 203; response[offset++] = 0;
        response[offset++] = 113; response[offset++] = 42;
        Assert(InterfaceDnsResolver.ParseResponse(response, "api.is74.ru", 0x1234)
                .SequenceEqual([IPAddress.Parse("203.0.113.42")]),
            "adapter DNS response was not parsed correctly");
        try
        {
            _ = InterfaceDnsResolver.ParseResponse(response, "w.is74.ru", 0x1234);
            throw new InvalidOperationException("a mismatched DNS response was accepted");
        }
        catch (FormatException) { }
        try
        {
            _ = InterfaceDnsResolver.ParseResponse(response, "api.is74.ru", 0x1235);
            throw new InvalidOperationException("DNS transaction ID was not verified");
        }
        catch (FormatException) { }

        var connector = new DirectNetworkConnector(() => adapters, "unplugged-id");
        try
        {
            await connector.ConnectHostAsync("api.is74.ru", 443, CancellationToken.None);
            throw new InvalidOperationException("missing adapter silently fell back to another route");
        }
        catch (DirectNetworkUnavailableException) { }
        try
        {
            await connector.ConnectHostAsync("example.org", 443, CancellationToken.None);
            throw new InvalidOperationException("arbitrary third-party host bypassed the VPN");
        }
        catch (DirectNetworkUnavailableException) { }
    }

    private static PhysicalAdapter Adapter(string id, NetworkInterfaceType type, string? ssid, bool up) =>
        new(id, id, type, up, 11, IPAddress.Parse("192.168.2.24"),
            [IPAddress.Parse("192.168.2.1")], true, ssid);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
