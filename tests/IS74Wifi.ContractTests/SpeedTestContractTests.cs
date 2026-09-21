using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using IS74Wifi.Core;

internal static class SpeedTestContractTests
{
    public static async Task RunAsync()
    {
        var requests = new ConcurrentQueue<(string Method, string PathAndQuery)>();
        var downloadPayload = new byte[16 * 1024];
        Array.Fill<byte>(downloadPayload, 0x5a);

        using var client = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            requests.Enqueue((request.Method.Method, request.RequestUri!.PathAndQuery));

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/backend/empty.php")
            {
                await Task.Delay(1, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/backend/garbage.php")
            {
                await Task.Delay(1, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(downloadPayload)
                };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/backend/empty.php")
            {
                if (request.Content is not null)
                {
                    await request.Content.CopyToAsync(Stream.Null, cancellationToken);
                }
                await Task.Delay(1, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
                {
                    Content = new ByteArrayContent([])
                };
            }

            throw new InvalidOperationException("unexpected speed-test request: " + request.RequestUri);
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        var options = new Is74SpeedTestOptions
        {
            BaseUri = new Uri("https://speed.example.test/"),
            PingCount = 4,
            MaxPingRetries = 1,
            DownloadStreams = 2,
            UploadStreams = 1,
            MultistreamDelay = TimeSpan.FromMilliseconds(2),
            InterPhaseDelay = TimeSpan.FromMilliseconds(1),
            DownloadGraceTime = TimeSpan.FromMilliseconds(20),
            UploadGraceTime = TimeSpan.FromMilliseconds(20),
            MaxThroughputDuration = TimeSpan.FromMilliseconds(80),
            SampleInterval = TimeSpan.FromMilliseconds(10),
            UploadRequestBytes = 16 * 1024,
            AutoShortenFastTests = false
        };

        var updates = new ConcurrentQueue<SpeedTestProgress>();
        var provider = new Is74SpeedTestProvider(client, options);
        var result = await provider.MeasureAsync(new InlineProgress<SpeedTestProgress>(updates.Enqueue));

        Assert(result.Provider == Is74SpeedTestProvider.ProviderName, "speed-test provider identity changed");
        Assert(result.TestVersion == Is74SpeedTestProvider.TestVersion, "speed-test version changed");
        Assert(result.TestScope == "regional" && result.ServerKind == "regional_provider",
            "IS74 result must remain classified as regional provider traffic");
        Assert(result.DownloadMbps > 0 && result.UploadMbps > 0, "throughput test produced no speed");
        Assert(result.DownloadBytes > 0 && result.UploadBytes > 0, "throughput byte counters were not populated");
        Assert(result.LatencyMs >= 1 && result.JitterMs >= 0, "latency/jitter result is invalid");
        Assert(result.LatencySampleCount == 3, "first ping must stay warm-up-only");
        Assert(result.PacketLossPct is null, "LibreSpeed capture does not support packet-loss measurement");

        var captured = requests.ToArray();
        Assert(captured.Count(item => item.Method == "GET" && item.PathAndQuery.StartsWith("/backend/empty.php?", StringComparison.Ordinal)) == 4,
            "latency request count no longer matches configured LibreSpeed ping count");
        Assert(captured.Any(item => item.Method == "GET" && item.PathAndQuery.Contains("/backend/garbage.php?", StringComparison.Ordinal) && item.PathAndQuery.Contains("ckSize=100", StringComparison.Ordinal)),
            "download endpoint or ckSize contract changed");
        Assert(captured.Any(item => item.Method == "POST" && item.PathAndQuery.StartsWith("/backend/empty.php?", StringComparison.Ordinal)),
            "upload endpoint contract changed");
        Assert(!captured.Any(item => item.PathAndQuery.Contains("getIP.php", StringComparison.OrdinalIgnoreCase) || item.PathAndQuery.Contains("telemetry.php", StringComparison.OrdinalIgnoreCase)),
            "native speed test must not submit provider IP/telemetry requests");

        Assert(updates.Any(update => update.Stage == SpeedTestStage.Latency), "latency progress missing");
        Assert(updates.Any(update => update.Stage == SpeedTestStage.Download && update.Mbps > 0), "download progress missing");
        Assert(updates.Any(update => update.Stage == SpeedTestStage.Upload && update.Mbps > 0), "upload progress missing");
        Assert(updates.Last().Stage == SpeedTestStage.Completed, "completion progress missing");

        var telemetry = SpeedTestTelemetry.CreateEvent(
            result,
            "0123456789abcdef0123456789abcdef",
            "0.0.0-test",
            new SpeedTestRadioSnapshot(
                WifiSignalBucket: "good",
                WifiBand: "5ghz",
                ConnectionType: "campus_wifi",
                LinkRxMbps: 866.7,
                LinkTxMbps: 866.7,
                WifiProtocol: "802.11ac"),
            new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.FromHours(5)));

        using var telemetryJson = JsonDocument.Parse(TelemetrySerialization.Serialize(telemetry));
        var root = telemetryJson.RootElement;
        Assert(root.GetProperty("event_type").GetString() == "speed_test", "speed telemetry event type changed");
        Assert(root.GetProperty("test_scope").GetString() == "regional", "speed telemetry scope changed");
        Assert(root.GetProperty("server_kind").GetString() == "regional_provider", "speed telemetry server kind changed");
        Assert(root.GetProperty("time_bucket").GetString() == "evening", "speed telemetry time bucket mapping changed");
        Assert(root.GetProperty("sample_count").GetInt32() == 3, "speed telemetry latency sample count changed");
        Assert(!root.TryGetProperty("packet_loss_pct", out _), "unmeasured packet loss must serialize as null/absent, not zero");

        await TestGenericTelemetryPostContractAsync(telemetry);
    }

    private static async Task TestGenericTelemetryPostContractAsync(TelemetrySpeedTestEvent telemetry)
    {
        var seen = new List<(string Method, Uri Uri, string? Body)>();
        using var client = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            seen.Add((request.Method.Method, request.RequestUri!, body));

            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"ok\":true,\"sort\":\"download_mbps_desc\",\"total\":1,\"entries\":[{" +
                        "\"rank\":1,\"nickname\":\"campus-cat\",\"download_mbps\":84.3," +
                        "\"upload_mbps\":52.1,\"latency_ms\":11.2,\"jitter_ms\":4.1," +
                        "\"packet_loss_pct\":null}]}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"accepted\":1,\"duplicate_batch\":false}")
            };
        }));

        var telemetryClient = new TelemetryClient(client, new Uri("https://script.example.test/exec?deployment=test"));
        var write = await telemetryClient.SubmitSpeedTestAsync(telemetry, TimeSpan.FromSeconds(1));

        Assert(write.Success, "routed Apps Script speed-test POST was rejected by client parser");
        var speedRequest = seen.Single(item => item.Method == "POST");
        Assert(speedRequest.Uri.Query.Contains("deployment=test", StringComparison.Ordinal) &&
               speedRequest.Uri.Query.Contains("route=speedtest", StringComparison.Ordinal),
            "speed-test POST must preserve deployment query and add route=speedtest");
        Assert(speedRequest.Body is not null && speedRequest.Body.Contains("\"event_type\":\"speed_test\"", StringComparison.Ordinal),
            "speed-test payload was not posted to the routed receiver");

        var leaderboard = new TelemetryLeaderboardEntry
        {
            EntryId = "leader-0123456789abcdef",
            InstallId = "0123456789abcdef0123456789abcdef",
            TestId = telemetry.TestId,
            AppVersion = "0.0.0-test",
            EventId = "event-0123456789abcdef",
            Nickname = "campus-cat",
            DownloadMbps = telemetry.DownloadMbps,
            UploadMbps = telemetry.UploadMbps,
            LatencyMs = telemetry.LatencyMs,
            JitterMs = telemetry.JitterMs,
            WifiSignalBucket = "good",
            WifiBand = "5ghz",
            TimeBucket = "evening"
        };

        using var leaderboardJson = JsonDocument.Parse(TelemetrySerialization.Serialize(leaderboard));
        Assert(leaderboardJson.RootElement.GetProperty("wifi_signal_bucket").GetString() == "good",
            "leaderboard payload is missing backend-required Wi-Fi signal bucket");
        Assert(leaderboardJson.RootElement.GetProperty("wifi_band").GetString() == "5ghz",
            "leaderboard payload is missing backend-required Wi-Fi band");
        Assert(leaderboardJson.RootElement.GetProperty("time_bucket").GetString() == "evening",
            "leaderboard payload is missing backend-required time bucket");

        var publish = await telemetryClient.PublishLeaderboardAsync(leaderboard, TimeSpan.FromSeconds(1));
        Assert(publish.Success, "leaderboard POST was rejected by client parser");
        Assert(seen.Any(item => item.Method == "POST" && item.Uri.Query.Contains("route=leaderboard", StringComparison.Ordinal)),
            "leaderboard publication must use route=leaderboard");

        var board = await telemetryClient.GetLeaderboardAsync(50, TimeSpan.FromSeconds(1));
        Assert(board.Success && board.Entries.Count == 1, "public leaderboard response was not parsed");
        Assert(board.Entries[0].Nickname == "campus-cat" && Math.Abs(board.Entries[0].DownloadMbps!.Value - 84.3) < 0.01,
            "public leaderboard fields changed");
        Assert(seen.Any(item => item.Method == "GET" &&
                                item.Uri.Query.Contains("route=leaderboard", StringComparison.Ordinal) &&
                                item.Uri.Query.Contains("limit=50", StringComparison.Ordinal)),
            "leaderboard read must use the public route with a bounded limit");

        var batch = new TelemetryQueueBatch(
            "batch-0123456789abcdef",
            [TelemetrySerialization.Serialize(telemetry)],
            []);
        var batchWrite = await telemetryClient.SendTelemetryBatchAsync(batch, TimeSpan.FromSeconds(1));
        Assert(batchWrite.Success, "generic queued batch POST was rejected by client parser");
        var queuedRequest = seen.Last(item => item.Method == "POST");
        Assert(queuedRequest.Uri.Query == "?deployment=test",
            "queued telemetry must stay on the backward-compatible generic POST route");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
