namespace IS74Wifi.Core;

public sealed record AppSettings
{
    public NotificationMode NotificationMode { get; init; } = NotificationMode.Important;
    public double AuthWindowHours { get; init; } = 24;
    public int AgentPollSeconds { get; init; } = 15;
    public int GuardWindowSeconds { get; init; } = 10;
    public int GuardProbeIntervalMilliseconds { get; init; } = 250;
    public int GuardProbeTimeoutMilliseconds { get; init; } = 300;
    public int InternetProbeConfirmDelaySeconds { get; init; } = 2;
    public int MaxAutomaticStepOneAttempts { get; init; } = ProtocolContract.MaxAutomaticStepOneAttempts;
    public int[] AutomaticRetryDelaysSeconds { get; init; } = [15, 30, 60];

    // Telemetry is local-first. With no endpoint configured, traces simply stay
    // in the local queue and authorization behavior is unchanged.
    public string? TelemetryEndpoint { get; init; }
    public int TelemetryUploadIntervalHours { get; init; } = 12;
    public int TelemetryHttpTimeoutMilliseconds { get; init; } = 3000;
    public int InteractiveBackendTimeoutMilliseconds { get; init; } = 15000;
    public int TelemetryMaxBatchesPerFlush { get; init; } = 1;
}
