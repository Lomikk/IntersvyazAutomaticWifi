using System.Security.Cryptography;
using System.Text;
using System.Net;

namespace IS74Wifi.Core;

public sealed class CaptivePortalClient(HttpTransport transport) : ICaptivePortalClient
{
    private static readonly Uri PortalBase = new("http://w.is74.ru/");
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public async Task<CaptivePortalResult<StepOneResponse>> SendStepOneAsync(
        string phone,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        const string operation = "portal.stepOne";
        ValidatePhone(phone);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(PortalBase, "stepOne"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["phone"] = "8" + phone,
            ["dial_code"] = "7",
            ["country_code"] = "ru",
            ["sendPush"] = "on"
        });

        var call = await transport.SendAsync(request, timeout ?? DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (!call.TransportSucceeded)
        {
            return CaptivePortalResult<StepOneResponse>.Fail(TransportFailure(operation, call));
        }

        var response = call.Response!;
        if (!IsRedirect(response.StatusCode))
        {
            return CaptivePortalResult<StepOneResponse>.Fail(HttpStatusFailure(operation, response));
        }

        var location = response.Location;
        if (location is null)
        {
            return UnexpectedRedirect<StepOneResponse>(operation, response);
        }

        if (PortalRedirectClassifier.IsAlreadyAuthorized(location))
        {
            return CaptivePortalResult<StepOneResponse>.Success(new StepOneResponse(
                StepOneDisposition.AlreadyAuthorized,
                location,
                null,
                response.ServerDate,
                response.Elapsed,
                (int)response.StatusCode,
                response.Server,
                response.ContentType,
                response.ContentLength,
                response.RetryAfter));
        }

        if (PortalRedirectClassifier.TryResolveStepTwo(location, out var stepTwoUri))
        {
            return CaptivePortalResult<StepOneResponse>.Success(new StepOneResponse(
                StepOneDisposition.StepTwo,
                location,
                stepTwoUri,
                response.ServerDate,
                response.Elapsed,
                (int)response.StatusCode,
                response.Server,
                response.ContentType,
                response.ContentLength,
                response.RetryAfter));
        }

        return UnexpectedRedirect<StepOneResponse>(operation, response);
    }

    public async Task<CaptivePortalResult<StepTwoResponse>> SendStepTwoAsync(
        string phone,
        string confirmCode,
        Uri? observedStepTwoLocation = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        const string operation = "portal.stepTwo";
        ValidatePhone(phone);
        ValidateConfirmationCode(confirmCode);

        Uri stepTwoUri;
        if (observedStepTwoLocation is null)
        {
            stepTwoUri = BuildDirectStepTwoUri(phone);
        }
        else if (!PortalRedirectClassifier.TryResolveStepTwo(observedStepTwoLocation, out stepTwoUri))
        {
            throw new ArgumentException("Observed Location is not an allowed stepTwo route.", nameof(observedStepTwoLocation));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, stepTwoUri);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["confirmCode"] = confirmCode,
            ["phone"] = phone
        });

        var call = await transport.SendAsync(request, timeout ?? DefaultTimeout, cancellationToken).ConfigureAwait(false);
        if (!call.TransportSucceeded)
        {
            return CaptivePortalResult<StepTwoResponse>.Fail(TransportFailure(operation, call));
        }

        var response = call.Response!;
        if (!IsRedirect(response.StatusCode))
        {
            return CaptivePortalResult<StepTwoResponse>.Fail(HttpStatusFailure(operation, response));
        }

        if (response.Location is null || !PortalRedirectClassifier.IsStepThree(response.Location))
        {
            return UnexpectedRedirect<StepTwoResponse>(operation, response);
        }

        return CaptivePortalResult<StepTwoResponse>.Success(new StepTwoResponse(
            response.Location,
            response.ServerDate,
            response.Elapsed,
            (int)response.StatusCode,
            response.Server,
            response.ContentType,
            response.ContentLength,
            response.RetryAfter));
    }

    public static Uri BuildDirectStepTwoUri(string phone)
    {
        ValidatePhone(phone);
        return new Uri(PortalBase, $"stepTwo?phone={Uri.EscapeDataString(phone)}&isMp=true");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => (int)statusCode is >= 300 and <= 399;

    private static CaptivePortalFailure TransportFailure(string operation, HttpCallResult call) => new(
        CaptivePortalFailureKind.Transport,
        operation,
        call.FailureKind,
        SideEffectMayHaveOccurred: call.FailureKind != TransportFailureKind.DnsUnavailable,
        Elapsed: call.Elapsed);

    private static CaptivePortalFailure HttpStatusFailure(string operation, HttpResponseData response) => new(
        CaptivePortalFailureKind.HttpStatus,
        operation,
        StatusCode: (int)response.StatusCode,
        Location: response.Location,
        SideEffectMayHaveOccurred: true,
        Elapsed: response.Elapsed,
        Server: response.Server,
        ContentType: response.ContentType,
        ContentLength: response.ContentLength,
        RetryAfter: response.RetryAfter,
        BodyKind: string.IsNullOrEmpty(response.Body) ? "empty" : "other",
        BodySha256: HashBody(response.Body));

    private static CaptivePortalResult<T> UnexpectedRedirect<T>(string operation, HttpResponseData response) =>
        CaptivePortalResult<T>.Fail(new CaptivePortalFailure(
            CaptivePortalFailureKind.UnexpectedRedirect,
            operation,
            StatusCode: (int)response.StatusCode,
            Location: response.Location,
            SideEffectMayHaveOccurred: true,
            Elapsed: response.Elapsed,
            Server: response.Server,
            ContentType: response.ContentType,
            ContentLength: response.ContentLength,
            RetryAfter: response.RetryAfter,
            BodyKind: string.IsNullOrEmpty(response.Body) ? "empty" : "other",
            BodySha256: HashBody(response.Body)));

    private static string? HashBody(string body) =>
        string.IsNullOrEmpty(body)
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private static void ValidatePhone(string phone)
    {
        if (phone.Length != 10 || phone.Any(c => c is < '0' or > '9'))
        {
            throw new ArgumentException("Phone must contain exactly 10 digits.", nameof(phone));
        }
    }

    private static void ValidateConfirmationCode(string confirmCode)
    {
        if (confirmCode.Length != 4 || confirmCode.Any(c => c is < '0' or > '9'))
        {
            throw new ArgumentException("Wi-Fi confirmation code must contain exactly 4 digits.", nameof(confirmCode));
        }
    }
}

public static class PortalRedirectClassifier
{
    private static readonly Uri PortalBase = new("http://w.is74.ru/");

    public static bool TryResolveStepTwo(Uri location, out Uri stepTwoUri)
    {
        stepTwoUri = default!;
        if (!TryResolvePortalLocation(location, out var resolved))
        {
            return false;
        }

        if (!string.Equals(resolved.AbsolutePath, "/stepTwo", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(resolved.Query))
        {
            return false;
        }

        stepTwoUri = resolved;
        return true;
    }

    public static bool IsStepThree(Uri location)
    {
        if (!TryResolvePortalLocation(location, out var resolved))
        {
            return false;
        }

        return string.Equals(resolved.AbsolutePath, "/stepThree", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAlreadyAuthorized(Uri location)
    {
        if (!location.IsAbsoluteUri)
        {
            var text = location.OriginalString;
            return IsApplicationLandingPath(text);
        }

        if (!IsHttpScheme(location))
        {
            return false;
        }

        if (IsHost(location, "is74.ru") || IsHost(location, "www.is74.ru"))
        {
            return IsApplicationLandingPath(location.PathAndQuery + location.Fragment);
        }

        if (IsHost(location, "openwifi.is74.ru"))
        {
            return string.Equals(
                location.AbsolutePath.TrimEnd('/'),
                "/home/connect/formy_connect/landing/pages/wifi",
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool TryResolvePortalLocation(Uri location, out Uri resolved)
    {
        resolved = location.IsAbsoluteUri ? location : new Uri(PortalBase, location);
        return IsHost(resolved, "w.is74.ru") && IsHttpScheme(resolved);
    }

    private static bool IsApplicationLandingPath(string value)
    {
        var path = value;
        var queryOrFragment = path.IndexOfAny(['?', '#']);
        if (queryOrFragment >= 0)
        {
            path = path[..queryOrFragment];
        }

        return string.Equals(
            path.TrimEnd('/'),
            "/home/connect/formy_connect/landing/pages/prilozheniye",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHost(Uri uri, string host) =>
        string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}
