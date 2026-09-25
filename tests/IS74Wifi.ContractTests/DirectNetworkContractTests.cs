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

        // Resolver success and adapter-qualified cache: the system resolver is
        // never contacted while the interface DNS can answer.
        var interfaceCalls = 0;
        var systemCalls = 0;
        var goodDns = new InterfaceDnsResolver(
            (_, _, _) =>
            {
                interfaceCalls++;
                return Task.FromResult(new[] { IPAddress.Parse("203.0.113.42") });
            },
            (_, _) =>
            {
                systemCalls++;
                throw new InvalidOperationException("Unexpected system DNS fallback");
            });
        var directResult = await goodDns.ResolveWithSourceAsync("api.is74.ru", wifi, CancellationToken.None);
        Assert(directResult.Source == DnsAddressSource.AdapterDns && !directResult.FromCache &&
               directResult.Addresses.SequenceEqual([IPAddress.Parse("203.0.113.42")]),
            "successful interface DNS lookup had incorrect provenance");
        var cachedResult = await goodDns.ResolveWithSourceAsync("api.is74.ru", wifi, CancellationToken.None);
        Assert(cachedResult.FromCache && cachedResult.Source == DnsAddressSource.AdapterDns &&
               interfaceCalls == 1 && systemCalls == 0, "interface DNS cache missed or used system DNS");

        // A failed raw UDP lookup is allowed to fall back for addresses ONLY.
        // The TCP dialer must still be handed the exact selected adapter.
        var failedInterfaceCalls = 0;
        var fallbackCalls = 0;
        var fallbackDns = new InterfaceDnsResolver(
            (_, _, _) =>
            {
                failedInterfaceCalls++;
                throw new IOException("UDP/53 blocked by VPN");
            },
            (_, _) =>
            {
                fallbackCalls++;
                return Task.FromResult(new[] { IPAddress.IPv6Loopback, IPAddress.Parse("198.51.100.12") });
            });
        var fallbackResult = await fallbackDns.ResolveWithSourceAsync("api.is74.ru", wifi, CancellationToken.None);
        Assert(fallbackResult.Source == DnsAddressSource.SystemFallback &&
               fallbackResult.Addresses.SequenceEqual([IPAddress.Parse("198.51.100.12")]),
            "system fallback should return IPv4 addresses and declare its provenance");
        Assert((await fallbackDns.ResolveWithSourceAsync("api.is74.ru", wifi, CancellationToken.None)).FromCache &&
               fallbackCalls == 1 && failedInterfaceCalls == 1, "fallback cache was not used");
        var dialCount = 0;
        var boundConnector = new DirectNetworkConnector(() => adapters, wifi.Id, fallbackDns,
            (selected, ip, port, _) =>
            {
                Assert(selected.Id == wifi.Id && selected.SourceIPv4 == wifi.SourceIPv4 &&
                       ip.Equals(IPAddress.Parse("198.51.100.12")) && port == 443,
                    "TCP lost the manual adapter/endpoint after DNS fallback");
                dialCount++;
                return ValueTask.FromResult<Stream>(new MemoryStream());
            });
        await using (var connection = await boundConnector.ConnectHostAsync("api.is74.ru", 443, CancellationToken.None)) { }
        Assert(dialCount == 1, "adapter-bound TCP dialer was not used after DNS fallback");

        var rejectedTcp = new DirectNetworkConnector(() => adapters, wifi.Id, fallbackDns,
            (_, _, _, _) => throw new System.Net.Sockets.SocketException(
                (int)System.Net.Sockets.SocketError.NetworkUnreachable));
        try
        {
            await rejectedTcp.ConnectHostAsync("api.is74.ru", 443, CancellationToken.None);
            throw new InvalidOperationException("VPN-blocked TCP silently fell back to the system route");
        }
        catch (DirectNetworkUnavailableException) { }

        // An adapter without IPv4 DNS servers may use the system resolver for
        // an IP, but only after an actual usable adapter has been selected.
        var withoutDns = wifi with { DnsServers = [] };
        var noAdapterDns = new InterfaceDnsResolver(
            systemLookup: (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.20") }));
        var noAdapterDnsResult = await noAdapterDns.ResolveWithSourceAsync(
            "w.is74.ru", withoutDns, CancellationToken.None);
        Assert(noAdapterDnsResult.Source == DnsAddressSource.SystemFallback,
            "missing adapter DNS did not use the address-only fallback");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await fallbackDns.ResolveWithSourceAsync("online.susu.ru", wifi, cancelled.Token);
            throw new InvalidOperationException("cancelled requests must not issue DNS fallback");
        }
        catch (OperationCanceledException) { }

        var portalPort = 0;
        var portalConnector = new DirectNetworkConnector(() => adapters, wifi.Id, goodDns,
            (_, _, port, _) =>
            {
                portalPort = port;
                return ValueTask.FromResult<Stream>(new MemoryStream());
            });
        Assert(await portalConnector.CanReachPortalAsync(TimeSpan.FromSeconds(1), CancellationToken.None) &&
               portalPort == 80, "side-effect-free portal preflight must use HTTP/80, not TLS/443");

        var brokenDns = new InterfaceDnsResolver(
            (_, _, _) => throw new IOException("no adapter DNS"),
            (_, _) => throw new IOException("no system DNS"));
        var forbiddenDials = 0;
        var failingConnector = new DirectNetworkConnector(() => adapters, wifi.Id, brokenDns,
            (_, _, _, _) =>
            {
                forbiddenDials++;
                throw new InvalidOperationException("TCP must not start without an IP");
            });
        try
        {
            await failingConnector.ConnectHostAsync("api.is74.ru", 443, CancellationToken.None);
            throw new InvalidOperationException("both DNS paths failed but TCP was attempted");
        }
        catch (CachedDnsUnavailableException) { }
        Assert(forbiddenDials == 0, "DNS failures must not trigger system-route TCP fallback");

        var missingAdapterDnsCalls = 0;
        var noAdapterConnector = new DirectNetworkConnector(() => adapters, "missing-manual-id",
            new InterfaceDnsResolver((_, _, _) =>
            {
                missingAdapterDnsCalls++;
                throw new IOException("missing adapter must short-circuit");
            }));
        try
        {
            await noAdapterConnector.ConnectHostAsync("api.is74.ru", 443, CancellationToken.None);
            throw new InvalidOperationException("missing manual adapter was silently replaced");
        }
        catch (DirectNetworkUnavailableException) { }
        Assert(missingAdapterDnsCalls == 0, "missing manual adapter must fail before DNS");

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
