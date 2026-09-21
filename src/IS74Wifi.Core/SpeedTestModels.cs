namespace IS74Wifi.Core;

public enum SpeedTestStage
{
    Latency,
    Download,
    Upload,
    Completed
}

public sealed record SpeedTestProgress(
    SpeedTestStage Stage,
    double Progress,
    double? Mbps = null,
    double? LatencyMs = null,
    double? JitterMs = null);

public sealed record SpeedTestMeasurement(
    string Provider,
    string TestVersion,
    string TestScope,
    string ServerKind,
    double DownloadMbps,
    double UploadMbps,
    double LatencyMs,
    double JitterMs,
    double? PacketLossPct,
    TimeSpan DownloadDuration,
    TimeSpan UploadDuration,
    int LatencySampleCount,
    long DownloadBytes,
    long UploadBytes);

public interface ISpeedTestProvider
{
    Task<SpeedTestMeasurement> MeasureAsync(
        IProgress<SpeedTestProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record SpeedTestRadioSnapshot(
    string WifiSignalBucket = "unknown",
    string WifiBand = "unknown",
    string ConnectionType = "unknown",
    double? LinkRxMbps = null,
    double? LinkTxMbps = null,
    string? WifiProtocol = null);

public static class SpeedTestTelemetry
{
    public static TelemetrySpeedTestEvent CreateEvent(
        SpeedTestMeasurement measurement,
        string installId,
        string appVersion,
        SpeedTestRadioSnapshot? radio = null,
        DateTimeOffset? now = null)
    {
        radio ??= new SpeedTestRadioSnapshot();
        var timestamp = now ?? DateTimeOffset.Now;

        return new TelemetrySpeedTestEvent
        {
            TestId = "speed-" + Guid.NewGuid().ToString("N"),
            InstallId = installId,
            AppVersion = appVersion,
            EventId = "event-" + Guid.NewGuid().ToString("N"),
            TestVersion = measurement.TestVersion,
            Result = "success",
            TestScope = measurement.TestScope,
            ServerKind = measurement.ServerKind,
            DownloadMbps = RoundMetric(measurement.DownloadMbps),
            UploadMbps = RoundMetric(measurement.UploadMbps),
            LatencyMs = RoundMetric(measurement.LatencyMs),
            JitterMs = RoundMetric(measurement.JitterMs),
            PacketLossPct = measurement.PacketLossPct is { } loss ? RoundMetric(loss) : null,
            DownloadDurationMs = RoundMetric(measurement.DownloadDuration.TotalMilliseconds),
            UploadDurationMs = RoundMetric(measurement.UploadDuration.TotalMilliseconds),
            SampleCount = measurement.LatencySampleCount,
            WifiSignalBucket = radio.WifiSignalBucket,
            WifiBand = radio.WifiBand,
            ConnectionType = radio.ConnectionType,
            TimeBucket = GetTimeBucket(timestamp),
            DownloadBytes = measurement.DownloadBytes,
            UploadBytes = measurement.UploadBytes,
            LinkRxMbps = radio.LinkRxMbps is { } rx ? RoundMetric(rx) : null,
            LinkTxMbps = radio.LinkTxMbps is { } tx ? RoundMetric(tx) : null,
            WifiProtocol = radio.WifiProtocol
        };
    }

    private static string GetTimeBucket(DateTimeOffset value) => value.Hour switch
    {
        >= 0 and < 6 => "night",
        >= 6 and < 12 => "morning",
        >= 12 and < 18 => "day",
        _ => "evening"
    };

    private static double RoundMetric(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
