namespace IS74Wifi.Core;

public enum AuthorizationAttemptReason
{
    Manual,
    Automatic,
    Retry
}

public enum AuthorizationOutcomeKind
{
    Success,
    AlreadyAuthorized,
    AlreadyOnline,
    WrongWifi,
    Busy,
    BearerInvalid,
    RetryableBeforeStepOne,
    RetryableStepOne,
    StepTwoAmbiguous,
    UserActionRequired,
    Cancelled
}

public enum WifiCodeSource
{
    Primary,
    Fallback
}

public enum AuthorizationProgressStage
{
    TargetWifiConfirmed,
    BaselineLoaded,
    CaptiveRequestStarted,
    FreshCodeReceived,
    StepTwoStarted,
    StepTwoAccepted,
    InternetCheckStarted,
    InternetConfirmed
}

public sealed record AuthorizationRequest(
    string BearerToken,
    string Phone,
    string DeviceId,
    AuthorizationAttemptReason Reason = AuthorizationAttemptReason.Manual,
    bool Force = false,
    Action<AuthorizationProgressStage>? Progress = null);

public sealed record AuthorizationTiming(
    int PreStepOneMilliseconds,
    long BaselineId,
    int? StepOneAttempt,
    WifiCodeSource? CodeSource,
    int? CodeTargetMilliseconds,
    int? CodePollStartMilliseconds,
    int? CodeObservedMilliseconds,
    int? StepTwoStartMilliseconds,
    int? StepTwoDoneMilliseconds,
    IReadOnlyList<PushPollObservation> Polls);

public sealed record AuthorizationOutcome(
    AuthorizationOutcomeKind Kind,
    bool? InternetConfirmed,
    DateTimeOffset? AuthorizedAtUtc,
    TimeSpan? RetryAfter,
    AuthorizationTiming? Timing);

public sealed record AuthorizationFlowOptions
{
    public TimeSpan InitialInternetProbeTimeout { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan BaselineTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan StepOneTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan StepTwoTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RecoveryInternetProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan PostSuccessInternetProbeTimeout { get; init; } = TimeSpan.FromSeconds(4);
    public int[] LostStepTwoProbeOffsetsMilliseconds { get; init; } = ProtocolContract.LostStepTwoProbeOffsetsMilliseconds.ToArray();
    public int[] PostSuccessProbeOffsetsMilliseconds { get; init; } = [0, 500, 1000, 1500, 2000, 2500, 3000, 3500];
}
