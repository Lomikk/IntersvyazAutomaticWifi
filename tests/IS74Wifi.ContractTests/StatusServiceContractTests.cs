using IS74Wifi.App;
using IS74Wifi.Core;

internal static class StatusServiceContractTests
{
    public static async Task RunAsync()
    {
        using var temp = TestDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var json = new JsonFileStore();
        var machine = new StatusMachineState(
            Installed: false,
            InstalledVersion: null,
            InstalledExecutablePath: Path.Combine(temp.Path, "Programs", "IS74Wifi", "IS74Wifi.exe"),
            AutomaticAuthorizationEnabled: false,
            AgentRunning: false);
        var service = new StatusService("1.2.3", paths, json, () => machine);

        var fresh = service.ReadLocalSnapshot();
        Assert(!fresh.Registered, "fresh local status must be unregistered");
        Assert(fresh.WifiNetwork == WifiNetworkState.Unknown && fresh.WifiSsid is null,
            "missing Wi-Fi must remain unknown without inventing an SSID");
        Assert(fresh.AuthorizationExpectedExpiryUtc is null && !fresh.AuthorizationAlreadyActive,
            "fresh local status must not invent authorization state");

        new DpapiSecretStore(paths).Save(new StoredSecrets("test-token", "9991234567"));
        var registered = service.ReadLocalSnapshot();
        Assert(registered.Registered, "saved registration must appear in local status");
        Assert(registered.MaskedPhone.EndsWith("67", StringComparison.Ordinal),
            "registered status must preserve masked phone formatting");
        Assert(registered.AuthorizationExpectedExpiryUtc is null && !registered.AuthorizationAlreadyActive,
            "registration alone must not imply active Wi-Fi authorization");

        new RuntimeStateStore(paths, json).Save(new RuntimeState
        {
            LastResult = "already-authorized",
            InternetConfirmed = false
        });
        var alreadyAuthorized = service.ReadLocalSnapshot();
        Assert(!alreadyAuthorized.AuthorizationAlreadyActive,
            "legacy global already-authorized state must not be promoted to an active physical path");

        var agentSnapshot = new AgentNetworkSnapshot(
            CapturedAtUtc: DateTimeOffset.UtcNow,
            InternetAvailable: true,
            VpnActive: true,
            WifiSsid: "Campus Wi-Fi test",
            Paths:
            [
                new AgentNetworkPathSnapshot(
                    "Wi-Fi · Campus Wi-Fi test", "Campus Wi-Fi test", 7, "10.0.0.2", "10.0.0.1",
                    PathAuthorizationStatus.Internet, true, DateTimeOffset.UtcNow.AddHours(2), "success",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), false, DateTimeOffset.UtcNow, false, false)
            ]);
        new AgentNetworkSnapshotStore(paths, json).Save(agentSnapshot);
        var agentOwned = service.ReadLocalSnapshot();
        Assert(agentOwned.InternetAvailable == true && agentOwned.ActivePhysicalPathCount == 1 && agentOwned.VpnActive,
            "local UI status must be built from the last agent-owned network snapshot");
        Assert(agentOwned.WifiSsid == "Campus Wi-Fi test" && agentOwned.NetworkPaths?.Single().Status == PathAuthorizationStatus.Internet,
            "agent-owned SSID/path status must flow into the UI snapshot");

        var settingsStore = new SettingsStore(paths, json);
        settingsStore.Save(settingsStore.Load() with
        {
            IgnoreNetworkCheck = true,
            DirectNetworkAdapterId = PhysicalAdapterSelection.SystemRoute,
            AnonymousStatisticsConsent = AnonymousStatisticsConsent.Declined,
            AutomaticUpdates = true,
            IncludePrereleaseUpdates = false,
            NotificationMode = NotificationMode.Off,
            ShowTrayIcon = false
        });
        var changedSettings = service.ReadLocalSnapshot();
        Assert(!changedSettings.NetworkCheckIgnored && changedSettings.DirectNetworkMode == "системный",
            "legacy network-check setting must no longer affect the path-aware UI snapshot");
        Assert(changedSettings.AnonymousStatisticsConsent == AnonymousStatisticsConsent.Declined &&
               changedSettings.AutomaticUpdates && !changedSettings.IncludePrereleaseUpdates &&
               changedSettings.NotificationMode == "выкл" && !changedSettings.ShowTrayIcon,
            "changed UI settings must be reflected by the next local snapshot");

        var updateStore = new UpdateStateStore(paths, json);
        updateStore.Save(new UpdateState
        {
            AvailableVersion = "1.2.3",
            AvailableReleasePageUrl = "https://example.test/release",
            LastNotifiedVersion = "1.2.3"
        });
        var normalizedUpdate = service.ReadLocalSnapshot();
        Assert(normalizedUpdate.AvailableUpdateVersion is null,
            "status must preserve UpdateMaintenanceService stale-version normalization");
        Assert(updateStore.Load().AvailableVersion is null && updateStore.Load().AvailableReleasePageUrl is null,
            "stale update normalization must still be persisted locally");

        var refreshCalls = 0;
        var updateCalls = 0;
        var refreshedSnapshot = agentSnapshot with
        {
            InternetAvailable = false,
            Paths =
            [
                agentSnapshot.Paths[0] with { Status = PathAuthorizationStatus.Captive }
            ]
        };
        var refreshed = await service.RefreshNetworkSnapshotAsync(
            _ =>
            {
                refreshCalls++;
                return Task.FromResult(refreshedSnapshot);
            },
            _ =>
            {
                updateCalls++;
                return Task.CompletedTask;
            });
        Assert(refreshCalls == 1 && updateCalls == 1,
            "network refresh must make its network/update side effects explicit and single-shot");
        Assert(refreshed.InternetAvailable == false && refreshed.CaptivePathCount == 1,
            "agent refresh snapshot must flow into the returned UI status");

        var reportRefreshCalls = 0;
        var report = await service.BuildDetailedReportAsync(_ =>
        {
            reportRefreshCalls++;
            return Task.FromResult(agentSnapshot);
        });
        Assert(reportRefreshCalls == 1, "detailed report must use only the supplied agent network refresh");
        Assert(report.Any(line => line == $"Данные приложения: {paths.Root}"),
            "detailed report must use the supplied temporary AppPaths");
        Assert(report.Any(line => line == "Интернет: доступен"),
            "detailed report must include the supplied Internet result");
        Assert(report.Any(line => line == "Регистрация: сохранена"),
            "detailed report must read registration from the supplied AppPaths");
        Assert(report.Any(line => line == "Значок в трее: скрывать"),
            "detailed report must expose tray visibility");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
