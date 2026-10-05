using System.Net;
using System.Net.NetworkInformation;
using IS74Wifi.Core;

internal static class PathAwareAgentContractTests
{
    public static async Task RunAsync()
    {
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
        var probe = new NetworkPathProbe((path, _, _) =>
        {
            probeCalls.Add(path.AdapterId);
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
        Assert(!probeCalls.Contains(vpn.AdapterId), "VPN path reached the automatic probe");
        Assert(auth.Paths.Count == 1, "more than one SMS authorization flow ran in one tick");
        var first = auth.Paths[0];
        Assert(first.AdapterId == wifi.AdapterId,
            "the Windows-preferred Internet path must win simultaneous captive arbitration");
        Assert(state.Find(first.Identity)?.ExpectedExpiryUtc == now.AddHours(settings.AuthWindowHours),
            "successful path authorization did not create its own expiry");

        probeCalls.Clear();
        await coordinator.TickAsync();
        Assert(probeCalls.SequenceEqual([usb.AdapterId, usb.AdapterId]),
            "backup captive path must be confirmed twice before starting another SMS flow");
        Assert(auth.Paths.Count == 2 && auth.Paths[1].AdapterId == usb.AdapterId,
            "second captive path was not serviced after the preferred path completed");
        Assert(state.Find(usb.Identity)?.ExpectedExpiryUtc == now.AddHours(settings.AuthWindowHours),
            "second path did not receive an independent expiry");

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
