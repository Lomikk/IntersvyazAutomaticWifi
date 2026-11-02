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
        var wifiReads = 0;
        var service = new StatusService("1.2.3", paths, json, () => machine,
            () => { wifiReads++; return WifiDiagnosticsContractTests.Fixture(); });

        var fresh = service.ReadLocalSnapshot();
        Assert(!fresh.Registered, "fresh local status must be unregistered");
        Assert(fresh.WifiNetwork == WifiNetworkState.Unknown && fresh.WifiSsid is null,
            "missing Wi-Fi must remain unknown without inventing an SSID");
        Assert(fresh.AuthorizationExpectedExpiryUtc is null && !fresh.AuthorizationAlreadyActive,
            "fresh local status must not invent authorization state");
        Assert(fresh.AuthorizationConnectionsSummary == "Все подключения до выбора сетей", "fresh UI must expose pending setup");
        var initialStore = new SettingsStore(paths, json);
        initialStore.Save(initialStore.Load() with { InitialSetupCompleted = true });

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
        var usbSnapshot = agentSnapshot with
        {
            WifiSsid = null,
            Paths = [agentSnapshot.Paths[0] with { Ssid = null, Name = "USB" }]
        };
        var ignoredUsb = service.ReadSnapshot(usbSnapshot);
        Assert(ignoredUsb.ActivePhysicalPathCount == 0 && ignoredUsb.InternetAvailable == false && ignoredUsb.AuthorizationExpectedExpiryUtc is null,
            "Campus-only local status must not reuse an old USB Internet/expiry snapshot");
        initialStore.Save(initialStore.Load() with { InitialSetupCompleted = false });
        Assert(service.ReadSnapshot(usbSnapshot).ActivePhysicalPathCount == 1,
            "pending setup must not hide the only USB connection");
        initialStore.Save(initialStore.Load() with { InitialSetupCompleted = true });
        var connectionStore = new SettingsStore(paths, json);
        connectionStore.Save(connectionStore.Load() with
        {
            AuthorizationConnections = new() { WifiSsids = [], Adapters = [new("usb", "My phone")] }
        });
        var selectedUsb = service.ReadSnapshot(usbSnapshot with { Paths = [usbSnapshot.Paths[0] with { AdapterId = "usb" }] });
        Assert(selectedUsb.ActivePhysicalPathCount == 1 && selectedUsb.InternetAvailable == true,
            "selected USB snapshot was hidden");
        var unselectedWifi = service.ReadSnapshot(agentSnapshot);
        Assert(unselectedWifi.ActivePhysicalPathCount == 0 && unselectedWifi.InternetAvailable == false,
            "deselected Campus snapshot retained Internet or expiry");
        Assert(service.ReadSnapshot(usbSnapshot).ActivePhysicalPathCount == 0,
            "legacy USB snapshot without an adapter ID inherited permission");
        connectionStore.Save(connectionStore.Load() with { AuthorizationConnections = new() });
        machine = machine with { NetworkOperationRunning = true };
        var running = service.ReadLocalSnapshot();
        Assert(running.NetworkPaths?.Single().NetworkOperationRunning == true,
            "local status failed to expose the network operation owner while the persisted snapshot was unchanged");
        Assert(new AgentNetworkSnapshotStore(paths, json).Load()?.CapturedAtUtc == agentSnapshot.CapturedAtUtc,
            "reading operation progress must not fabricate a fresh persisted network snapshot");
        machine = machine with { NetworkOperationRunning = false };

        var edgeWatchSnapshot = agentSnapshot with
        {
            Paths =
            [
                agentSnapshot.Paths[0] with
                {
                    LastResult = "already-authorized",
                    EdgeWatchActive = true,
                    AuthorizationWindowApproximate = true
                }
            ]
        };
        new AgentNetworkSnapshotStore(paths, json).Save(edgeWatchSnapshot);
        var edgeWatchOwned = service.ReadLocalSnapshot();
        Assert(edgeWatchOwned.NetworkPaths?.Single().EdgeWatchActive == true,
            "agent edge-watch state must flow into the UI snapshot");

        var settingsStore = new SettingsStore(paths, json);
        settingsStore.Save(settingsStore.Load() with
        {
            NetworkMode = NetworkMode.Advanced,
            IgnoreNetworkCheck = true,
            DirectNetworkAdapterId = PhysicalAdapterSelection.SystemRoute,
            AnonymousStatisticsConsent = AnonymousStatisticsConsent.Declined,
            AutomaticUpdates = true,
            IncludePrereleaseUpdates = false,
            NotificationMode = NotificationMode.Off,
            ShowTrayIcon = false
        });
        var changedSettings = service.ReadLocalSnapshot();
        Assert(!changedSettings.NetworkCheckIgnored && changedSettings.DirectNetworkMode == "выбранные подключения · IP-first",
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
        Assert(wifiReads == 0, "ordinary local/menu status must not request radio diagnostics");
        var lastProbe = DateTimeOffset.UtcNow.AddMinutes(-3);
        edgeWatchSnapshot = edgeWatchSnapshot with { Paths = [edgeWatchSnapshot.Paths[0] with { LastProbeUtc = lastProbe }] };
        var report = await service.BuildDetailedReportAsync(_ =>
        {
            reportRefreshCalls++;
            return Task.FromResult(edgeWatchSnapshot);
        });
        Assert(reportRefreshCalls == 1, "detailed report must use only the supplied agent network refresh");
        Assert(wifiReads == 1, "report must read Wi-Fi exactly once, without periodic or extra bound probes");
        Assert(report.Any(line => line == $"Данные приложения: {paths.Root}"),
            "detailed report must use the supplied temporary AppPaths");
        Assert(report.Any(line => line == "Интернет: доступен"),
            "detailed report must include the supplied Internet result");
        Assert(report.Any(line => line == "Регистрация: сохранена"),
            "detailed report must read registration from the supplied AppPaths");
        Assert(report.Any(line => line == "Значок в трее: скрывать"),
            "detailed report must expose tray visibility");
        Assert(report.Any(line => line.Contains("Режим: наблюдение фактической границы", StringComparison.Ordinal)),
            "detailed report must explain agent edge-watch instead of leaving an expired boundary unexplained");
        Assert(report.Any(line => line.Contains("Ожидаемая граница:", StringComparison.Ordinal) &&
                                  line.Contains("(приблизительно)", StringComparison.Ordinal)),
            "detailed report must label approximate authorization boundaries");
        foreach (var section in new[] { "Программа", "Состояние", "Обновления", "Телеметрия", "Сетевые пути", "Пути файлов",
            "Wi-Fi: текущее подключение", "Доступные беспроводные сети", "Дополнительная диагностика сетевых путей" })
            Assert(report.Count(line => line == $"=== {section} ===") == 1, "report lost or duplicated a section: " + section);
        Assert(report.Any(line => line.Contains($"Последняя bound-проверка: {lastProbe.ToLocalTime():dd.MM.yyyy HH:mm:ss}", StringComparison.Ordinal)),
            "report omitted the real previous probe timestamp");
        Assert(report.Any(line => line.Contains("VPN: обнаружен", StringComparison.Ordinal)) &&
               report.Any(line => line.Contains("Последний результат:", StringComparison.Ordinal)) &&
               report.Any(line => line.Contains("Следующая проверка:", StringComparison.Ordinal)), "path details from the old screen were lost");
        Assert(report.Any(line => line.Contains("02:00:00:00:00:00", StringComparison.Ordinal)) &&
               report.All(line => !line.Contains("9991234567", StringComparison.Ordinal) && !line.Contains("test-token", StringComparison.Ordinal)),
            "radio data absent or registration secrets leaked into the report");
        var degraded = new StatusService("1.2.3", paths, json, () => machine,
            () => throw new InvalidOperationException("private Wi-Fi error"));
        var degradedReport = await degraded.BuildDetailedReportAsync(_ => throw new IOException("private network error"));
        Assert(degradedReport.Any(line => line.StartsWith("=== Пути файлов", StringComparison.Ordinal)) &&
               degradedReport.Any(line => line.Contains("Показан сохранённый снимок", StringComparison.Ordinal)) &&
               degradedReport.Any(line => line.Contains("Данные Wi-Fi недоступны", StringComparison.Ordinal)) &&
               degradedReport.All(line => !line.Contains("private", StringComparison.Ordinal)), "one failing data source broke or leaked into the rest of the report");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
