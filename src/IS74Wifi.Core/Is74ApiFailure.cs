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

public sealed record Is74ApiResult<T>(T? Value, Is74ApiFailure? Failure)
{
    public bool IsSuccess => Failure is null;

    public static Is74ApiResult<T> Success(T value) => new(value, null);

    public static Is74ApiResult<T> Fail(Is74ApiFailure failure) => new(default, failure);
}
