using System.Text;
using System.Text.Json;

namespace IS74Wifi.Core;

public sealed record TelemetryWriteResult(
    bool Success,
    bool Duplicate,
    string? Error);

public sealed class TelemetryClient(HttpClient http, Uri endpoint)
{
    public async Task<TelemetryWriteResult> SendTelemetryBatchAsync(
        TelemetryQueueBatch batch,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var body = BuildBatchEnvelope(batch.BatchId, batch.EventJson);
        return await PostAsync("telemetry", body, timeout, cancellationToken).ConfigureAwait(false);
    }

    public Task<TelemetryWriteResult> SubmitSpeedTestAsync(
        TelemetrySpeedTestEvent value,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            "speedtest",
            TelemetrySerialization.Serialize(value),
            timeout,
            cancellationToken);

    public Task<TelemetryWriteResult> PublishLeaderboardAsync(
        TelemetryLeaderboardEntry value,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            "leaderboard",
            TelemetrySerialization.Serialize(value),
            timeout,
            cancellationToken);

    public async Task<string?> GetLeaderboardJsonAsync(
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var response = await http.GetAsync(
            BuildRouteUri("leaderboard", ("limit", Math.Clamp(limit, 1, 250).ToString())),
            timeoutCts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private async Task<TelemetryWriteResult> PostAsync(
        string route,
        string json,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildRouteUri(route));
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
                    var error = root.ValueKind == JsonValueKind.Object &&
                                root.TryGetProperty("error", out var errorElement) &&
                                errorElement.ValueKind == JsonValueKind.String
                        ? errorElement.GetString()
                        : "rejected";
                    return new TelemetryWriteResult(false, false, error);
                }

                var duplicate = root.TryGetProperty("duplicate_batch", out var duplicateElement) &&
                                duplicateElement.ValueKind == JsonValueKind.True;
                return new TelemetryWriteResult(true, duplicate, null);
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
