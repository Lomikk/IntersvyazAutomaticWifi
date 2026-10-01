namespace IS74Wifi.Core;

public sealed record CampusSpeedTestRun(
    SpeedTestMeasurement Measurement,
    SpeedTestRadioSnapshot Radio,
    TelemetrySpeedTestEvent Telemetry,
    TelemetryWriteResult StatisticsWrite,
    bool StatisticsQueued);

public sealed record CampusLeaderboardPublishResult(
    TelemetryWriteResult Write);

public sealed class CampusSpeedToolsService(
    ISpeedTestProvider speedTestProvider,
    TelemetryClient? telemetryClient,
    TelemetryQueue telemetryQueue,
    string installId,
    string appVersion,
    int interactiveBackendTimeoutMilliseconds,
    Func<bool>? anonymousStatisticsAllowed = null)
{
    // Older settings files may persist zero or the former 15-second value. Keep
    // a 30-second floor so upgrades also tolerate Apps Script cold starts.
    private readonly TimeSpan interactiveBackendTimeout = TimeSpan.FromMilliseconds(
        Math.Clamp(interactiveBackendTimeoutMilliseconds, 30000, 60000));
    private readonly Func<bool> statisticsAllowed = anonymousStatisticsAllowed ?? (() => true);

    public bool BackendEnabled => telemetryClient is not null;
    public bool AnonymousStatisticsAllowed => statisticsAllowed();

    public async Task<CampusSpeedTestRun> MeasureAsync(
        IProgress<SpeedTestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var measurement = await speedTestProvider.MeasureAsync(progress, cancellationToken).ConfigureAwait(false);
        var radio = WindowsWifiService.GetSpeedTestRadioSnapshot();
        var telemetry = SpeedTestTelemetry.CreateEvent(measurement, installId, appVersion, radio);

        if (!statisticsAllowed())
        {
            return new CampusSpeedTestRun(
                measurement,
                radio,
                telemetry,
                new TelemetryWriteResult(false, false, "statistics_disabled"),
                StatisticsQueued: false);
        }

        if (telemetryClient is null)
        {
            QueueSpeedTest(telemetry);
            return new CampusSpeedTestRun(
                measurement,
                radio,
                telemetry,
                new TelemetryWriteResult(false, false, "backend_not_configured"),
                StatisticsQueued: true);
        }

        var write = await telemetryClient.SubmitSpeedTestAsync(
            telemetry,
            interactiveBackendTimeout,
            cancellationToken).ConfigureAwait(false);

        var queued = !write.Success && ShouldRetryLater(write.Error);
        if (queued)
        {
            QueueSpeedTest(telemetry);
        }

        return new CampusSpeedTestRun(measurement, radio, telemetry, write, queued);
    }

    public async Task<CampusLeaderboardPublishResult> PublishAsync(
        CampusSpeedTestRun run,
        string nickname,
        CancellationToken cancellationToken = default)
    {
        var normalized = LeaderboardNicknamePreferences.Normalize(nickname);
        if (normalized is null)
        {
            return new CampusLeaderboardPublishResult(
                new TelemetryWriteResult(false, false, "invalid_nickname"));
        }

        if (telemetryClient is null)
        {
            return new CampusLeaderboardPublishResult(
                new TelemetryWriteResult(false, false, "backend_not_configured"));
        }

        var speed = run.Telemetry;
        var entry = new TelemetryLeaderboardEntry
        {
            EntryId = "leader-" + Guid.NewGuid().ToString("N"),
            InstallId = installId,
            TestId = speed.TestId,
            AppVersion = appVersion,
            EventId = "event-" + Guid.NewGuid().ToString("N"),
            Nickname = normalized,
            DownloadMbps = speed.DownloadMbps,
            UploadMbps = speed.UploadMbps,
            LatencyMs = speed.LatencyMs,
            JitterMs = speed.JitterMs,
            PacketLossPct = speed.PacketLossPct,
            WifiSignalBucket = speed.WifiSignalBucket,
            WifiBand = speed.WifiBand,
            TimeBucket = speed.TimeBucket
        };

        var write = await telemetryClient.PublishLeaderboardAsync(
            entry,
            interactiveBackendTimeout,
            cancellationToken).ConfigureAwait(false);

        // Leaderboard changes are user actions and are never deferred through the
        // telemetry queue: a delayed publish must not resurrect a position after leave.
        return new CampusLeaderboardPublishResult(write);
    }

    public Task<LeaderboardReadResult> GetLeaderboardAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (telemetryClient is null)
        {
            return Task.FromResult(new LeaderboardReadResult(
                false,
                "backend_not_configured",
                []));
        }

        return telemetryClient.GetLeaderboardAsync(limit, interactiveBackendTimeout, cancellationToken);
    }

    public Task<LeaderboardControlResult> GetParticipationStateAsync(
        CancellationToken cancellationToken = default) =>
        telemetryClient is null
            ? Task.FromResult(new LeaderboardControlResult(false, "backend_not_configured", null))
            : telemetryClient.GetLeaderboardStatusAsync(installId, interactiveBackendTimeout, cancellationToken);

    public Task<LeaderboardControlResult> RenameAsync(
        string nickname,
        CancellationToken cancellationToken = default)
    {
        var normalized = LeaderboardNicknamePreferences.Normalize(nickname);
        if (normalized is null)
        {
            return Task.FromResult(new LeaderboardControlResult(false, "invalid_nickname", null));
        }

        return telemetryClient is null
            ? Task.FromResult(new LeaderboardControlResult(false, "backend_not_configured", null))
            : telemetryClient.RenameLeaderboardAsync(
                installId, normalized, interactiveBackendTimeout, cancellationToken);
    }

    public Task<LeaderboardControlResult> LeaveAsync(CancellationToken cancellationToken = default) =>
        telemetryClient is null
            ? Task.FromResult(new LeaderboardControlResult(false, "backend_not_configured", null))
            : telemetryClient.LeaveLeaderboardAsync(installId, interactiveBackendTimeout, cancellationToken);

    public Task<LeaderboardControlResult> RejoinAsync(
        string nickname,
        CancellationToken cancellationToken = default)
    {
        var normalized = LeaderboardNicknamePreferences.Normalize(nickname);
        if (normalized is null)
        {
            return Task.FromResult(new LeaderboardControlResult(false, "invalid_nickname", null));
        }

        return telemetryClient is null
            ? Task.FromResult(new LeaderboardControlResult(false, "backend_not_configured", null))
            : telemetryClient.JoinLeaderboardAsync(
                installId, normalized, interactiveBackendTimeout, cancellationToken);
    }

    private void QueueSpeedTest(TelemetrySpeedTestEvent telemetry) =>
        telemetryQueue.Enqueue([TelemetrySerialization.Serialize(telemetry)]);

    private static bool ShouldRetryLater(string? error) => error is
        "rate_limited" or
        "timeout" or
        "transport" or
        "dns" or
        "connect" or
        "tls" or
        "proxy" or
        "http_version" or
        "redirect" or
        "protocol" or
        "invalid_response" or
        "http_408" or
        "http_429" or
        "http_500" or
        "http_502" or
        "http_503" or
        "http_504";
}
