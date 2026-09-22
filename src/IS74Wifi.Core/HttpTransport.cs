using System.Diagnostics;
using System.Net.Http;

namespace IS74Wifi.Core;

public sealed class HttpTransport(HttpClient client)
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

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);
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
            return HttpCallResult.Failure(TransportFailureKind.Cancelled, null, clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return HttpCallResult.Failure(TransportFailureKind.Timeout, null, clock.Elapsed);
        }
        catch (ResponseBodyTooLargeException)
        {
            return HttpCallResult.Failure(TransportFailureKind.ResponseTooLarge, null, clock.Elapsed);
        }
        catch (CachedDnsUnavailableException exception)
        {
            return HttpCallResult.Failure(TransportFailureKind.DnsUnavailable, exception.Message, clock.Elapsed);
        }
        catch (HttpRequestException exception) when (
            exception.HttpRequestError == HttpRequestError.NameResolutionError ||
            ContainsCachedDnsUnavailable(exception))
        {
            return HttpCallResult.Failure(TransportFailureKind.DnsUnavailable, exception.Message, clock.Elapsed);
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.ConnectionError)
        {
            return HttpCallResult.Failure(TransportFailureKind.ConnectionFailure, exception.Message, clock.Elapsed);
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return HttpCallResult.Failure(TransportFailureKind.TlsFailure, null, clock.Elapsed);
        }
        catch (HttpRequestException exception)
        {
            return HttpCallResult.Failure(TransportFailureKind.Unexpected, exception.Message, clock.Elapsed);
        }
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
