using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using IS74Wifi.Core;

internal static class CampusNetworkModeContractTests
{
    public static async Task RunAsync()
    {
        using var temp = TempDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var json = new JsonFileStore();
        var settings = new SettingsStore(paths, json);
        Assert(settings.Load().NetworkMode == NetworkMode.Simple, "new installation must use Simple");
        File.WriteAllText(paths.SettingsFile, "{\"showTrayIcon\":false,\"directNetworkAdapterId\":\"usb\"}");
        Assert(settings.Load().NetworkMode == NetworkMode.Advanced && settings.Load().DirectNetworkAdapterId == "usb" && !settings.Load().ShowTrayIcon,
            "old settings silently changed existing user network behavior");
        settings.Save(settings.Load() with { NetworkMode = NetworkMode.Simple });
        Assert(settings.Load().NetworkMode == NetworkMode.Simple, "explicit mode must survive restart");

        var campus = new PhysicalAdapter("wifi", "Wi-Fi", NetworkInterfaceType.Wireless80211, true, 11,
            IPAddress.Parse("192.0.2.10"), [IPAddress.Parse("192.0.2.53")], true, "Campus Wi-Fi") { HardwareInterface = true };
        var usb = campus with { Id = "usb", Type = NetworkInterfaceType.Ethernet, Ssid = null };
        var other = campus with { Id = "other", Ssid = "Other" };
        var vpn = campus with { Id = "vpn", LooksVirtual = true, HardwareInterface = false };
        var simple = new NetworkPathEnumerator(() => [campus, usb, other, vpn], NetworkMode.Simple);
        Assert(simple.EnumerateAutomaticCandidates().Select(p => p.AdapterId).SequenceEqual([campus.Id]),
            "Simple exposed USB, other SSID or VPN to authorization/probes");
        Assert(new NetworkPathEnumerator(() => [usb, other], NetworkMode.Simple).EnumerateAutomaticCandidates().Count == 0,
            "Simple must wait without alternative path when Campus is missing");
        Assert(new NetworkPathEnumerator(() => [campus, usb, other], NetworkMode.Advanced).EnumerateAutomaticCandidates().Count == 3,
            "Advanced must preserve previous physical candidates");

        var calls = 0;
        var ip = IPAddress.Parse("198.51.100.10");
        Task<DnsResolution> Lookup(string _, PhysicalAdapter __, CancellationToken token)
        { token.ThrowIfCancellationRequested(); calls++; return Task.FromResult(new DnsResolution([ip], DnsAddressSource.AdapterDns, false)); }
        var book = new CampusAddressBook(paths, json, lookup: Lookup);
        await book.ResolveAsync("online.susu.ru", campus, CancellationToken.None);
        Assert(!File.Exists(paths.CampusAddressFile), "unconnected DNS address was persisted as working");
        book.RememberConnected("online.susu.ru", campus, ip);
        Assert(File.Exists(paths.CampusAddressFile), "reachable candidate did not persist");
        var forbiddenDns = new CampusAddressBook(paths, json, lookup: (_, _, _) => throw new InvalidOperationException("DNS forbidden"));
        var rebound = campus with { SourceIPv4 = IPAddress.Parse("192.0.2.20"), IPv4Index = 22 };
        var result = await forbiddenDns.ResolveAsync("online.susu.ru", rebound, CancellationToken.None);
        Assert(result.Source == DnsAddressSource.PersistentCampus && result.Addresses.SequenceEqual([ip]),
            "persisted IP did not survive runtime restart/DHCP/index change");
        var dialCalls = 0;
        var connector = new DirectNetworkConnector(() => [rebound], rebound.Id,
            testDialer: (adapter, address, _, _) =>
            {
                Assert(adapter.IPv4Index == 22 && adapter.SourceIPv4!.Equals(rebound.SourceIPv4) && address.Equals(ip), "stale interface binding reused");
                dialCalls++;
                throw new SocketException((int)SocketError.NetworkUnreachable);
            }, addressLookup: forbiddenDns.ResolveAsync, connected: forbiddenDns.RememberConnected);
        for (var i = 0; i < 2; i++)
        {
            try { await connector.ConnectHostAsync("online.susu.ru", 80, CancellationToken.None); throw new Exception("expected transport failure"); }
            catch (DirectNetworkUnavailableException) { }
        }
        Assert(calls == 1 && dialCalls == 2 && File.Exists(paths.CampusAddressFile),
            "Unreachable refreshed DNS, removed cache or did not make fresh connections");
        foreach (var wrong in new[] { usb, other, vpn })
        {
            try { await book.ResolveAsync("online.susu.ru", wrong, CancellationToken.None); throw new Exception("wrong network accepted"); }
            catch (DirectNetworkUnavailableException) { }
        }
        var original = File.ReadAllText(paths.CampusAddressFile);
        var failedRefresh = new CampusAddressBook(paths, json, lookup: Lookup,
            validate: (_, _, _, _) => throw new IOException("unreachable new candidate"));
        try { await failedRefresh.RefreshAsync(campus, CancellationToken.None); throw new Exception("expected failed refresh"); }
        catch (IOException) { }
        Assert(File.ReadAllText(paths.CampusAddressFile) == original, "failed maintenance replaced existing working addresses");
        var updatedIp = IPAddress.Parse("198.51.100.20");
        var refreshedHosts = new List<string>();
        var refresh = new CampusAddressBook(paths, json,
            lookup: (_, _, _) => Task.FromResult(new DnsResolution([updatedIp], DnsAddressSource.AdapterDns, false)),
            validate: (host, _, _, _) => { refreshedHosts.Add(host); return Task.CompletedTask; });
        await refresh.RefreshAsync(campus, CancellationToken.None);
        var afterRefresh = new CampusAddressBook(paths, json,
            lookup: (_, _, _) => throw new InvalidOperationException("explicitly refreshed addresses lost"));
        foreach (var host in CampusAddressBook.Hosts)
            Assert((await afterRefresh.ResolveAsync(host, campus, CancellationToken.None)).Addresses.SequenceEqual([updatedIp]),
                "successful explicit refresh did not replace the host across restart");
        Assert(refreshedHosts.Count == 3, "refresh skipped endpoint validation");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await book.ResolveAsync("online.susu.ru", campus, cancelled.Token); throw new Exception("cancellation ignored"); }
        catch (OperationCanceledException) { }
    }

    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
