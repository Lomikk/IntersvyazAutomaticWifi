using System.Net;
using System.Net.NetworkInformation;
using IS74Wifi.Core;

internal static class PathAwareAgentContractTests
{
    public static async Task RunAsync()
    {
        await ProbeReasonsDoNotChangeSchedulingAsync();
        await WindowsSignalsOnlyProbeChangedSelectedPathsAsync();
        using var temp = TempDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var json = new JsonFileStore();
        var settings = new AppSettings();
        var now = new DateTimeOffset(2026, 10, 6, 1, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var state = new PathAuthorizationStateStore(paths, json, new RuntimeStateStore(paths, json), clock);
        var logger = new DiagnosticLogger(paths);

        var wifi = Path("wifi", NetworkInterfaceType.Wireless80211, "192.168.137.221", "192.168.137.1", "Laptop-Hotspot");
        var usb = Path("usb", NetworkInterfaceType.Ethernet, "10.253.184.3", "10.253.184.224");
        var vpn = Path("tap", NetworkInterfaceType.Ethernet, "10.72.2.153", null, hardware: false, looksVirtual: true);
        var probes = new Dictionary<string, NetworkPathProbeStatus>(StringComparer.OrdinalIgnoreCase)
        {
            [wifi.AdapterId] = NetworkPathProbeStatus.Captive,
            [usb.AdapterId] = NetworkPathProbeStatus.Captive,
            [vpn.AdapterId] = NetworkPathProbeStatus.Captive
        };
        var probeCalls = new List<string>();
        var probeTimeouts = new List<TimeSpan>();
        var probe = new NetworkPathProbe((path, timeout, _) =>
        {
            probeCalls.Add(path.AdapterId);
            probeTimeouts.Add(timeout);
            var status = probes[path.AdapterId];
            return Task.FromResult(ProbeResult(status));
        });
        var auth = new FakePathAuthorizationRunner(state, clock, settings);
        var preferred = new PreferredNetworkPathResolver(_ => wifi.Identity);
        var coordinator = new PathAwareAgentCoordinator(
            () => new StoredSecrets("token", "9123456789"),
            () => "device",
            () => [vpn, wifi, usb],
            state,
            probe,
            auth,
            settings,
            logger,
            clock,
            preferredPathResolver: preferred);

        await coordinator.TickAsync();
        Assert(probeCalls.SequenceEqual([wifi.AdapterId, wifi.AdapterId]),
            "preferred captive path must be confirmed twice and authorized before probing backup paths");
        Assert(probeTimeouts.Count == 2 &&
               probeTimeouts[0] == TimeSpan.FromSeconds(2) &&
               probeTimeouts[1] >= probeTimeouts[0],
            "short persisted guard timeout starved the second VPN-affected captive probe");
        Assert(!probeCalls.Contains(vpn.AdapterId), "VPN path reached the automatic probe");
        Assert(auth.Paths.Count == 1, "more than one SMS authorization flow ran in one tick");
        var first = auth.Paths[0];
        Assert(first.AdapterId == wifi.AdapterId,
            "the Windows-preferred Internet path must win simultaneous captive arbitration");
        Assert(state.Find(first.Identity)?.ExpectedExpiryUtc == now.AddHours(settings.AuthWindowHours),
            "successful path authorization did not create its own expiry");

        // A failed second probe still blocks stepOne: increasing its budget must
        // not weaken the two-independent-HTTP-responses safety gate.
        var uncertainPath = Path("vpn-uncertain", NetworkInterfaceType.Wireless80211,
            "192.168.137.230", "192.168.137.1", "VPN-affected-WiFi");
        var uncertainProbeCalls = 0;
        var uncertainProbe = new NetworkPathProbe((_, timeout, _) =>
        {
            uncertainProbeCalls++;
            Assert(timeout >= TimeSpan.FromSeconds(2),
                "VPN confirmation reverted to the insufficient old timeout");
            return Task.FromResult(ProbeResult(uncertainProbeCalls == 1
                ? NetworkPathProbeStatus.Captive : NetworkPathProbeStatus.Unreachable));
        });
        // This coordinator enumerates a different world. Give it an independent
        // store so MarkMissingAsDisconnected cannot mutate the arbitration fixture.
        using var uncertainTemp = TempDirectory.Create();
        var uncertainPaths = new AppPaths(uncertainTemp.Path);
        var uncertainState = new PathAuthorizationStateStore(uncertainPaths, json, timeProvider: clock);
        var uncertainAuth = new FakePathAuthorizationRunner(uncertainState, clock, settings);
        var uncertainCoordinator = new PathAwareAgentCoordinator(
            () => new StoredSecrets("token", "9123456789"),
            () => "device",
            () => [uncertainPath],
            uncertainState,
            uncertainProbe,
            uncertainAuth,
            settings,
            new DiagnosticLogger(uncertainPaths),
            clock,
            delayAsync: (_, _) => Task.CompletedTask);
        await uncertainCoordinator.TickAsync();
        Assert(uncertainProbeCalls == 2 && uncertainAuth.Paths.Count == 0,
            "failed secondary captive confirmation initiated an authorization");

        probeCalls.Clear();
        await coordinator.TickAsync();
        Assert(probeCalls.SequenceEqual([usb.AdapterId, usb.AdapterId]),
            "backup captive path must be confirmed twice before starting another SMS flow");
        Assert(auth.Paths.Count == 2 && auth.Paths[1].AdapterId == usb.AdapterId,
            "second captive path was not serviced after the preferred path completed");
        Assert(state.Find(usb.Identity)?.ExpectedExpiryUtc == now.AddHours(settings.AuthWindowHours),
            "second path did not receive an independent expiry");
        Assert(PathAuthorizationSchedule.GetNextCheckUtc(state.Find(wifi.Identity), wifi, settings, now) ==
                   now.AddHours(settings.AuthWindowHours).AddSeconds(-30) &&
               PathAuthorizationSchedule.GetNextCheckUtc(state.Find(usb.Identity), usb, settings, now) ==
                   now.AddHours(settings.AuthWindowHours).AddSeconds(-30),
            "two different physical paths must use the same symmetric pre-expiry schedule");

        // Preserve the old latency-first edge behaviour for the path Windows says
        // currently carries Internet: at the known expiry, the first automatic
        // attempt starts immediately without inserting a preliminary probe.
        state.RecordConfirmedAuthorization(wifi, now.AddHours(-24), now, now.AddHours(-24));
        probes[wifi.AdapterId] = NetworkPathProbeStatus.Captive;
        probeCalls.Clear();
        var authBeforeFastEdge = auth.Paths.Count;
        await coordinator.TickAsync();
        Assert(auth.Paths.Count == authBeforeFastEdge + 1 && auth.Paths[^1].AdapterId == wifi.AdapterId,
            "preferred path did not receive the latency-first timer shot at its known expiry");
        Assert(probeCalls.Count == 0,
            "preferred timer shot inserted a probe into the old latency-critical expiry path");

        // A DHCP/ifIndex transport change on the same identity forces an immediate probe,
        // but preserves the authorization history until that probe supplies new truth.
        var changedWifi = wifi with { InterfaceIndex = 77, SourceIPv4 = IPAddress.Parse("192.168.137.250") };
        probes[wifi.AdapterId] = NetworkPathProbeStatus.Internet;
        var changedCoordinator = new PathAwareAgentCoordinator(
            () => new StoredSecrets("token", "9123456789"),
            () => "device",
            () => [changedWifi, usb],
            state,
            probe,
            auth,
            settings,
            logger,
            clock,
            preferredPathResolver: preferred);
        probeCalls.Clear();
        Assert(changedCoordinator.GetSleepDelay() <= TimeSpan.FromMilliseconds(100),
            "transport change did not wake the path-aware agent immediately");
        await changedCoordinator.TickAsync();
        Assert(probeCalls.Contains(wifi.AdapterId), "changed transport was not reprobed");
        Assert(state.Find(wifi.Identity)?.LastSuccessfulAuthUtc == now,
            "reprobe after DHCP/ifIndex change erased path authorization history");

        // Past expiry is only a probe trigger. If the bound path still has Internet,
        // the agent must not send a speculative stepOne.
        var expiredAt = now.AddHours(-1);
        state.RecordConfirmedAuthorization(usb, now.AddHours(-25), expiredAt, now.AddHours(-25));
        probes[usb.AdapterId] = NetworkPathProbeStatus.Internet;
        var callsBefore = auth.Paths.Count;
        await changedCoordinator.TickAsync();
        Assert(auth.Paths.Count == callsBefore, "expired timer caused authorization without a captive probe");
        Assert(state.Find(usb.Identity)?.Status == PathAuthorizationStatus.Internet,
            "expired-but-online path was not retained as Internet after probe");

        // A freshly appeared/changed path may be visible before DHCP/ICS/NAT is
        // fully usable. Do not publish the first transient failure as a stable
        // Unreachable state: retry once while the UI remains in Unknown/checking.
        var settling = Path("settling", NetworkInterfaceType.Wireless80211,
            "192.168.137.222", "192.168.137.1", "Settling-Hotspot");
        var settlingCalls = 0;
        var postSettlingStatus = NetworkPathProbeStatus.Internet;
        PathAuthorizationStatus? statusDuringRetry = null;
        var settlingProbe = new NetworkPathProbe((path, _, _) =>
        {
            settlingCalls++;
            if (settlingCalls == 1)
            {
                return Task.FromResult(ProbeResult(NetworkPathProbeStatus.Unreachable));
            }

            statusDuringRetry = state.Find(path.Identity)?.Status;
            return Task.FromResult(ProbeResult(postSettlingStatus));
        });
        var settlingCoordinator = new PathAwareAgentCoordinator(
            () => new StoredSecrets("token", "9123456789"),
            () => "device",
            () => [settling],
            state,
            settlingProbe,
            auth,
            settings,
            logger,
            clock);

        await settlingCoordinator.TickAsync();
        Assert(settlingCalls == 2,
            "new path did not receive a settling retry after its first transient failure");
        Assert(statusDuringRetry == PathAuthorizationStatus.Unknown,
            "first transient failure was published before the settling retry completed");
        Assert(state.Find(settling.Identity)?.Status == PathAuthorizationStatus.Internet,
            "settling retry did not replace the transient failure with Internet truth");

        // Once Internet is known but its authorization boundary is not, there is
        // no useful timer to poll toward. Ordinary agent/background wakes must
        // remain silent; only a Windows connectivity/NCSI change triggers one
        // fresh bound probe for that untimed path.
        var untimedInternet = state.Find(settling.Identity)!;
        Assert(untimedInternet.ExpectedExpiryUtc is null && untimedInternet.LastProbeUtc == now,
            "untimed Internet path setup unexpectedly acquired an expiry");
        Assert(PathAuthorizationSchedule.GetDelay(untimedInternet, settling, settings, now.AddMinutes(1)) ==
               PathAuthorizationSchedule.MaximumWait,
            "unknown-expiry Internet path retained a periodic probe timer");
        Assert(PathAuthorizationSchedule.GetNextCheckUtc(untimedInternet, settling, settings, now.AddMinutes(1)) is null &&
               PathAuthorizationSchedule.IsConnectivityEventDriven(untimedInternet, settling),
            "unknown-expiry Internet path was not marked as connectivity-event driven");

        var callsBeforeIdleTick = settlingCalls;
        await settlingCoordinator.TickAsync();
        Assert(settlingCalls == callsBeforeIdleTick,
            "ordinary wake unexpectedly probed an unknown-expiry Internet path");

        settlingCoordinator.NotifyNetworkConnectivityChanged();
        await settlingCoordinator.TickAsync();
        Assert(settlingCalls == callsBeforeIdleTick + 1,
            "connectivity event did not trigger exactly one probe for an unknown-expiry Internet path");

        postSettlingStatus = NetworkPathProbeStatus.Unreachable;
        settlingCoordinator.NotifyNetworkConnectivityChanged();
        await settlingCoordinator.TickAsync();
        var callsAfterUnreachableEvent = settlingCalls;
        Assert(state.Find(settling.Identity)?.Status == PathAuthorizationStatus.Unreachable,
            "connectivity-triggered unreachable result was not recorded");
        await settlingCoordinator.TickAsync();
        Assert(settlingCalls == callsAfterUnreachableEvent,
            "untimed unreachable path fell back to periodic polling after the connectivity event");

        var knownExpiry = now.AddHours(10);
        state.RecordConfirmedAuthorization(settling, now.AddHours(-14), knownExpiry, now.AddHours(-14));
        var timedInternet = state.Find(settling.Identity)!;
        Assert(PathAuthorizationSchedule.GetNextCheckUtc(timedInternet, settling, settings, now) == knownExpiry.AddSeconds(-30),
            "known authorization boundary did not schedule the same pre-edge wake used by the agent");
        Assert(PathAuthorizationSchedule.GetNextCheckUtc(timedInternet, settling, settings, now, agentRunning: false) is null,
            "stopped agent must not advertise a background check that cannot occur");
    }

    private static async Task ProbeReasonsDoNotChangeSchedulingAsync()
    {
        using var temp = TestDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var json = new JsonFileStore();
        var logger = new DiagnosticLogger(paths);
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableTimeProvider(now);
        var settings = new AppSettings();
        var state = new PathAuthorizationStateStore(paths, json, timeProvider: clock);
        var wifi = Path("logging-wifi", NetworkInterfaceType.Wireless80211, "192.0.2.2", "192.0.2.1", "test");
        state.RecordProbe(wifi, new(wifi.Identity, NetworkPathProbeStatus.Internet, ProbeResult(NetworkPathProbeStatus.Internet)), now);
        var calls = 0;
        var probe = new NetworkPathProbe((_, _, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new InternetProbeResult(false, false, null, "private-response-body", TransportFailureKind.Timeout, TimeSpan.FromSeconds(2))
                : ProbeResult(NetworkPathProbeStatus.Internet));
        }, logger);
        var runner = new FakePathAuthorizationRunner(state, clock, settings);
        var coordinator = new PathAwareAgentCoordinator(() => new("token", "9991234567"), () => "device",
            () => [wifi], state, probe, runner, settings, logger, clock);
        coordinator.NotifyNetworkConnectivityChanged(NetworkChangeReason.WindowsAddress);
        coordinator.NotifyNetworkConnectivityChanged(NetworkChangeReason.Resume);
        await coordinator.TickAsync();
        await coordinator.TickAsync();
        Assert(calls == 1 && runner.Paths.Count == 0, "diagnostic causes added a retry or authorization to an untimed failure");
        Assert(state.Find(wifi.Identity)?.ExpectedExpiryUtc is null && coordinator.GetSleepDelay() == PathAuthorizationSchedule.MaximumWait,
            "diagnostics created an authorization boundary or polling deadline");
        coordinator.NotifyNetworkConnectivityChanged(NetworkChangeReason.WindowsAvailability);
        await coordinator.TickAsync();
        var log = File.ReadAllText(paths.DiagnosticLogFile);
        Assert(calls == 2 && log.Contains("reason=event.WindowsAddress+Resume", StringComparison.Ordinal) &&
               log.Contains("reason=event.WindowsAvailability", StringComparison.Ordinal), "coalesced or subsequent event causes were lost");
        Assert(log.Contains("failure=Timeout", StringComparison.Ordinal) && log.Contains("transport-ms=2000", StringComparison.Ordinal) &&
               log.Contains("budget-ms=2000", StringComparison.Ordinal) && log.Contains("ifIndex=26", StringComparison.Ordinal),
            "probe logs omit transport failure, budget or binding");
        Assert(!log.Contains("private-response-body", StringComparison.Ordinal) && !log.Contains("9991234567", StringComparison.Ordinal),
            "probe diagnostics leaked request secrets or response body");
        Assert(log.Split("network.probe start ").Length - 1 == 2 && log.Split("network.probe result ").Length - 1 == 2,
            "actual probes cannot be counted from matching start/result records");
    }

    private static async Task WindowsSignalsOnlyProbeChangedSelectedPathsAsync()
    {
        using var temp = TestDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var json = new JsonFileStore();
        var logger = new DiagnosticLogger(paths);
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableTimeProvider(now);
        var settings = new AppSettings();
        var state = new PathAuthorizationStateStore(paths, json, timeProvider: clock);
        var wifi = Path("{00000000-0000-0000-0000-000000000001}", NetworkInterfaceType.Wireless80211, "192.0.2.2", "192.0.2.1", "test");
        var usb = Path("usb", NetworkInterfaceType.Ethernet, "192.0.2.4", "192.0.2.3");
        NetworkPathSnapshot[] current = [wifi, usb];
        foreach (var path in current)
            state.RecordProbe(path, new(path.Identity, NetworkPathProbeStatus.Internet, ProbeResult(NetworkPathProbeStatus.Internet)), now);
        IReadOnlyDictionary<string, int>? profiles = new Dictionary<string, int>
        {
            ["00000000-0000-0000-0000-000000000001"] = 3, ["usb"] = 3, ["vpn"] = 3
        };
        var probeCalls = new List<string>();
        var notifyDuringProbe = false;
        var wifiCaptive = false;
        PathAwareAgentCoordinator? coordinator = null;
        var probe = new NetworkPathProbe((path, _, _) =>
        {
            probeCalls.Add(path.AdapterId);
            if (notifyDuringProbe) coordinator!.NotifyNetworkConnectivityChanged(NetworkChangeReason.WindowsAddress);
            return Task.FromResult(ProbeResult(wifiCaptive && path.IsWifi ? NetworkPathProbeStatus.Captive : NetworkPathProbeStatus.Internet));
        }, logger);
        var runner = new FakePathAuthorizationRunner(state, clock, settings);
        coordinator = new PathAwareAgentCoordinator(() => new("token", "9991234567"), () => "device", () => current,
            state, probe, runner, settings, logger, clock, readConnectivityLevels: () => profiles);
        await coordinator.TickAsync(); // Seed the local profile baseline; no HTTP needed.
        var baselineLog = File.Exists(paths.DiagnosticLogFile) ? File.ReadAllText(paths.DiagnosticLogFile) : "";
        for (var i = 0; i < 20; i++)
        {
            profiles = new Dictionary<string, int> { ["00000000-0000-0000-0000-000000000001"] = 3, ["usb"] = 3, ["vpn"] = i % 4 };
            coordinator.NotifyNetworkConnectivityChanged(NetworkChangeReason.WindowsAddress | NetworkChangeReason.Ncsi | NetworkChangeReason.WindowsAvailability);
            await coordinator.TickAsync();
        }
        Assert(probeCalls.Count == 0 && (File.Exists(paths.DiagnosticLogFile) ? File.ReadAllText(paths.DiagnosticLogFile) : "") == baselineLog,
            "unchanged selected paths / changing VPN caused HTTP or ignored-event log spam");
        profiles = new Dictionary<string, int> { ["00000000-0000-0000-0000-000000000001"] = 1, ["usb"] = 3, ["vpn"] = 3 };
        notifyDuringProbe = true;
        coordinator.NotifyNetworkConnectivityChanged(NetworkChangeReason.WindowsAddress);
        await coordinator.TickAsync();
        await coordinator.TickAsync(); // Duplicate arrived while the first request was running.
        notifyDuringProbe = false;
        Assert(probeCalls.SequenceEqual([wifi.AdapterId]), "loss on physical Wi-Fi was masked by VPN or duplicate event probed stable Wi-Fi/USB");
        profiles = new Dictionary<string, int> { ["00000000-0000-0000-0000-000000000001"] = 3, ["usb"] = 3 };
        coordinator.NotifyNetworkConnectivityChanged(NetworkChangeReason.Ncsi);
        await coordinator.TickAsync();
        Assert(probeCalls.Count == 2, "restored physical profile did not trigger one synchronization");
        wifi = wifi with { SourceIPv4 = IPAddress.Parse("192.0.2.9") }; current = [wifi, usb];
        coordinator.NotifyNetworkConnectivityChanged(NetworkChangeReason.WindowsAddress);
        await coordinator.TickAsync();
        Assert(probeCalls.Count == 3 && probeCalls[^1] == wifi.AdapterId, "selected binding change was filtered out");
        current = [usb];
        await coordinator.TickAsync();
        Assert(state.Find(wifi.Identity)?.Status == PathAuthorizationStatus.Disconnected, "removed selected path remained active");
        current = [wifi, usb];
        await coordinator.TickAsync();
        Assert(probeCalls.Count == 4, "returning selected Wi-Fi was not probed");
        profiles = null;
        coordinator.NotifyNetworkConnectivityChanged(NetworkChangeReason.WindowsAddress);
        await coordinator.TickAsync();
        var afterReadFailure = probeCalls.Count;
        coordinator.NotifyNetworkConnectivityChanged(NetworkChangeReason.WindowsAddress);
        await coordinator.TickAsync();
        Assert(probeCalls.Count == afterReadFailure && afterReadFailure == 6, "profile read error caused repeated probes or skipped the first uncertainty transition");
        profiles = new Dictionary<string, int> { ["00000000-0000-0000-0000-000000000001"] = 3, ["usb"] = 3 };
        await coordinator.TickAsync();
        Assert(probeCalls.Count == 8, "recovery from unavailable profile data was ignored");
        coordinator.NotifyNetworkConnectivityChanged(NetworkChangeReason.Resume);
        await coordinator.TickAsync();
        Assert(probeCalls.Count == 10 && runner.Paths.Count == 0, "resume synchronization was filtered or produced authorization on Internet paths");
        var expiry = now.AddHours(1);
        state.RecordConfirmedAuthorization(wifi, now.AddHours(-23), expiry, now);
        profiles = new Dictionary<string, int> { ["00000000-0000-0000-0000-000000000001"] = 1, ["usb"] = 3 };
        coordinator.NotifyNetworkConnectivityChanged(NetworkChangeReason.Ncsi);
        await coordinator.TickAsync();
        Assert(probeCalls.Count == 11 && state.Find(wifi.Identity)?.ExpectedExpiryUtc == expiry,
            "profile loss before a known deadline was ignored or erased its timer");
        profiles = new Dictionary<string, int> { ["00000000-0000-0000-0000-000000000001"] = 3, ["usb"] = 3 };
        await coordinator.TickAsync();
        wifiCaptive = true;
        profiles = new Dictionary<string, int> { ["00000000-0000-0000-0000-000000000001"] = 1, ["usb"] = 3 };
        coordinator.NotifyNetworkConnectivityChanged(NetworkChangeReason.Ncsi);
        await coordinator.TickAsync();
        Assert(probeCalls.Count == 14 && runner.Paths.Count == 1 && runner.Paths[0].AdapterId == wifi.AdapterId,
            "same-IP captive transition did not pass the two-probe gate into authorization");
        var restartRunner = new FakePathAuthorizationRunner(state, clock, settings);
        var restarted = new PathAwareAgentCoordinator(() => new("token", "9991234567"), () => "device", () => current,
            state, probe, restartRunner, settings, logger, clock, readConnectivityLevels: () => profiles);
        await restarted.TickAsync();
        Assert(probeCalls.Count == 16 && restartRunner.Paths.Count == 1,
            "startup with a non-Internet physical profile silently trusted saved Internet state");
    }

    private static NetworkPathSnapshot Path(
        string id,
        NetworkInterfaceType type,
        string source,
        string? gateway,
        string? ssid = null,
        bool hardware = true,
        bool looksVirtual = false) => new(
            id,
            id,
            type,
            IsUp: true,
            InterfaceIndex: id == "usb" ? 8 : 26,
            SourceIPv4: IPAddress.Parse(source),
            GatewayIPv4: gateway is null ? null : IPAddress.Parse(gateway),
            DnsServers: [IPAddress.Parse("192.0.2.53")],
            Ssid: ssid,
            LooksVirtual: looksVirtual,
            HardwareInterface: hardware,
            ConnectorPresent: hardware);

    private static InternetProbeResult ProbeResult(NetworkPathProbeStatus status) => new(
        Online: status == NetworkPathProbeStatus.Internet,
        HttpResponseReceived: status != NetworkPathProbeStatus.Unreachable,
        StatusCode: status switch
        {
            NetworkPathProbeStatus.Internet => HttpStatusCode.Found,
            NetworkPathProbeStatus.Captive => HttpStatusCode.OK,
            NetworkPathProbeStatus.Ambiguous => HttpStatusCode.ServiceUnavailable,
            _ => null
        },
        Body: null,
        FailureKind: status == NetworkPathProbeStatus.Unreachable
            ? TransportFailureKind.DirectRouteUnavailable
            : TransportFailureKind.None,
        Elapsed: TimeSpan.FromMilliseconds(1),
        Location: status == NetworkPathProbeStatus.Internet ? InternetConnectivityProbe.ExpectedLocation : null);

    private sealed class FakePathAuthorizationRunner(
        PathAuthorizationStateStore state,
        TimeProvider clock,
        AppSettings settings) : IPathAuthorizationRunner
    {
        public List<NetworkPathSnapshot> Paths { get; } = [];

        public Task<AuthorizationOutcome> RunAsync(
            NetworkPathSnapshot path,
            AuthorizationRequest request,
            PathAuthorizationExecutionContext? executionContext = null,
            CancellationToken cancellationToken = default)
        {
            Paths.Add(path);
            var now = clock.GetUtcNow();
            state.RecordConfirmedAuthorization(path, now, now.AddHours(settings.AuthWindowHours), now);
            return Task.FromResult(new AuthorizationOutcome(
                AuthorizationOutcomeKind.Success,
                InternetConfirmed: true,
                AuthorizedAtUtc: now,
                RetryAfter: null,
                Timing: null));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
