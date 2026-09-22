namespace IS74Wifi.Core;

public enum AnonymousStatisticsConsent
{
    Unknown = 0,
    Allowed = 1,
    Declined = 2
}

public sealed record AppSettings
{
    public NotificationMode NotificationMode { get; init; } = NotificationMode.Important;
    // Explicit user override for environments where Windows network detection
    // is not representative of the route used by captive-portal traffic
    // (USB tethering, VPN/proxy software, multiple adapters, etc.).
    public bool IgnoreNetworkCheck { get; init; }
    public AnonymousStatisticsConsent AnonymousStatisticsConsent { get; init; } = global::IS74Wifi.Core.AnonymousStatisticsConsent.Unknown;
    public bool AutomaticUpdates { get; init; }
    // null means follow the channel implied by the current build: alpha builds
    // continue receiving prereleases, stable builds stay on stable releases.
    public bool? IncludePrereleaseUpdates { get; init; }
    public double AuthWindowHours { get; init; } = 24;
    // Cadence during the five-minute approach to expiry; distant daytime
    // checks use AgentTiming's 15-minute heartbeat instead.
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
    public int TelemetryHttpTimeoutMilliseconds { get; init; } = 10000;
    public int InteractiveBackendTimeoutMilliseconds { get; init; } = 15000;
    public int TelemetryMaxBatchesPerFlush { get; init; } = 1;
}
