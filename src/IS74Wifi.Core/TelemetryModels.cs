using System.Text.Json.Serialization;

namespace IS74Wifi.Core;

public static class TelemetryContract
{
    public const int Schema = 3;
    public const string PushScheduleVersion = "push-v1";
    // Keep transport batches within the currently deployed Apps Script receiver's
    // hard guards. Payload leaves a small envelope/headroom margin below 64 KiB.
    public const int MaxUploadEvents = 64;
    public const int MaxUploadPayloadBytes = 60 * 1024;
}

public sealed record TelemetryAttemptEvent
{
    public string EventType { get; init; } = "attempt";
    public int Schema { get; init; } = TelemetryContract.Schema;
    public required string AttemptId { get; init; }
    public required string InstallId { get; init; }
    public required string AppVersion { get; init; }
    public required string EventId { get; init; }
    public required string Result { get; init; }
    public required string Trigger { get; init; }
    public double? BaselineMs { get; init; }
    public double? StepOneResponseMs { get; init; }
    public int? StepOneStatus { get; init; }
    public double? FirstPollStartedMs { get; init; }
    public double? CodeObservedMs { get; init; }
    public int? PollThatFoundCode { get; init; }
    public int? MailboxRequests { get; init; }
    public double? StepTwoStartedMs { get; init; }
    public double? StepTwoResponseMs { get; init; }
    public int? StepTwoStatus { get; init; }
    public double? InternetConfirmedMs { get; init; }
    public int? InternetProbeRequests { get; init; }
    public bool? CodeBeforeStepOneResponse { get; init; }
    public double? StepOneResponseMinusCodeMs { get; init; }
    public int? StepOneAttempts { get; init; }
    public int? StepTwoAttempts { get; init; }
    public required double TotalMs { get; init; }
    public string ScheduleVersion { get; init; } = TelemetryContract.PushScheduleVersion;
    public bool? StepTwoBeforeStepOneResponse { get; init; }
    public double? CodeToStepTwoMs { get; init; }
    public bool? FastPathUsed { get; init; }
    public bool? FastPathSuccess { get; init; }
    public int? MaxMailboxInFlight { get; init; }
    public bool? OverlappingMailboxObserved { get; init; }
    public string? StepOneLocationKind { get; init; }
    public string? StepTwoLocationKind { get; init; }
    public double? InternetConfirmedAfterStepTwoMs { get; init; }
}

public sealed record TelemetryMailboxPollEvent
{
    public string EventType { get; init; } = "mailbox_poll";
    public int Schema { get; init; } = TelemetryContract.Schema;
    public required string AttemptId { get; init; }
    public required string InstallId { get; init; }
    public required string AppVersion { get; init; }
    public required string EventId { get; init; }
    public required int PollIndex { get; init; }
    public string ScheduleVersion { get; init; } = TelemetryContract.PushScheduleVersion;
    public double? PlannedStartMs { get; init; }
    public double? ActualStartMs { get; init; }
    public double? CompletedMs { get; init; }
    public double? DurationMs { get; init; }
    public int? ResponseOrder { get; init; }
    public int? HttpStatus { get; init; }
    public int? PageSize { get; init; }
    public int? ReturnedCount { get; init; }
    public int? FreshMessageCount { get; init; }
    public bool? WifiCodeFound { get; init; }
    public long? NewestIdDelta { get; init; }
    public long? WifiMessageIdDelta { get; init; }
    public int? InFlightAtStart { get; init; }
    public string? CacheStatus { get; init; }
    public bool? Cancelled { get; init; }
    public string? CancelReason { get; init; }
}

public sealed record TelemetryInternetProbeEvent
{
    public string EventType { get; init; } = "internet_probe";
    public int Schema { get; init; } = TelemetryContract.Schema;
    public required string AttemptId { get; init; }
    public required string InstallId { get; init; }
    public required string AppVersion { get; init; }
    public required string EventId { get; init; }
    public required string Phase { get; init; }
    public required int ProbeIndex { get; init; }
    public double? PlannedStartMs { get; init; }
    public double? ActualStartMs { get; init; }
    public double? CompletedMs { get; init; }
    public double? DurationMs { get; init; }
    public int? HttpStatus { get; init; }
    public string? LocationKind { get; init; }
    public bool? Online { get; init; }
    public string? BodyKind { get; init; }
    public int? InFlightAtStart { get; init; }
}

public sealed record TelemetryPortalResponseEvent
{
    public string EventType { get; init; } = "portal_response";
    public int Schema { get; init; } = TelemetryContract.Schema;
    public required string AttemptId { get; init; }
    public required string InstallId { get; init; }
    public required string AppVersion { get; init; }
    public required string EventId { get; init; }
    public required string Stage { get; init; }
    public required int RequestIndex { get; init; }
    public double? StartedMs { get; init; }
    public double? CompletedMs { get; init; }
    public double? DurationMs { get; init; }
    public int? HttpStatus { get; init; }
    public string? LocationKind { get; init; }
    public string? Server { get; init; }
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }
    public double? RetryAfterMs { get; init; }
    public string? BodyKind { get; init; }
    public string? BodySha256 { get; init; }
}

public sealed record TelemetryErrorEvent
{
    public string EventType { get; init; } = "error";
    public int Schema { get; init; } = TelemetryContract.Schema;
    public required string AttemptId { get; init; }
    public required string InstallId { get; init; }
    public required string AppVersion { get; init; }
    public required string EventId { get; init; }
    public required string Stage { get; init; }
    public required string ErrorClass { get; init; }
    public int? HttpStatus { get; init; }
    public double? DurationMs { get; init; }
    public string? ExceptionType { get; init; }
    public string? NativeError { get; init; }
    public string? LocationKind { get; init; }
}

// Reserved for the planned Campus Wi-Fi quality feature. These DTOs deliberately
// contain no phone/account identifiers and use the same permanent random install_id.
public sealed record TelemetrySpeedTestEvent
{
    public string EventType { get; init; } = "speed_test";
    public int Schema { get; init; } = TelemetryContract.Schema;
    public required string TestId { get; init; }
    public required string InstallId { get; init; }
    public required string AppVersion { get; init; }
    public required string EventId { get; init; }
    public required string TestVersion { get; init; }
    public required string Result { get; init; }
    public required string TestScope { get; init; }
    public required string ServerKind { get; init; }
    public double? DownloadMbps { get; init; }
    public double? UploadMbps { get; init; }
    public double? LatencyMs { get; init; }
    public double? JitterMs { get; init; }
    public double? PacketLossPct { get; init; }
    public double? DownloadDurationMs { get; init; }
    public double? UploadDurationMs { get; init; }
    public int? SampleCount { get; init; }
    public string WifiSignalBucket { get; init; } = "unknown";
    public string WifiBand { get; init; } = "unknown";
    public string ConnectionType { get; init; } = "unknown";
    public string TimeBucket { get; init; } = "unknown";
    public long? DownloadBytes { get; init; }
    public long? UploadBytes { get; init; }
    public double? LinkRxMbps { get; init; }
    public double? LinkTxMbps { get; init; }
    public string? WifiProtocol { get; init; }
}

public sealed record TelemetryLeaderboardEntry
{
    public string EventType { get; init; } = "leaderboard_entry";
    public int Schema { get; init; } = TelemetryContract.Schema;
    public required string EntryId { get; init; }
    public required string InstallId { get; init; }
    public required string TestId { get; init; }
    public required string AppVersion { get; init; }
    public required string EventId { get; init; }
    public required string Nickname { get; init; }
    public double? DownloadMbps { get; init; }
    public double? UploadMbps { get; init; }
    public double? LatencyMs { get; init; }
    public double? JitterMs { get; init; }
    public double? PacketLossPct { get; init; }
    public string WifiSignalBucket { get; init; } = "unknown";
    public string WifiBand { get; init; } = "unknown";
    public string TimeBucket { get; init; } = "unknown";
}

public sealed record TelemetryUploadState(
    DateTimeOffset? LastSuccessfulUploadUtc,
    DateTimeOffset? NextAttemptUtc,
    int ConsecutiveFailures);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(TelemetryAttemptEvent))]
[JsonSerializable(typeof(TelemetryMailboxPollEvent))]
[JsonSerializable(typeof(TelemetryInternetProbeEvent))]
[JsonSerializable(typeof(TelemetryPortalResponseEvent))]
[JsonSerializable(typeof(TelemetryErrorEvent))]
[JsonSerializable(typeof(TelemetrySpeedTestEvent))]
[JsonSerializable(typeof(TelemetryLeaderboardEntry))]
internal sealed partial class TelemetryJsonContext : JsonSerializerContext;
