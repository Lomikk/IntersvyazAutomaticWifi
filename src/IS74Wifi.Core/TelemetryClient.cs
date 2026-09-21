using System.Globalization;
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

public sealed class TelemetryClient(HttpClient http, Uri endpoint)
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
        return await PostAsync(endpoint, body, timeout, cancellationToken).ConfigureAwait(false);
    }

    public Task<TelemetryWriteResult> SubmitSpeedTestAsync(
        TelemetrySpeedTestEvent value,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            BuildRouteUri("speedtest"),
            TelemetrySerialization.Serialize(value),
            timeout,
            cancellationToken);

    public Task<TelemetryWriteResult> PublishLeaderboardAsync(
        TelemetryLeaderboardEntry value,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        PostAsync(
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

        try
        {
            using var response = await http.GetAsync(
                BuildRouteUri("leaderboard", ("limit", Math.Clamp(limit, 1, 250).ToString(CultureInfo.InvariantCulture))),
                timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new LeaderboardReadResult(false, "http_" + (int)response.StatusCode, []);
            }

            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
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
                return new LeaderboardReadResult(false, "invalid_response", []);
            }

            var entries = new List<LeaderboardPublicEntry>();
            foreach (var item in entriesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var nickname = ReadString(item, "nickname");
                if (string.IsNullOrWhiteSpace(nickname))
                {
                    continue;
                }

                entries.Add(new LeaderboardPublicEntry(
                    ReadInt(item, "rank") ?? entries.Count + 1,
                    nickname,
                    ReadDouble(item, "download_mbps"),
                    ReadDouble(item, "upload_mbps"),
                    ReadDouble(item, "latency_ms"),
                    ReadDouble(item, "jitter_ms"),
                    ReadDouble(item, "packet_loss_pct")));
            }

            return new LeaderboardReadResult(true, null, entries);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new LeaderboardReadResult(false, "cancelled", []);
        }
        catch (OperationCanceledException)
        {
            return new LeaderboardReadResult(false, "timeout", []);
        }
        catch (HttpRequestException)
        {
            return new LeaderboardReadResult(false, "transport", []);
        }
        catch (JsonException)
        {
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
        try
        {
            using var response = await http.GetAsync(
                BuildRouteUri("leaderboard", ("limit", Math.Clamp(limit, 1, 250).ToString(CultureInfo.InvariantCulture))),
                timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<TelemetryWriteResult> PostAsync(
        Uri target,
        string json,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, target);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
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
            catch (JsonException)
            {
                return new TelemetryWriteResult(false, false, "invalid_response");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TelemetryWriteResult(false, false, "cancelled");
        }
        catch (OperationCanceledException)
        {
            return new TelemetryWriteResult(false, false, "timeout");
        }
        catch (HttpRequestException)
        {
            return new TelemetryWriteResult(false, false, "transport");
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
            ? errorElement.GetString() ?? "rejected"
            : "rejected";

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
