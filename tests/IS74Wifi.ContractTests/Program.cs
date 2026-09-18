using System.Net;
using IS74Wifi.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("settings-state-device", TestStorageAsync),
    ("dpapi-current-user", TestDpapiAsync),
    ("log-redaction-rotation", TestLoggingAsync),
    ("ssid-policy", TestSsidPolicyAsync),
    ("named-mutex", TestMutexAsync),
    ("http-transport", TestHttpTransportAsync)
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

return 0;

static Task TestStorageAsync()
{
    using var temp = TempDirectory.Create();
    var paths = new AppPaths(temp.Path);
    var json = new JsonFileStore();
    var settings = new SettingsStore(paths, json).Load();

    Assert(settings.AuthWindowHours == 24, "default auth window changed");
    Assert(settings.MaxAutomaticStepOneAttempts == 4, "automatic attempt limit changed");
    Assert(settings.AutomaticRetryDelaysSeconds.SequenceEqual([15, 30, 60]), "retry schedule changed");
    Assert(File.Exists(paths.SettingsFile), "default settings were not persisted");

    var stateStore = new RuntimeStateStore(paths, json);
    var expectedExpiry = DateTimeOffset.UtcNow.AddHours(24);
    var state = new RuntimeState
    {
        LastResult = "success",
        ExpectedExpiryUtc = expectedExpiry,
        AutomaticStepOneAttempts = 2,
        InternetConfirmed = true
    };
    stateStore.Save(state);
    var roundTrip = stateStore.Load();
    Assert(roundTrip.LastResult == "success", "runtime state result did not round-trip");
    Assert(roundTrip.AutomaticStepOneAttempts == 2, "runtime attempt count did not round-trip");
    Assert(roundTrip.InternetConfirmed == true, "runtime internet flag did not round-trip");
    Assert(roundTrip.ExpectedExpiryUtc?.ToUnixTimeMilliseconds() == expectedExpiry.ToUnixTimeMilliseconds(), "runtime timestamp did not round-trip");

    var deviceStore = new DeviceIdentityStore(paths);
    var first = deviceStore.GetOrCreate();
    var second = deviceStore.GetOrCreate();
    Assert(first == second, "device ID is not stable");
    Assert(first.Length == 16 && first.All(Uri.IsHexDigit), "device ID format changed");
    return Task.CompletedTask;
}

static Task TestDpapiAsync()
{
    using var temp = TempDirectory.Create();
    var store = new DpapiSecretStore(new AppPaths(temp.Path));
    var expected = new StoredSecrets("token-value-never-log", "9991234567");
    store.Save(expected);
    var actual = store.Load();

    Assert(actual == expected, "DPAPI CurrentUser round-trip failed");
    var ciphertext = File.ReadAllText(Path.Combine(temp.Path, "secrets.dpapi"));
    Assert(!ciphertext.Contains(expected.Token, StringComparison.Ordinal), "token leaked into DPAPI file");
    Assert(!ciphertext.Contains(expected.Phone, StringComparison.Ordinal), "phone leaked into DPAPI file");
    return Task.CompletedTask;
}

static Task TestLoggingAsync()
{
    using var temp = TempDirectory.Create();
    var paths = new AppPaths(temp.Path);
    var logger = new DiagnosticLogger(paths, maxBytes: 32, retentionFiles: 3);

    logger.Write(DiagnosticLevel.Info,
        "Authorization: Bearer abc.def phone=79991234567 confirmCode=1234 authId=secret\n" +
        "{\"token\":\"json-token\",\"phone\":\"89991234567\"} 4321 код авторизации");
    logger.Write(DiagnosticLevel.Warn, "rotation trigger payload");

    var allLogs = string.Join("\n", Directory.GetFiles(paths.LogDirectory, "diagnostic*.log").Select(File.ReadAllText));
    foreach (var secret in new[] { "abc.def", "79991234567", "89991234567", "json-token", "authId=secret", "confirmCode=1234", "4321 код авторизации" })
    {
        Assert(!allLogs.Contains(secret, StringComparison.Ordinal), $"log leaked secret: {secret}");
    }
    Assert(allLogs.Contains("<redacted>", StringComparison.Ordinal), "redaction marker missing");
    Assert(Directory.GetFiles(paths.LogDirectory, "diagnostic*.log").Length >= 2, "log rotation did not run");
    return Task.CompletedTask;
}

static Task TestSsidPolicyAsync()
{
    Assert(SsidPolicy.IsTarget("Campus Wi-Fi"), "exact campus SSID rejected");
    Assert(SsidPolicy.IsTarget("campus wi-fi 5G"), "case-insensitive campus SSID rejected");
    Assert(!SsidPolicy.IsTarget("Other Wi-Fi"), "foreign SSID accepted");
    Assert(!SsidPolicy.IsTarget(null), "null SSID accepted");
    return Task.CompletedTask;
}

static Task TestMutexAsync()
{
    var name = $"Local\\IS74Wifi.Contract.{Guid.NewGuid():N}";
    using var first = NamedMutexLease.TryAcquire(name);
    Assert(first is not null, "first mutex acquisition failed");
    using var second = NamedMutexLease.TryAcquire(name);
    Assert(second is null, "second mutex acquisition unexpectedly succeeded");
    return Task.CompletedTask;
}

static async Task TestHttpTransportAsync()
{
    using var successClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
    {
        Content = new StringContent("body"),
        Headers = { Location = new Uri("https://example.test/next") }
    })));
    var successTransport = new HttpTransport(successClient);
    using var successRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
    var success = await successTransport.SendAsync(successRequest, TimeSpan.FromSeconds(1));
    Assert(success.TransportSucceeded, "successful transport was classified as failure");
    Assert(success.Response?.StatusCode == HttpStatusCode.Found, "HTTP status was not preserved");
    Assert(success.Response?.Location?.AbsoluteUri == "https://example.test/next", "Location was not preserved");

    using var timeoutClient = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }));
    var timeoutTransport = new HttpTransport(timeoutClient);
    using var timeoutRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
    var timeout = await timeoutTransport.SendAsync(timeoutRequest, TimeSpan.FromMilliseconds(40));
    Assert(timeout.FailureKind == TransportFailureKind.Timeout, "timeout was not mapped to Timeout");

    using var dnsClient = new HttpClient(new DelegateHandler((_, _) =>
        throw new HttpRequestException(HttpRequestError.NameResolutionError, "host unknown", null, null)));
    var dnsTransport = new HttpTransport(dnsClient);
    using var dnsRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
    var dns = await dnsTransport.SendAsync(dnsRequest, TimeSpan.FromSeconds(1));
    Assert(dns.FailureKind == TransportFailureKind.DnsUnavailable, "DNS failure was not mapped to DnsUnavailable");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        handler(request, cancellationToken);
}

sealed class TempDirectory : IDisposable
{
    private TempDirectory(string path) => Path = path;

    public string Path { get; }

    public static TempDirectory Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IS74Wifi-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TempDirectory(path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
