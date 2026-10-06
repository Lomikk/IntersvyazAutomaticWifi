using System.Net;
using System.Text.Json;
using IS74Wifi.Core;

internal static class BackendWireContractTests
{
    public static async Task RunAsync()
    {
        using var samples = JsonDocument.Parse(await FixtureAsync("backend-events.json"));
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        foreach (var sample in samples.RootElement.EnumerateArray())
        {
            var wire = sample.GetProperty("event_type").GetString() switch
            {
                "mailbox_poll" => TelemetrySerialization.Serialize(sample.Deserialize<TelemetryMailboxPollEvent>(options)!),
                "internet_probe" => TelemetrySerialization.Serialize(sample.Deserialize<TelemetryInternetProbeEvent>(options)!),
                "portal_response" => TelemetrySerialization.Serialize(sample.Deserialize<TelemetryPortalResponseEvent>(options)!),
                "registration_event" => TelemetrySerialization.Serialize(sample.Deserialize<TelemetryRegistrationEvent>(options)!),
                "path_observation" => TelemetrySerialization.Serialize(sample.Deserialize<TelemetryPathObservationEvent>(options)!),
                "error" => TelemetrySerialization.Serialize(sample.Deserialize<TelemetryErrorEvent>(options)!),
                "leaderboard_entry" => TelemetrySerialization.Serialize(sample.Deserialize<TelemetryLeaderboardEntry>(options)!),
                _ => throw new InvalidOperationException("unhandled wire fixture")
            };
            using var actual = JsonDocument.Parse(wire);
            Assert(JsonElement.DeepEquals(sample, actual.RootElement), "client/server event fixture diverged");
        }

        var rateJson = await FixtureAsync("backend-rate-limited.json");
        var reply = rateJson;
        using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply) })));
        var client = new TelemetryClient(http, new Uri("https://backend.example.test/exec"));
        var timeout = TimeSpan.FromSeconds(2);
        var batch = new TelemetryQueueBatch("batch-wire-0001", [], []);
        var write = await client.SendTelemetryBatchAsync(batch, timeout);
        var read = await client.GetLeaderboardAsync(50, timeout);
        var control = await client.GetLeaderboardStatusAsync("install-wire-00000001", timeout);
        Assert(!write.Success && write.Error == "rate_limited" && write.RetryAfterSeconds == 60,
            "write rate-limit envelope mismatch");
        Assert(!read.Success && read.Error == "rate_limited" && read.RetryAfterSeconds == 60,
            "read rate-limit envelope mismatch");
        Assert(!control.Success && control.Error == "rate_limited" && control.RetryAfterSeconds == 60,
            "control rate-limit envelope mismatch");

        foreach (var (value, expected) in new (string Value, int? Expected)[]
        {
            ("null", null), ("-1", null), ("0", null), ("1.5", null),
            ("\"60\"", null), ("2147483647", 21600), ("9999999999999999", null), ("2", 2)
        })
        {
            reply = "{\"ok\":false,\"error\":\"rate_limited\",\"retry_after_seconds\":" + value + "}";
            Assert((await client.SendTelemetryBatchAsync(batch, timeout)).RetryAfterSeconds == expected,
                "untrusted retry delay must be validated and bounded");
        }
        reply = "{\"ok\":false,\"error\":\"rate_limited\"}";
        Assert((await client.SendTelemetryBatchAsync(batch, timeout)).RetryAfterSeconds is null,
            "older receiver without retry delay is no longer supported");
    }

    private static Task<string> FixtureAsync(string name) =>
        File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
