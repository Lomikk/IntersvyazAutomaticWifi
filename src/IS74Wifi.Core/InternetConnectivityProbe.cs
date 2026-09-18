using System.Net;

namespace IS74Wifi.Core;

public sealed class InternetConnectivityProbe(HttpTransport transport)
{
    public static readonly Uri CanonicalUri = new("http://www.msftconnecttest.com/connecttest.txt");
    public const string ExpectedBody = "Microsoft Connect Test";

    public async Task<InternetProbeResult> ProbeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CanonicalUri);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");

        var result = await transport.SendAsync(request, timeout, cancellationToken).ConfigureAwait(false);
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
            Online: IsExpectedResponse(response.StatusCode, response.Body),
            HttpResponseReceived: true,
            StatusCode: response.StatusCode,
            Body: response.Body,
            FailureKind: TransportFailureKind.None,
            Elapsed: response.Elapsed);
    }

    public static bool IsExpectedResponse(HttpStatusCode statusCode, string? body) =>
        statusCode == HttpStatusCode.OK &&
        string.Equals(body?.Trim(), ExpectedBody, StringComparison.Ordinal);
}
