namespace IS74Wifi.Core;

public sealed record RuntimeState
{
    public DateTimeOffset? LastAuthUtc { get; init; }
    public DateTimeOffset? ExpectedExpiryUtc { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public string? LastAttemptReason { get; init; }
    public string? LastResult { get; init; }
    public bool? InternetConfirmed { get; init; }
    public int AutomaticStepOneAttempts { get; init; }
    public int PreStepFailureCount { get; init; }
    public DateTimeOffset? NextAutomaticRetryUtc { get; init; }
    public bool UserActionRequired { get; init; }
    public bool EdgeWatchActive { get; init; }
}
