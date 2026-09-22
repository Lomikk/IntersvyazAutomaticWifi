using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

namespace IS74Wifi.Core;

public sealed record TelemetryDiagnosticHop(
    int Index,
    string Method,
    Uri Uri,
    int? StatusCode,
    string? HttpVersion,
    Uri? Location,
    string? ContentType,
    TimeSpan Elapsed,
    string? Body,
    string? Error,
    string? ErrorDetail);

public sealed record TelemetryDiagnosticReport(
    bool Completed,
    string? Error,
    IReadOnlyList<TelemetryDiagnosticHop> Hops);

public static class TelemetryDiagnostics
{
    private const int MaximumRedirects = 10;
    private const int BodyPreviewCharacters = 4096;

    public static Uri BuildLeaderboardProbeUri(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var query = string.IsNullOrWhiteSpace(builder.Query)
            ? string.Empty
            : builder.Query.TrimStart('?') + "&";
        builder.Query = query + "route=leaderboard&limit=3";
        return builder.Uri;
    }

    public static Uri BuildPostProbeUri(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint);
        var query = string.IsNullOrWhiteSpace(builder.Query)
            ? string.Empty
            : builder.Query.TrimStart('?') + "&";
        builder.Query = query + "route=speedtest";
        return builder.Uri;
    }

    public static Task<TelemetryDiagnosticReport> ProbeGetAsync(
        HttpClient http,
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        ProbeAsync(
            http,
            HttpMethod.Get,
            BuildLeaderboardProbeUri(endpoint),
            content: null,
            timeout,
            cancellationToken);

    public static Task<TelemetryDiagnosticReport> ProbePostAsync(
        HttpClient http,
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        ProbeAsync(
            http,
            HttpMethod.Post,
            BuildPostProbeUri(endpoint),
            // Deliberately invalid: it exercises doPost and its ContentService
            // redirect without creating a telemetry or leaderboard row.
            content: "{}",
            timeout,
            cancellationToken);

    private static async Task<TelemetryDiagnosticReport> ProbeAsync(
        HttpClient http,
        HttpMethod initialMethod,
        Uri initialUri,
        string? content,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var hops = new List<TelemetryDiagnosticHop>();
        var method = initialMethod;
        var uri = initialUri;

        for (var index = 0; index <= MaximumRedirects; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(method, uri);
                if (content is not null && method != HttpMethod.Get && method != HttpMethod.Head)
                {
                    request.Content = new StringContent(content, Encoding.UTF8, "application/json");
                }

                using var response = await http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                var location = ResolveLocation(uri, response.Headers.Location);
                var isRedirect = IsRedirect(response.StatusCode);
                var hopError = isRedirect && response.Headers.Location is null
                    ? "redirect_missing_location"
                    : null;

                hops.Add(new TelemetryDiagnosticHop(
                    index,
                    method.Method,
                    uri,
                    (int)response.StatusCode,
                    response.Version.ToString(),
                    location,
                    response.Content.Headers.ContentType?.ToString(),
                    stopwatch.Elapsed,
                    Preview(responseBody),
                    hopError,
                    null));

                if (!isRedirect)
                {
                    return new TelemetryDiagnosticReport(true, null, hops);
                }
                if (hopError is not null || location is null)
                {
                    return new TelemetryDiagnosticReport(false, hopError, hops);
                }
                if (index == MaximumRedirects)
                {
                    return new TelemetryDiagnosticReport(false, "redirect_limit", hops);
                }

                method = RedirectMethod(method, response.StatusCode);
                uri = location;
            }
            catch (OperationCanceledException ex)
            {
                var error = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
                hops.Add(FailedHop(index, method, uri, stopwatch.Elapsed, error, ex));
                return new TelemetryDiagnosticReport(false, error, hops);
            }
            catch (HttpRequestException ex)
            {
                var error = TelemetryClient.ClassifyTransportError(ex);
                hops.Add(FailedHop(index, method, uri, stopwatch.Elapsed, error, ex));
                return new TelemetryDiagnosticReport(false, error, hops);
            }
        }

        return new TelemetryDiagnosticReport(false, "redirect_limit", hops);
    }

    private static TelemetryDiagnosticHop FailedHop(
        int index,
        HttpMethod method,
        Uri uri,
        TimeSpan elapsed,
        string error,
        Exception exception) =>
        new(
            index,
            method.Method,
            uri,
            null,
            null,
            null,
            null,
            elapsed,
            null,
            error,
            DescribeException(exception));

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static HttpMethod RedirectMethod(HttpMethod current, HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.SeeOther && current != HttpMethod.Head)
        {
            return HttpMethod.Get;
        }
        if (current == HttpMethod.Post &&
            statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found)
        {
            return HttpMethod.Get;
        }
        return current;
    }

    private static Uri? ResolveLocation(Uri current, Uri? location) =>
        location is null
            ? null
            : location.IsAbsoluteUri
                ? location
                : new Uri(current, location);

    private static string Preview(string body)
    {
        // `backend-diagnose` prints this to Console: never preserve raw terminal
        // controls from an untrusted/HTML/compromised HTTP response.
        var result = new StringBuilder();
        foreach (var rune in body.EnumerateRunes())
        {
            if (result.Length >= BodyPreviewCharacters) break;
            if (rune.Value is '\r' or '\n' or '\t')
            {
                result.Append(' ');
                continue;
            }
            if (Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate)
            {
                continue;
            }
            result.Append(rune.ToString());
        }
        return result.ToString().Trim() + (body.Length > BodyPreviewCharacters ? "…" : string.Empty);
    }

    private static string DescribeException(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add(current.GetType().Name + ": " + Preview(current.Message));
        }
        return string.Join(" -> ", parts);
    }
}
