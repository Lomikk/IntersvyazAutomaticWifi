using System.Net;

namespace IS74Wifi.Core;

public sealed class InternetConnectivityProbe(HttpTransport transport) : IInternetConnectivityProbe
{
    public static readonly Uri CanonicalUri = new("http://online.susu.ru/");
    public static readonly Uri ExpectedLocation = new("https://online.susu.ru/");

    public async Task<InternetProbeResult> ProbeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CanonicalUri);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");

        // The validated success condition is entirely in the response headers:
        // online.susu.ru redirects HTTP -> HTTPS when Internet is open, while
        // InterSvyaz captive interception returns HTTP 200 with injected HTML.
        // Do not read a response body on this timing-critical probe.
        var result = await transport.SendAsync(
            request,
            timeout,
            cancellationToken,
            readBody: false).ConfigureAwait(false);

        if (!result.TransportSucceeded || result.Response is null)
        {
            return new InternetProbeResult(
                Online: false,
                HttpResponseReceived: false,
                StatusCode: null,
                Body: null,
                FailureKind: result.FailureKind,
                Elapsed: result.Elapsed);
        }

        var response = result.Response;
        return new InternetProbeResult(
            Online: IsExpectedResponse(response.StatusCode, response.Location),
            HttpResponseReceived: true,
            StatusCode: response.StatusCode,
            Body: null,
            FailureKind: TransportFailureKind.None,
            Elapsed: response.Elapsed);
    }

    public static bool IsExpectedResponse(HttpStatusCode statusCode, Uri? location)
    {
        var status = (int)statusCode;
        return status is >= 300 and <= 399 &&
               location is { IsAbsoluteUri: true } &&
               string.Equals(
                   location.AbsoluteUri,
                   ExpectedLocation.AbsoluteUri,
                   StringComparison.OrdinalIgnoreCase);
    }
}
