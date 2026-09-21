namespace IS74Wifi.Core;

public enum CaptivePortalFailureKind
{
    Transport,
    HttpStatus,
    UnexpectedRedirect
}

public sealed record CaptivePortalFailure(
    CaptivePortalFailureKind Kind,
    string Operation,
    TransportFailureKind? TransportFailure = null,
    int? StatusCode = null,
    Uri? Location = null,
    bool SideEffectMayHaveOccurred = false,
    TimeSpan? Elapsed = null,
    string? Server = null,
    string? ContentType = null,
    long? ContentLength = null,
    TimeSpan? RetryAfter = null,
    string? BodyKind = null,
    string? BodySha256 = null);

public sealed record CaptivePortalResult<T>(T? Value, CaptivePortalFailure? Failure)
{
    public bool IsSuccess => Failure is null;

    public static CaptivePortalResult<T> Success(T value) => new(value, null);

    public static CaptivePortalResult<T> Fail(CaptivePortalFailure failure) => new(default, failure);
}

public enum StepOneDisposition
{
    StepTwo,
    AlreadyAuthorized
}

public sealed record StepOneResponse(
    StepOneDisposition Disposition,
    Uri Location,
    Uri? StepTwoUri,
    DateTimeOffset? ServerDate,
    TimeSpan Elapsed,
    int StatusCode = 302,
    string? Server = null,
    string? ContentType = null,
    long? ContentLength = null,
    TimeSpan? RetryAfter = null);

public sealed record StepTwoResponse(
    Uri Location,
    DateTimeOffset? ServerDate,
    TimeSpan Elapsed,
    int StatusCode = 302,
    string? Server = null,
    string? ContentType = null,
    long? ContentLength = null,
    TimeSpan? RetryAfter = null);
