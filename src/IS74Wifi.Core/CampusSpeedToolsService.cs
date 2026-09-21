namespace IS74Wifi.Core;

public sealed record CampusSpeedTestRun(
    SpeedTestMeasurement Measurement,
    SpeedTestRadioSnapshot Radio,
    TelemetrySpeedTestEvent Telemetry,
    TelemetryWriteResult StatisticsWrite,
    bool StatisticsQueued);

public sealed record CampusLeaderboardPublishResult(
    TelemetryWriteResult Write,
    bool Queued);

public sealed class CampusSpeedToolsService(
    ISpeedTestProvider speedTestProvider,
    TelemetryClient? telemetryClient,
    TelemetryQueue telemetryQueue,
    string installId,
    string appVersion,
    int interactiveBackendTimeoutMilliseconds)
{
    private readonly TimeSpan interactiveBackendTimeout = TimeSpan.FromMilliseconds(
        Math.Clamp(interactiveBackendTimeoutMilliseconds, 3000, 30000));

    public bool BackendEnabled => telemetryClient is not null;

    public async Task<CampusSpeedTestRun> MeasureAsync(
        IProgress<SpeedTestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var measurement = await speedTestProvider.MeasureAsync(progress, cancellationToken).ConfigureAwait(false);
        var radio = WindowsWifiService.GetSpeedTestRadioSnapshot();
        var telemetry = SpeedTestTelemetry.CreateEvent(measurement, installId, appVersion, radio);

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
        var trimmed = nickname.Trim();
        if (trimmed.Length is < 1 or > 32 ||
            trimmed.Any(ch => char.IsControl(ch)) ||
            "=+-@".Contains(trimmed[0]))
        {
            return new CampusLeaderboardPublishResult(
                new TelemetryWriteResult(false, false, "invalid_nickname"),
                Queued: false);
        }

        if (telemetryClient is null)
        {
            return new CampusLeaderboardPublishResult(
                new TelemetryWriteResult(false, false, "backend_not_configured"),
                Queued: false);
        }

        var speed = run.Telemetry;
        var entry = new TelemetryLeaderboardEntry
        {
            EntryId = "leader-" + Guid.NewGuid().ToString("N"),
            InstallId = installId,
            TestId = speed.TestId,
            AppVersion = appVersion,
            EventId = "event-" + Guid.NewGuid().ToString("N"),
            Nickname = trimmed,
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

        var queued = !write.Success && ShouldRetryLater(write.Error);
        if (queued)
        {
            telemetryQueue.Enqueue([TelemetrySerialization.Serialize(entry)]);
        }

        return new CampusLeaderboardPublishResult(write, queued);
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

    private void QueueSpeedTest(TelemetrySpeedTestEvent telemetry) =>
        telemetryQueue.Enqueue([TelemetrySerialization.Serialize(telemetry)]);

    private static bool ShouldRetryLater(string? error) => error is
        "timeout" or
        "transport" or
        "invalid_response" or
        "http_500" or
        "http_502" or
        "http_503" or
        "http_504";
}
