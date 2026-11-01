using System.Net;
using System.Net.NetworkInformation;
using IS74Wifi.Core;

internal static class PathAuthorizationStateContractTests
{
    public static Task RunAsync()
    {
        using var temp = TempDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var json = new JsonFileStore();
        var now = new DateTimeOffset(2026, 10, 6, 0, 30, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        var legacyStore = new RuntimeStateStore(paths, json);
        legacyStore.Save(new RuntimeState
        {
            LastAuthUtc = now.AddHours(-2),
            ExpectedExpiryUtc = now.AddHours(22),
            LastResult = "success",
            InternetConfirmed = true
        });

        var store = new PathAuthorizationStateStore(paths, json, legacyStore, clock);
        var migrated = store.Load();
        Assert(migrated.SchemaVersion == PathAuthorizationStateDocument.CurrentSchemaVersion,
            "path state schema version was not initialized");
        Assert(migrated.Paths.Length == 0,
            "legacy global authorization must never be assigned to an arbitrary path");
        Assert(migrated.LegacyGlobalHint is
            {
                LastResult: "success",
                InternetConfirmed: true,
                LastAuthUtc: not null,
                ExpectedExpiryUtc: not null
            }, "legacy global authorization was not retained as a diagnostic hint");
        Assert(File.Exists(paths.PathAuthorizationStateFile),
            "first migration did not persist path authorization state");

        var wifi = Path(
            adapterId: "wifi-guid",
            type: NetworkInterfaceType.Wireless80211,
            source: "192.168.137.221",
            gateway: "192.168.137.1",
            ssid: "Campus Wi-Fi",
            name: "Wi-Fi");
        var usb = Path(
            adapterId: "usb-guid",
            type: NetworkInterfaceType.Ethernet,
            source: "10.253.184.3",
            gateway: "10.253.184.224",
            name: "USB Android");

        var wifiObserved = store.Observe(wifi, now);
        var usbObserved = store.Observe(usb, now.AddSeconds(1));
        Assert(wifiObserved.Status == PathAuthorizationStatus.Unknown &&
               usbObserved.Status == PathAuthorizationStatus.Unknown,
            "new physical paths must start unknown until a bound probe runs");
        Assert(store.Load().Paths.Length == 2,
            "independent physical paths did not persist independently");

        var wifiProbeAt = now.AddSeconds(2);
        store.RecordProbe(wifi, Probe(wifi, NetworkPathProbeStatus.Captive), wifiProbeAt);
        store.RecordProbe(usb, Probe(usb, NetworkPathProbeStatus.Internet), wifiProbeAt);
        Assert(store.Find(wifi.Identity)?.Status == PathAuthorizationStatus.Captive,
            "Wi-Fi captive probe contaminated or failed to update its path state");
        Assert(store.Find(usb.Identity)?.Status == PathAuthorizationStatus.Internet,
            "USB Internet probe contaminated or failed to update its path state");

        var authorizedAt = now.AddMinutes(1);
        var expiry = authorizedAt.AddHours(24);
        store.RecordConfirmedAuthorization(wifi, authorizedAt, expiry, authorizedAt.AddSeconds(1));
        var authorizedWifi = store.Find(wifi.Identity);
        Assert(authorizedWifi?.LastSuccessfulAuthUtc == authorizedAt &&
               authorizedWifi.ExpectedExpiryUtc == expiry &&
               authorizedWifi.Status == PathAuthorizationStatus.Internet,
            "confirmed per-path authorization did not persist its timer");
        Assert(store.Find(usb.Identity)?.ExpectedExpiryUtc is null,
            "authorizing Wi-Fi incorrectly created a timer for USB");

        var renewedWifi = wifi with
        {
            InterfaceIndex = 77,
            SourceIPv4 = IPAddress.Parse("192.168.137.250")
        };
        Assert(renewedWifi.Identity == wifi.Identity,
            "DHCP/ifIndex test setup unexpectedly changed the path identity");
        store.Observe(renewedWifi, now.AddMinutes(2));
        var afterDhcp = store.Find(wifi.Identity);
        Assert(afterDhcp?.SourceIPv4 == "192.168.137.250" &&
               afterDhcp.ExpectedExpiryUtc == expiry,
            "DHCP/source-IP refresh lost durable authorization history");

        var switchedWifi = renewedWifi with { Ssid = "Laptop-Hotspot" };
        Assert(switchedWifi.Identity != wifi.Identity,
            "SSID switch test setup unexpectedly preserved the path identity");
        store.Observe(switchedWifi, now.AddMinutes(3));
        Assert(store.Load().Paths.Length == 3,
            "network switch on one Wi-Fi adapter reused another network's state");
        Assert(store.Find(switchedWifi.Identity)?.ExpectedExpiryUtc is null,
            "new Wi-Fi network inherited the previous SSID timer");

        store.MarkMissingAsDisconnected([usb.Identity, switchedWifi.Identity]);
        Assert(store.Find(wifi.Identity)?.Status == PathAuthorizationStatus.Disconnected,
            "missing path history was not retained as disconnected");
        Assert(store.Find(wifi.Identity)?.ExpectedExpiryUtc == expiry,
            "disconnecting a path erased its authorization history");
        store.Observe(wifi, now.AddMinutes(4));
        Assert(store.Find(wifi.Identity)?.Status == PathAuthorizationStatus.Unknown,
            "reappeared path must return to unknown until it is probed again");
        Assert(store.Find(wifi.Identity)?.ExpectedExpiryUtc == expiry,
            "reappeared path lost its previous timer before validation");

        store.RecordProbe(wifi, Probe(wifi, NetworkPathProbeStatus.Internet), expiry.AddMinutes(5));
        var stale = store.Find(wifi.Identity);
        Assert(stale?.Status == PathAuthorizationStatus.Internet && stale.ExpectedExpiryUtc == expiry,
            "post-expiry Internet probe must not silently manufacture a new 24-hour timer");
        Assert(PathAuthorizationSchedule.IsConnectivityEventDriven(stale!, wifi),
            "observed Internet beyond the three-minute window must wait for a connectivity event");
        var rebound = store.Observe(renewedWifi, expiry.AddMinutes(6));
        Assert(rebound.Status == PathAuthorizationStatus.Unknown && rebound.LastProbeUtc is null &&
               !PathAuthorizationSchedule.IsConnectivityEventDriven(rebound, renewedWifi),
            "changed transport binding reused an old Internet observation to sleep indefinitely");
        store.RecordProbe(wifi, Probe(wifi, NetworkPathProbeStatus.Internet), expiry.AddMinutes(7));

        var wrongProbe = Probe(usb, NetworkPathProbeStatus.Internet);
        try
        {
            store.RecordProbe(wifi, wrongProbe, now);
            throw new InvalidOperationException("probe result from a different path was accepted");
        }
        catch (ArgumentException)
        {
        }

        var vpn = Path(
            adapterId: "tap-guid",
            type: NetworkInterfaceType.Ethernet,
            source: "10.72.2.153",
            gateway: null,
            looksVirtual: true,
            name: "TAP-Windows Adapter V9");
        try
        {
            store.Observe(vpn, now);
            throw new InvalidOperationException("VPN received PathAuthorizationState");
        }
        catch (DirectNetworkUnavailableException)
        {
        }

        var pathRuntime = new PathRuntimeStateStore(store, wifi);
        pathRuntime.Save(new RuntimeState
        {
            LastAuthUtc = authorizedAt,
            ExpectedExpiryUtc = expiry,
            LastAttemptUtc = now.AddMinutes(5),
            LastAttemptReason = "retry",
            LastResult = "step-one-retryable-error",
            InternetConfirmed = false,
            AutomaticStepOneAttempts = 2,
            PreStepFailureCount = 1,
            NextAutomaticRetryUtc = now.AddMinutes(6),
            UserActionRequired = false,
            EdgeWatchActive = true
        });
        var restoredRuntime = pathRuntime.Load();
        Assert(restoredRuntime.AutomaticStepOneAttempts == 2 &&
               restoredRuntime.PreStepFailureCount == 1 &&
               restoredRuntime.EdgeWatchActive &&
               restoredRuntime.NextAutomaticRetryUtc == now.AddMinutes(6),
            "per-path authorization cycle state did not round-trip through RuntimeState adapter");
        Assert(store.Find(usb.Identity)?.AutomaticStepOneAttempts == 0,
            "per-path authorization attempt budget leaked into another path");

        // A corrupt primary with no verified backup must never be silently
        // interpreted as absent state or overwritten on the next mutation.
        using var corruptTemp = TempDirectory.Create();
        var corruptPaths = new AppPaths(corruptTemp.Path);
        corruptPaths.EnsureDirectories();
        File.WriteAllText(corruptPaths.PathAuthorizationStateFile, "{not-json");
        var corruptStore = new PathAuthorizationStateStore(
            corruptPaths, new JsonFileStore(), timeProvider: clock);
        AssertThrows<InvalidDataException>(() => corruptStore.Load(),
            "corrupt primary without backup returned empty state");
        AssertThrows<InvalidDataException>(() => corruptStore.Observe(usb, now),
            "corrupt primary was overwritten by a network observation");
        Assert(File.ReadAllText(corruptPaths.PathAuthorizationStateFile) == "{not-json",
            "corrupt primary was modified instead of preserved for diagnosis");

        // The backup is the prior committed generation, not the old global
        // runtime hint. Restore a matching path and expiry, never invent one.
        using var backupTemp = TempDirectory.Create();
        var backupPaths = new AppPaths(backupTemp.Path);
        var backupStore = new PathAuthorizationStateStore(
            backupPaths, new JsonFileStore(), timeProvider: clock);
        backupStore.RecordConfirmedAuthorization(usb, authorizedAt, expiry, authorizedAt);
        backupStore.RecordProbe(usb, Probe(usb, NetworkPathProbeStatus.Internet), authorizedAt.AddSeconds(2));
        var backupPath = backupPaths.PathAuthorizationStateFile + ".bak";
        Assert(File.Exists(backupPath), "rotating previous-generation backup was not created");
        var preservedBackup = File.ReadAllText(backupPath);
        File.WriteAllText(backupPaths.PathAuthorizationStateFile, "{broken-primary");
        var fallback = backupStore.Load();
        Assert(fallback.Paths.Single().Identity == usb.Identity &&
               fallback.Paths.Single().ExpectedExpiryUtc == expiry &&
               File.ReadAllText(backupPath) == preservedBackup &&
               File.ReadAllText(backupPaths.PathAuthorizationStateFile) != "{broken-primary",
            "backup recovery did not preserve matching path expiry and backup");
        backupStore.RecordProbe(usb, Probe(usb, NetworkPathProbeStatus.Internet), authorizedAt.AddSeconds(3));
        Assert(backupStore.Find(usb.Identity)?.ExpectedExpiryUtc == expiry,
            "normal writes after recovery lost the expected expiry");

        // Crash after removal, before replacement: the remaining backup is
        // still authoritative and must not trigger a legacy-global migration.
        File.Delete(backupPaths.PathAuthorizationStateFile);
        var restoredAfterMissing = backupStore.Load();
        Assert(restoredAfterMissing.Paths.Single().ExpectedExpiryUtc == expiry &&
               File.Exists(backupPaths.PathAuthorizationStateFile),
            "missing primary did not recover from a verified backup");

        // Transient sharing violations are not corruption: never overwrite
        // the current primary or rotate its backup when a read fails.
        var bytesBeforeLock = File.ReadAllBytes(backupPaths.PathAuthorizationStateFile);
        using (var lockedFile = new FileStream(backupPaths.PathAuthorizationStateFile,
                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            AssertThrows<IOException>(() => backupStore.Observe(usb, now),
                "temporary file sharing violation was interpreted as empty state");
        }
        Assert(File.ReadAllBytes(backupPaths.PathAuthorizationStateFile).SequenceEqual(bytesBeforeLock) &&
               backupStore.Find(usb.Identity)?.ExpectedExpiryUtc == expiry,
            "temporary read failure corrupted durable state");

        // An unreadable primary AND backup means fail closed, retaining both
        // artifacts for manual diagnosis rather than erasing evidence.
        File.WriteAllText(backupPaths.PathAuthorizationStateFile, "{primary");
        File.WriteAllText(backupPath, "{backup");
        AssertThrows<InvalidDataException>(() => backupStore.Load(),
            "double-corruption silently reset path authorization state");
        Assert(File.ReadAllText(backupPaths.PathAuthorizationStateFile) == "{primary" &&
               File.ReadAllText(backupPath) == "{backup",
            "double-corruption overwrote the evidence");

        // Competing store instances must not lose unrelated path observations.
        using var concurrentTemp = TempDirectory.Create();
        var concurrentPaths = new AppPaths(concurrentTemp.Path);
        var firstStore = new PathAuthorizationStateStore(
            concurrentPaths, new JsonFileStore(), timeProvider: clock);
        firstStore.RecordConfirmedAuthorization(usb, authorizedAt, expiry, authorizedAt);
        Parallel.For(0, 12, i =>
        {
            var path = Path("parallel-" + i, NetworkInterfaceType.Ethernet,
                "10.23.0.3", "10.23.0.1");
            var anotherStore = new PathAuthorizationStateStore(
                concurrentPaths, new JsonFileStore(), timeProvider: clock);
            anotherStore.Observe(path, now);
        });
        Assert(firstStore.Load().Paths.Length == 13 &&
               firstStore.Find(usb.Identity)?.ExpectedExpiryUtc == expiry,
            "concurrent per-path updates lost an existing authorization expiry");

        // Registration reset must also remove the fallback, or a later startup
        // would resurrect an intentionally erased authorization history.
        var oldStateBackup = concurrentPaths.PathAuthorizationStateFile + ".bak";
        Assert(File.Exists(oldStateBackup), "concurrency test did not create a backup");
        new LocalStateMaintenance(concurrentPaths).ResetRegistration();
        Assert(!File.Exists(concurrentPaths.PathAuthorizationStateFile) &&
               !File.Exists(oldStateBackup),
            "registration reset left stale per-path authorization history");

        using var oldSchemaTemp = TempDirectory.Create();
        var oldSchemaPaths = new AppPaths(oldSchemaTemp.Path);
        oldSchemaPaths.EnsureDirectories();
        File.WriteAllText(oldSchemaPaths.PathAuthorizationStateFile,
            "{\"schemaVersion\":0,\"legacyGlobalHint\":null,\"paths\":[]}");
        var oldSchemaStore = new PathAuthorizationStateStore(
            oldSchemaPaths,
            new JsonFileStore(),
            new RuntimeStateStore(oldSchemaPaths, new JsonFileStore()),
            clock);
        Assert(oldSchemaStore.Load().SchemaVersion == PathAuthorizationStateDocument.CurrentSchemaVersion,
            "older path-state schema was not normalized to the current version");

        var reloaded = new PathAuthorizationStateStore(paths, new JsonFileStore(), legacyStore, clock).Load();
        Assert(reloaded.Paths.Length == 3 && reloaded.LegacyGlobalHint?.LastResult == "success",
            "per-path state or migration hint did not survive store recreation");

        return Task.CompletedTask;
    }

    private static NetworkPathProbeResult Probe(NetworkPathSnapshot path, NetworkPathProbeStatus status) =>
        new(
            path.Identity,
            status,
            new InternetProbeResult(
                Online: status == NetworkPathProbeStatus.Internet,
                HttpResponseReceived: status is NetworkPathProbeStatus.Internet or NetworkPathProbeStatus.Captive or NetworkPathProbeStatus.Ambiguous,
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
                Elapsed: TimeSpan.FromMilliseconds(1)));

    private static NetworkPathSnapshot Path(
        string adapterId,
        NetworkInterfaceType type,
        string? source,
        string? gateway,
        string? ssid = null,
        bool looksVirtual = false,
        string? name = null) => new(
            adapterId,
            name ?? adapterId,
            type,
            IsUp: true,
            InterfaceIndex: 11,
            SourceIPv4: source is null ? null : IPAddress.Parse(source),
            GatewayIPv4: gateway is null ? null : IPAddress.Parse(gateway),
            DnsServers: [IPAddress.Parse("192.0.2.53")],
            Ssid: ssid,
            LooksVirtual: looksVirtual);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
