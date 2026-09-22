namespace IS74Wifi.Core;

public enum Is74ApiFailureKind
{
    Transport,
    Unauthorized,
    HttpStatus,
    InvalidJson,
    InvalidPayload,
    MultipleAddresses
}

public sealed record Is74ApiFailure(
    Is74ApiFailureKind Kind,
    string Operation,
    TransportFailureKind? TransportFailure = null,
    int? StatusCode = null);

public sealed record Is74ApiResult<T>(
    T? Value,
    Is74ApiFailure? Failure,
    int? HttpStatus = null,
    TimeSpan? Elapsed = null,
    string? Server = null,
    TimeSpan? RetryAfter = null)
{
    public bool IsSuccess => Failure is null;

    public static Is74ApiResult<T> Success(T value) => new(value, null);

    public static Is74ApiResult<T> Success(T value, HttpCallResult call) => new(
        value,
        null,
        call.Response is null ? null : (int)call.Response.StatusCode,
        call.Elapsed,
        call.Response?.Server,
        call.Response?.RetryAfter);

    public static Is74ApiResult<T> Fail(Is74ApiFailure failure) => new(default, failure);

    public static Is74ApiResult<T> Fail(Is74ApiFailure failure, HttpCallResult call) => new(
        default,
        failure,
        call.Response is null ? failure.StatusCode : (int)call.Response.StatusCode,
        call.Elapsed,
        call.Response?.Server,
        call.Response?.RetryAfter);
}
