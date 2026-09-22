using System.Diagnostics;
using System.Net.Http;

namespace IS74Wifi.Core;

public sealed class HttpTransport(HttpClient client, DiagnosticLogger? logger = null)
{
    public async Task<HttpCallResult> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        bool readBody = true,
        bool readBodyOnRedirect = true)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var clock = Stopwatch.StartNew();
        // SendAsync(ResponseHeadersRead) includes DNS, TCP, TLS and waiting for
        // response headers. A timeout here cannot be attributed to one of those
        // sub-phases without changing the production connection handler.
        var phase = "before_headers";
        TimeSpan? headersElapsed = null;
        int? statusCode = null;

        HttpCallResult Fail(TransportFailureKind kind, string? detail = null)
        {
            if (kind != TransportFailureKind.Cancelled)
            {
                LogFailure(logger, request, kind, phase, clock.Elapsed, timeout, headersElapsed, statusCode);
            }
            return HttpCallResult.Failure(kind, detail, clock.Elapsed);
        }

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);
            headersElapsed = clock.Elapsed;
            statusCode = (int)response.StatusCode;
            phase = "reading_body";
            var isRedirect = (int)response.StatusCode is >= 300 and <= 399;
            var body = readBody && (!isRedirect || readBodyOnRedirect)
                ? await BoundedHttpContent.ReadAsStringAsync(
                    response.Content, BoundedHttpContent.DefaultBodyLimitBytes, timeoutCts.Token).ConfigureAwait(false)
                : string.Empty;
            var location = response.Headers.Location;
            var serverDate = response.Headers.Date;
            var server = response.Headers.Server.Count == 0 ? null : response.Headers.Server.ToString();
            var contentType = response.Content.Headers.ContentType?.ToString();
            var contentLength = response.Content.Headers.ContentLength;
            var retryAfter = GetRetryAfter(response.Headers.RetryAfter, serverDate);
            var cacheStatus = response.Headers.TryGetValues("X-Cache-Status", out var cacheValues)
                ? cacheValues.FirstOrDefault()
                : null;
            if ((int)response.StatusCode >= 400)
            {
                LogHttpStatus(logger, request, (int)response.StatusCode, clock.Elapsed, headersElapsed.Value, retryAfter);
            }
            return HttpCallResult.Success(new HttpResponseData(
                response.StatusCode,
                body,
                location,
                serverDate,
                clock.Elapsed,
                server,
                contentType,
                contentLength,
                retryAfter,
                cacheStatus));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(TransportFailureKind.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return Fail(TransportFailureKind.Timeout);
        }
        catch (ResponseBodyTooLargeException)
        {
            return Fail(TransportFailureKind.ResponseTooLarge);
        }
        catch (CachedDnsUnavailableException exception)
        {
            return Fail(TransportFailureKind.DnsUnavailable, exception.Message);
        }
        catch (HttpRequestException exception) when (
            exception.HttpRequestError == HttpRequestError.NameResolutionError ||
            ContainsCachedDnsUnavailable(exception))
        {
            return Fail(TransportFailureKind.DnsUnavailable, exception.Message);
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.ConnectionError)
        {
            return Fail(TransportFailureKind.ConnectionFailure, exception.Message);
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return Fail(TransportFailureKind.TlsFailure);
        }
        catch (HttpRequestException exception)
        {
            return Fail(TransportFailureKind.Unexpected, exception.Message);
        }
    }

    // Only fixed endpoint/route labels enter the local log. Never write the full
    // URL: stepTwo contains a phone query parameter and requests carry secrets.
    private static string SafeRoute(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true }) return "other";
        return (uri.Host.ToLowerInvariant(), uri.AbsolutePath) switch
        {
            ("api.is74.ru", "/mobile/auth/get-confirm") => "api.get-confirm",
            ("api.is74.ru", "/mobile/auth/check-confirm") => "api.check-confirm",
            ("api.is74.ru", "/mobile/auth/get-token") => "api.get-token",
            ("api.is74.ru", "/mobile/pushtoken/add-with-device-id") => "api.device-metadata",
            ("api.is74.ru", "/mobile/pushmessages") => "api.pushmessages",
            ("w.is74.ru", "/stepOne") => "portal.stepOne",
            ("w.is74.ru", "/stepTwo") => "portal.stepTwo",
            ("online.susu.ru", "/") => "internet.probe",
            _ => "other"
        };
    }

    private static long Milliseconds(TimeSpan duration) =>
        (long)Math.Max(0, Math.Round(duration.TotalMilliseconds));

    private static void LogFailure(
        DiagnosticLogger? logger,
        HttpRequestMessage request,
        TransportFailureKind kind,
        string phase,
        TimeSpan elapsed,
        TimeSpan timeout,
        TimeSpan? headersElapsed,
        int? statusCode)
    {
        if (logger is null) return;
        var route = SafeRoute(request.RequestUri);
        // The guard-window Internet probe may fire every few hundred ms on a
        // captive network. Do not rotate out useful API/portal errors with noise.
        if (route is "internet.probe" or "other") return;
        var headersMs = headersElapsed is null ? "none" : Milliseconds(headersElapsed.Value).ToString();
        logger.Write(DiagnosticLevel.Warn,
            $"network.failure route={route} kind={kind} phase={phase} " +
            $"elapsedMs={Milliseconds(elapsed)} budgetMs={Milliseconds(timeout)} " +
            $"headersMs={headersMs} httpStatus={statusCode?.ToString() ?? "none"}");
    }

    private static void LogHttpStatus(
        DiagnosticLogger? logger,
        HttpRequestMessage request,
        int statusCode,
        TimeSpan elapsed,
        TimeSpan headersElapsed,
        TimeSpan? retryAfter)
    {
        if (logger is null) return;
        var route = SafeRoute(request.RequestUri);
        if (route is "internet.probe" or "other") return;
        logger.Write(DiagnosticLevel.Warn,
            $"network.http-error route={route} status={statusCode} " +
            $"elapsedMs={Milliseconds(elapsed)} headersMs={Milliseconds(headersElapsed)} " +
            $"retryAfterMs={(retryAfter is null ? "none" : Milliseconds(retryAfter.Value).ToString())}");
    }


    private static TimeSpan? GetRetryAfter(
        System.Net.Http.Headers.RetryConditionHeaderValue? value,
        DateTimeOffset? serverDate)
    {
        if (value?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }
        if (value?.Date is { } date)
        {
            var origin = serverDate ?? DateTimeOffset.UtcNow;
            var remaining = date - origin;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
        return null;
    }

    private static bool ContainsCachedDnsUnavailable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is CachedDnsUnavailableException)
            {
                return true;
            }
        }
        return false;
    }
}
