using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IS74Wifi.Core;

public sealed record TelemetryWriteResult(
    bool Success,
    bool Duplicate,
    string? Error);

public sealed record LeaderboardPublicEntry(
    int Rank,
    string Nickname,
    double? DownloadMbps,
    double? UploadMbps,
    double? LatencyMs,
    double? JitterMs,
    double? PacketLossPct);

public sealed record LeaderboardReadResult(
    bool Success,
    string? Error,
    IReadOnlyList<LeaderboardPublicEntry> Entries);

public sealed class TelemetryClient(
    HttpClient http,
    Uri endpoint,
    DiagnosticLogger? logger = null)
{
    public async Task<TelemetryWriteResult> SendTelemetryBatchAsync(
        TelemetryQueueBatch batch,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var body = BuildBatchEnvelope(batch.BatchId, batch.EventJson);
        // Keep queued uploads on the backward-compatible generic endpoint. The
        // queue can contain a user-triggered speed/leaderboard retry alongside
        // authorization telemetry.
        return await PostAsync("batch", endpoint, body, timeout, cancellationToken).ConfigureAwait(false);
    }

    public Task<TelemetryWriteResult> SubmitSpeedTestAsync(
        TelemetrySpeedTestEvent value,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            "speedtest",
            BuildRouteUri("speedtest"),
            TelemetrySerialization.Serialize(value),
            timeout,
            cancellationToken);

    public Task<TelemetryWriteResult> PublishLeaderboardAsync(
        TelemetryLeaderboardEntry value,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            "leaderboard_publish",
            BuildRouteUri("leaderboard"),
            TelemetrySerialization.Serialize(value),
            timeout,
            cancellationToken);

    public async Task<LeaderboardReadResult> GetLeaderboardAsync(
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var target = BuildRouteUri(
            "leaderboard",
            ("limit", Math.Clamp(limit, 1, 250).ToString(CultureInfo.InvariantCulture)));
        var stopwatch = Stopwatch.StartNew();
        LogStart("leaderboard_read", HttpMethod.Get, target, timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            LogResponse("leaderboard_read", response, stopwatch.Elapsed, body);

            if (!response.IsSuccessStatusCode)
            {
                return new LeaderboardReadResult(false, "http_" + (int)response.StatusCode, []);
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ok", out var okElement) ||
                okElement.ValueKind != JsonValueKind.True)
            {
                return new LeaderboardReadResult(false, ReadError(root), []);
            }

            if (!root.TryGetProperty("entries", out var entriesElement) ||
                entriesElement.ValueKind != JsonValueKind.Array)
            {
                return new LeaderboardReadResult(false, "contract_mismatch", []);
            }

            var entries = new List<LeaderboardPublicEntry>();
            var maximumEntries = Math.Clamp(limit, 1, LeaderboardDisplayPolicy.MaximumEntries);
            foreach (var item in entriesElement.EnumerateArray())
            {
                if (entries.Count >= maximumEntries) break;
                if (item.ValueKind != JsonValueKind.Object) continue;

                // Never let remote strings or unbounded numerical values reach either
                // the rich renderer or the compact Console.WriteLine path.
                var nickname = LeaderboardDisplayPolicy.SanitizeNickname(ReadString(item, "nickname"));
                if (nickname.Length == 0) continue;

                var download = LeaderboardDisplayPolicy.SafeMetric(
                    ReadDouble(item, "download_mbps"), LeaderboardDisplayPolicy.MaximumSpeedMbps);
                var upload = LeaderboardDisplayPolicy.SafeMetric(
                    ReadDouble(item, "upload_mbps"), LeaderboardDisplayPolicy.MaximumSpeedMbps);
                var latency = LeaderboardDisplayPolicy.SafeMetric(
                    ReadDouble(item, "latency_ms"), LeaderboardDisplayPolicy.MaximumLatencyMs);
                var jitter = LeaderboardDisplayPolicy.SafeMetric(
                    ReadDouble(item, "jitter_ms"), LeaderboardDisplayPolicy.MaximumLatencyMs);
                var loss = LeaderboardDisplayPolicy.SafeMetric(
                    ReadDouble(item, "packet_loss_pct"), LeaderboardDisplayPolicy.MaximumPacketLossPct);
                if (download is null && upload is null && latency is null) continue;

                entries.Add(new LeaderboardPublicEntry(
                    LeaderboardDisplayPolicy.SafeRank(ReadInt(item, "rank"), entries.Count + 1),
                    nickname, download, upload, latency, jitter, loss));
            }

            return new LeaderboardReadResult(true, null, entries);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogFailure("leaderboard_read", "cancelled", stopwatch.Elapsed, null);
            return new LeaderboardReadResult(false, "cancelled", []);
        }
        catch (OperationCanceledException ex)
        {
            LogFailure("leaderboard_read", "timeout", stopwatch.Elapsed, ex);
            return new LeaderboardReadResult(false, "timeout", []);
        }
        catch (HttpRequestException ex)
        {
            var error = ClassifyTransportError(ex);
            LogFailure("leaderboard_read", error, stopwatch.Elapsed, ex);
            return new LeaderboardReadResult(false, error, []);
        }
        catch (JsonException ex)
        {
            LogFailure("leaderboard_read", "invalid_response", stopwatch.Elapsed, ex);
            return new LeaderboardReadResult(false, "invalid_response", []);
        }
    }

    public async Task<string?> GetLeaderboardJsonAsync(
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var target = BuildRouteUri(
            "leaderboard",
            ("limit", Math.Clamp(limit, 1, 250).ToString(CultureInfo.InvariantCulture)));
        var stopwatch = Stopwatch.StartNew();
        LogStart("leaderboard_json", HttpMethod.Get, target, timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            LogResponse("leaderboard_json", response, stopwatch.Elapsed, body);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return body;
        }
        catch (OperationCanceledException ex)
        {
            var error = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            LogFailure("leaderboard_json", error, stopwatch.Elapsed, ex);
            return null;
        }
        catch (HttpRequestException ex)
        {
            LogFailure("leaderboard_json", ClassifyTransportError(ex), stopwatch.Elapsed, ex);
            return null;
        }
    }

    private async Task<TelemetryWriteResult> PostAsync(
        string operation,
        Uri target,
        string json,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, target);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var stopwatch = Stopwatch.StartNew();
        LogStart(operation, HttpMethod.Post, target, timeout);

        try
        {
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            LogResponse(operation, response, stopwatch.Elapsed, body);
            if (!response.IsSuccessStatusCode)
            {
                return new TelemetryWriteResult(false, false, "http_" + (int)response.StatusCode);
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var ok = root.ValueKind == JsonValueKind.Object &&
                         root.TryGetProperty("ok", out var okElement) &&
                         okElement.ValueKind is JsonValueKind.True;
                if (!ok)
                {
                    return new TelemetryWriteResult(false, false, ReadError(root));
                }

                var duplicateBatch = root.TryGetProperty("duplicate_batch", out var duplicateElement) &&
                                     duplicateElement.ValueKind == JsonValueKind.True;
                var duplicateEvents = root.TryGetProperty("duplicate_events", out var duplicateEventsElement) &&
                                      duplicateEventsElement.ValueKind == JsonValueKind.Number &&
                                      duplicateEventsElement.TryGetInt32(out var duplicateEventCount) &&
                                      duplicateEventCount > 0;
                return new TelemetryWriteResult(true, duplicateBatch || duplicateEvents, null);
            }
            catch (JsonException ex)
            {
                LogFailure(operation, "invalid_response", stopwatch.Elapsed, ex);
                return new TelemetryWriteResult(false, false, "invalid_response");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogFailure(operation, "cancelled", stopwatch.Elapsed, null);
            return new TelemetryWriteResult(false, false, "cancelled");
        }
        catch (OperationCanceledException ex)
        {
            LogFailure(operation, "timeout", stopwatch.Elapsed, ex);
            return new TelemetryWriteResult(false, false, "timeout");
        }
        catch (HttpRequestException ex)
        {
            var error = ClassifyTransportError(ex);
            LogFailure(operation, error, stopwatch.Elapsed, ex);
            return new TelemetryWriteResult(false, false, error);
        }
    }

    private Uri BuildRouteUri(string route, params (string Key, string Value)[] extra)
    {
        var builder = new UriBuilder(endpoint);
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(builder.Query))
        {
            values.Add(builder.Query.TrimStart('?'));
        }
        values.Add("route=" + Uri.EscapeDataString(route));
        values.AddRange(extra.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        builder.Query = string.Join("&", values);
        return builder.Uri;
    }

    private static string ReadError(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("error", out var errorElement) &&
        errorElement.ValueKind == JsonValueKind.String
            ? SafeBackendError(errorElement.GetString())
            : "rejected";

    private static string SafeBackendError(string? raw) =>
        raw is { Length: > 0 and <= 64 } &&
        raw.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            ? raw
            : "rejected";

    internal static string ClassifyTransportError(HttpRequestException exception) =>
        exception.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "dns",
            HttpRequestError.ConnectionError => "connect",
            HttpRequestError.SecureConnectionError => "tls",
            HttpRequestError.ProxyTunnelError => "proxy",
            HttpRequestError.VersionNegotiationError => "http_version",
            HttpRequestError.UserAuthenticationError => "authentication",
            HttpRequestError.ConfigurationLimitExceeded => "redirect",
            HttpRequestError.HttpProtocolError or
                HttpRequestError.InvalidResponse or
                HttpRequestError.ResponseEnded => "protocol",
            _ => "transport"
        };

    private void LogStart(string operation, HttpMethod method, Uri target, TimeSpan timeout) =>
        logger?.Write(
            DiagnosticLevel.Info,
            $"telemetry.http start operation={operation} method={method.Method} " +
            $"host={target.Host} path={target.AbsolutePath} timeoutMs={timeout.TotalMilliseconds:0}");

    private void LogResponse(
        string operation,
        HttpResponseMessage response,
        TimeSpan elapsed,
        string body)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
        logger?.Write(
            response.IsSuccessStatusCode ? DiagnosticLevel.Info : DiagnosticLevel.Warn,
            $"telemetry.http response operation={operation} status={(int)response.StatusCode} " +
            $"version={response.Version} elapsedMs={elapsed.TotalMilliseconds:0} " +
            $"contentType={contentType} body={BodyPreview(body)}");
    }

    private void LogFailure(
        string operation,
        string error,
        TimeSpan elapsed,
        Exception? exception)
    {
        var requestError = exception is HttpRequestException httpException
            ? httpException.HttpRequestError.ToString()
            : "none";
        var socketError = FindSocketException(exception)?.SocketErrorCode.ToString() ?? "none";
        var exceptionType = exception?.GetType().Name ?? "none";
        var message = exception?.Message ?? "none";
        logger?.Write(
            DiagnosticLevel.Warn,
            $"telemetry.http failure operation={operation} error={error} " +
            $"elapsedMs={elapsed.TotalMilliseconds:0} exception={exceptionType} " +
            $"httpRequestError={requestError} socketError={socketError} message={message}");
    }

    private static SocketException? FindSocketException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketException)
            {
                return socketException;
            }
        }
        return null;
    }

    private static string BodyPreview(string body)
    {
        // A malformed/HTML backend response can contain literal control bytes.
        // Never persist terminal escape codes in a diagnostic preview.
        var preview = new StringBuilder(512);
        foreach (var rune in body.EnumerateRunes())
        {
            if (preview.Length >= 512) break;
            if (rune.Value is '\r' or '\n' or '\t')
            {
                preview.Append(' ');
                continue;
            }

            if (Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate)
            {
                continue;
            }
            preview.Append(rune.ToString());
        }
        return preview.ToString().Trim() + (body.Length > 512 ? "…" : string.Empty);
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? ReadInt(JsonElement value, string name) =>
        value.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out var result)
            ? result
            : null;

    private static double? ReadDouble(JsonElement value, string name) =>
        value.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetDouble(out var result) &&
        double.IsFinite(result)
            ? result
            : null;

    private static string BuildBatchEnvelope(string batchId, IReadOnlyList<string> events)
    {
        var builder = new StringBuilder();
        builder.Append("{\"batch_id\":\"");
        builder.Append(batchId);
        builder.Append("\",\"events\":[");
        for (var i = 0; i < events.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append(events[i]);
        }
        builder.Append("]}");
        return builder.ToString();
    }
}
