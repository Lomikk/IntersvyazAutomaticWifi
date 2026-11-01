using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using IS74Wifi.Core;

internal static class AuthorizationConnectionsContractTests
{
    public static async Task RunAsync()
    {
        using var temp = TempDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var json = new JsonFileStore();
        var settings = new SettingsStore(paths, json);
        Assert(settings.Load().AuthorizationConnections!.WifiSsids.SequenceEqual(["Campus Wi-Fi"]),
            "new installation must select Campus only");
        Assert(settings.Load().ConnectionSelectionPending && settings.Load().InitialAutomaticAuthorizationChoice is null,
            "new installation must keep registration paths open until setup is confirmed");
        var legacy = new AppSettings();
        Assert(IS74Wifi.App.Program.InitializeInitialSetupState(legacy, registered: true).InitialSetupCompleted == true,
            "registered legacy installation must skip first-run questions");
        Assert(IS74Wifi.App.Program.InitializeInitialSetupState(legacy, registered: false).ConnectionSelectionPending,
            "unregistered legacy installation must get first-run setup");
        var unfinished = settings.Load() with { InitialAutomaticAuthorizationChoice = false };
        settings.Save(unfinished);
        Assert(IS74Wifi.App.Program.InitializeInitialSetupState(settings.Load(), registered: true).ConnectionSelectionPending &&
            settings.Load().InitialAutomaticAuthorizationChoice == false,
            "registration/restart must not complete setup or lose an explicit No");
        File.WriteAllText(paths.SettingsFile, "{\"showTrayIcon\":false,\"directNetworkAdapterId\":\"usb\"}");
        var migrated = settings.Load();
        Assert(migrated.AuthorizationConnections!.Adapters.Single().Id == "usb" && !migrated.ShowTrayIcon &&
            migrated.DirectNetworkAdapterId is null && migrated.NetworkMode is null,
            "migration lost explicit adapter or unrelated settings");
        Assert(!File.ReadAllText(paths.SettingsFile).Contains("networkMode", StringComparison.Ordinal), "legacy mode was persisted after migration");
        File.WriteAllText(paths.SettingsFile, "{\"networkMode\":1,\"directNetworkAdapterId\":\"usb\"}");
        Assert(settings.Load().AuthorizationConnections!.Adapters.Length == 0, "Simple migration added the ignored legacy adapter");
        File.WriteAllText(paths.SettingsFile, "{\"networkMode\":0,\"directNetworkAdapterId\":\"system\"}");
        Assert(settings.Load().AuthorizationConnections!.Adapters.Length == 0, "system route entered the physical allowlist");
        var selected = new AuthorizationConnectionSelection
        {
            WifiSsids = ["Campus Wi-Fi", "Phone"], Adapters = [new("usb", "My phone")]
        };
        settings.Save(settings.Load() with { AuthorizationConnections = selected });
        Assert(settings.Load().AuthorizationConnections!.WifiSsids.SequenceEqual(selected.WifiSsids) &&
            settings.Load().AuthorizationConnections!.Adapters.Single().Name == "My phone", "selection did not survive restart");
        settings.Save(settings.Load() with { AuthorizationConnections = new() { WifiSsids = [], Adapters = [] } });
        Assert(settings.Load().AuthorizationConnections!.WifiSsids.Length == 0, "empty explicit selection reverted to default");
        var campus = new PhysicalAdapter("wifi", "Wi-Fi", NetworkInterfaceType.Wireless80211, true, 11,
            IPAddress.Parse("192.0.2.10"), [IPAddress.Parse("192.0.2.53")], true, "Campus Wi-Fi") { HardwareInterface = true };
        var usb = campus with { Id = "usb", Type = NetworkInterfaceType.Ethernet, Ssid = null };
        var other = campus with { Id = "other", Ssid = "Phone" };
        var vpn = campus with { Id = "vpn", LooksVirtual = true, HardwareInterface = false };
        var defaultList = new NetworkPathEnumerator(() => [campus, usb, other, vpn]);
        Assert(defaultList.EnumerateAutomaticCandidates().Select(p => p.AdapterId).SequenceEqual([campus.Id]),
            "default selection exposed USB, other SSID or VPN to authorization/probes");
        Assert(new NetworkPathEnumerator(() => [usb, other]).EnumerateAutomaticCandidates().Count == 0,
            "Campus-only selection must wait when Campus is missing");
        var allowed = new NetworkPathEnumerator(() => [campus, usb, other, vpn], selected);
        Assert(allowed.EnumerateAutomaticCandidates().Select(p => p.AdapterId).SequenceEqual([campus.Id, usb.Id, other.Id]),
            "explicit list did not include all selected physical paths or allowed a VPN");
        Assert(new NetworkPathEnumerator(() => [usb, other, vpn], new(), selectionPending: true)
            .EnumerateAutomaticCandidates().Select(path => path.AdapterId).SequenceEqual([usb.Id, other.Id]),
            "pre-selection setup must allow registration without a Campus/Wi-Fi adapter but exclude VPN");
        Assert(new NetworkPathEnumerator(() => [usb], new() { WifiSsids = [], Adapters = [] }, selectionPending: true)
            .EnumerateAutomaticCandidates().Count == 1, "unconfirmed draft must not cut off registration");
        settings.Save(settings.Load() with { InitialSetupCompleted = true, InitialAutomaticAuthorizationChoice = true });
        Assert(!settings.Load().ConnectionSelectionPending && settings.Load().InitialAutomaticAuthorizationChoice == true,
            "confirmed setup/Yes did not persist");
        var changed = usb with { Name = "Ethernet 13", IPv4Index = 99, SourceIPv4 = IPAddress.Parse("192.0.2.99") };
        Assert(selected.Allows(NetworkPathSnapshot.FromAdapter(changed)), "profile/index/address change lost USB selection");
        Assert(!selected.Allows(NetworkPathSnapshot.FromAdapter(changed with { Id = "replacement" })), "a new adapter instance inherited permission");
        Assert(!selected.Allows(NetworkPathSnapshot.FromAdapter(other with { Ssid = "phone" })), "SSID matching must be case-sensitive");
        Assert(!selected.Allows(NetworkPathSnapshot.FromAdapter(campus with { Id = "usb", Ssid = "Unselected" })),
            "wired adapter ID must not authorize arbitrary Wi-Fi SSIDs");
        Assert(new NetworkPathEnumerator(() => [campus, usb, other], new() { WifiSsids = [], Adapters = [] })
            .EnumerateAutomaticCandidates().Count == 0, "empty list exposed a fallback path");
        var normalized = new AuthorizationConnectionSelection { WifiSsids = ["Phone", "Phone", "", new string('я', 17)],
            Adapters = [new("usb", "USB"), new("USB", "duplicate"), new("system", "system")] }.Normalize();
        Assert(normalized.WifiSsids.SequenceEqual(["Phone"]) && normalized.Adapters.Length == 1,
            "invalid/duplicate selections were not normalized");

        var calls = 0;
        var ip = IPAddress.Parse("198.51.100.10");
        Task<DnsResolution> Lookup(string _, PhysicalAdapter __, CancellationToken token)
        { token.ThrowIfCancellationRequested(); calls++; return Task.FromResult(new DnsResolution([ip], DnsAddressSource.AdapterDns, false)); }
        var book = new PathAddressBook(paths, json, lookup: Lookup);
        await book.ResolveAsync("online.susu.ru", campus, CancellationToken.None);
        Assert(!File.Exists(paths.PathAddressFile), "unconnected DNS address was persisted as working");
        book.RememberConnected("online.susu.ru", campus, ip);
        Assert(File.Exists(paths.PathAddressFile), "reachable candidate did not persist");
        var forbiddenDns = new PathAddressBook(paths, json, lookup: (_, _, _) => throw new InvalidOperationException("DNS forbidden"));
        var rebound = campus with { SourceIPv4 = IPAddress.Parse("192.0.2.20"), IPv4Index = 22 };
        var result = await forbiddenDns.ResolveAsync("online.susu.ru", rebound, CancellationToken.None);
        Assert(result.Source == DnsAddressSource.PersistentAddress && result.Addresses.SequenceEqual([ip]),
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
        Assert(calls == 1 && dialCalls == 2 && File.Exists(paths.PathAddressFile),
            "Unreachable refreshed DNS, removed cache or did not make fresh connections");
        foreach (var wrong in new[] { vpn, usb with { HardwareInterface = null } })
        {
            try { await book.ResolveAsync("online.susu.ru", wrong, CancellationToken.None); throw new Exception("wrong network accepted"); }
            catch (DirectNetworkUnavailableException) { }
        }
        // The same book accepts every allowed physical path. It does not decide
        // LTE/Campus or transfer Wi-Fi addresses/auth state to another adapter.
        var usbAddress = IPAddress.Parse("198.51.100.30");
        usb = usb with { GatewayIPv4 = IPAddress.Parse("192.0.2.1") };
        var usbLookups = 0;
        var usbBook = new PathAddressBook(paths, json, lookup: (_, _, _) =>
        {
            usbLookups++;
            return Task.FromResult(new DnsResolution([usbAddress], DnsAddressSource.AdapterDns, false));
        });
        await usbBook.ResolveAsync("online.susu.ru", usb, CancellationToken.None);
        usbBook.RememberConnected("online.susu.ru", usb, usbAddress);
        var usbRestart = await forbiddenDns.ResolveAsync("online.susu.ru", usb with { Name = "Network 13", IPv4Index = 42 }, CancellationToken.None);
        Assert(usbLookups == 1 && usbRestart.Addresses.SequenceEqual([usbAddress]), "USB address was not independently persisted/reused across profile/index changes");
        Assert((await forbiddenDns.ResolveAsync("online.susu.ru", campus, CancellationToken.None)).Addresses.SequenceEqual([ip]),
            "USB overwrote Campus addresses");
        await usbBook.ResolveAsync("online.susu.ru", usb with { GatewayIPv4 = IPAddress.Parse("192.0.2.2") }, CancellationToken.None);
        Assert(usbLookups == 2, "new USB gateway reused another context's candidates");
        var otherResult = await usbBook.ResolveAsync("online.susu.ru", other, CancellationToken.None);
        Assert(otherResult.Addresses.SequenceEqual([usbAddress]), "Advanced physical Wi-Fi used another transport policy");
        await SharedProbeAndRunnerUseIpFirstAsync(paths, json, new[] { campus, usb, other }, ip);
        var original = File.ReadAllText(paths.PathAddressFile);
        var failedRefresh = new PathAddressBook(paths, json, lookup: Lookup,
            validate: (_, _, _, _) => throw new IOException("unreachable new candidate"));
        try { await failedRefresh.RefreshAsync(campus, CancellationToken.None); throw new Exception("expected failed refresh"); }
        catch (IOException) { }
        Assert(File.ReadAllText(paths.PathAddressFile) == original, "failed maintenance replaced existing working addresses");
        var updatedIp = IPAddress.Parse("198.51.100.20");
        var refreshedHosts = new List<string>();
        var refresh = new PathAddressBook(paths, json,
            lookup: (_, _, _) => Task.FromResult(new DnsResolution([updatedIp], DnsAddressSource.AdapterDns, false)),
            validate: (host, _, _, _) => { refreshedHosts.Add(host); return Task.CompletedTask; });
        await refresh.RefreshAsync(campus, CancellationToken.None);
        var afterRefresh = new PathAddressBook(paths, json,
            lookup: (_, _, _) => throw new InvalidOperationException("explicitly refreshed addresses lost"));
        foreach (var host in PathAddressBook.Hosts)
            Assert((await afterRefresh.ResolveAsync(host, campus, CancellationToken.None)).Addresses.SequenceEqual([updatedIp]),
                "successful explicit refresh did not replace the host across restart");
        Assert(refreshedHosts.Count == 3, "refresh skipped endpoint validation");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await book.ResolveAsync("online.susu.ru", campus, cancelled.Token); throw new Exception("cancellation ignored"); }
        catch (OperationCanceledException) { }
    }

    private static async Task SharedProbeAndRunnerUseIpFirstAsync(AppPaths paths, JsonFileStore json, PhysicalAdapter[] adapters, IPAddress address)
    {
        var seed = new PathAddressBook(paths, json, lookup: (_, _, _) =>
            Task.FromResult(new DnsResolution([address], DnsAddressSource.AdapterDns, false)),
            validate: (_, _, _, _) => Task.CompletedTask);
        foreach (var adapter in adapters) await seed.RefreshAsync(adapter, CancellationToken.None);
        foreach (var adapter in adapters)
        {
            var dials = new List<string>();
            var book = new PathAddressBook(paths, json,
                lookup: (_, _, _) => throw new InvalidOperationException("warm probe/runner must never resolve DNS"),
                testDialer: (selected, ip, port, _) =>
                {
                    Assert(selected.Id == adapter.Id && ip.Equals(address), "shared runner/probe lost the adapter/IP binding");
                    dials.Add(port == 443 ? "api" : "probe");
                    throw new SocketException((int)SocketError.NetworkUnreachable);
                });
            var path = NetworkPathSnapshot.FromAdapter(adapter);
            var probe = new NetworkPathProbe(book);
            Assert((await probe.ProbeAsync(path, TimeSpan.FromSeconds(1))).Status == NetworkPathProbeStatus.Unreachable,
                "common IP-first probe must classify transport failure without DNS fallback");
            var state = new PathAuthorizationStateStore(paths, json);
            var runner = new PathAuthorizationRunner(state, new AppSettings(), new DiagnosticLogger(paths), book);
            var outcome = await runner.RunAsync(path, new AuthorizationRequest("test-token", "9991234567", "test-device", AuthorizationAttemptReason.Manual, Force: true));
            Assert(outcome.Kind == AuthorizationOutcomeKind.RetryableBeforeStepOne && dials.Contains("api") && dials.Contains("probe"),
                "common authorization runner did not use the same IP-first book as bound probes");
        }
    }

    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
