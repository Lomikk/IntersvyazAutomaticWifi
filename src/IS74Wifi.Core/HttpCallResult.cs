using System.Net;

namespace IS74Wifi.Core;

public sealed record HttpResponseData(
    HttpStatusCode StatusCode,
    string Body,
    Uri? Location,
    DateTimeOffset? ServerDate,
    TimeSpan Elapsed,
    string? Server = null,
    string? ContentType = null,
    long? ContentLength = null,
    TimeSpan? RetryAfter = null,
    string? CacheStatus = null)
{
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
}

public sealed record HttpCallResult(
    HttpResponseData? Response,
    TransportFailureKind FailureKind,
    string? ErrorMessage,
    TimeSpan Elapsed)
{
    public bool TransportSucceeded => FailureKind == TransportFailureKind.None && Response is not null;

    public static HttpCallResult Success(HttpResponseData response) =>
        new(response, TransportFailureKind.None, null, response.Elapsed);

    public static HttpCallResult Failure(TransportFailureKind kind, string? message, TimeSpan elapsed) =>
        new(null, kind, message, elapsed);
}
