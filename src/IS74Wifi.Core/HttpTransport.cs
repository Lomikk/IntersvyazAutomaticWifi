using System.Diagnostics;
using System.Net.Http;

namespace IS74Wifi.Core;

public sealed class HttpTransport(HttpClient client)
{
    public async Task<HttpCallResult> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        bool readBody = true)
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
            var body = readBody
                ? await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false)
                : string.Empty;
            var location = response.Headers.Location;
            var serverDate = response.Headers.Date;
            return HttpCallResult.Success(new HttpResponseData(
                response.StatusCode,
                body,
                location,
                serverDate,
                clock.Elapsed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HttpCallResult.Failure(TransportFailureKind.Cancelled, null, clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return HttpCallResult.Failure(TransportFailureKind.Timeout, null, clock.Elapsed);
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
        catch (HttpRequestException exception)
        {
            return HttpCallResult.Failure(TransportFailureKind.Unexpected, exception.Message, clock.Elapsed);
        }
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
