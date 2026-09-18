using System.Diagnostics;
using System.Net;
using System.Text;
using IS74Wifi.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("settings-state-device", TestStorageAsync),
    ("dpapi-current-user", TestDpapiAsync),
    ("log-redaction-rotation", TestLoggingAsync),
    ("ssid-policy", TestSsidPolicyAsync),
    ("windows-wlan", TestWindowsWlanAsync),
    ("named-mutex", TestMutexAsync),
    ("http-transport", TestHttpTransportAsync),
    ("internet-probe", TestInternetProbeAsync)
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
    var paths = new AppPaths(temp.Path);
    var store = new DpapiSecretStore(paths);
    var expected = new StoredSecrets("token-value-never-log", "9991234567");
    store.Save(expected);
    var actual = store.Load();

    Assert(actual == expected, "DPAPI CurrentUser round-trip failed");
    var ciphertext = File.ReadAllText(paths.SecretsFile);
    Assert(!ciphertext.Contains(expected.Token, StringComparison.Ordinal), "token leaked into DPAPI file");
    Assert(!ciphertext.Contains(expected.Phone, StringComparison.Ordinal), "phone leaked into DPAPI file");

    var legacy = new StoredSecrets("legacy-ps-token", "9123456789");
    var legacyJson = $"{{\"token\":\"{legacy.Token}\",\"phone\":\"{legacy.Phone}\"}}";
    RunWindowsPowerShell(
        "$s=ConvertTo-SecureString -String $env:IS74_PLAIN -AsPlainText -Force; " +
        "$c=ConvertFrom-SecureString -SecureString $s; " +
        "[IO.File]::WriteAllText($env:IS74_PATH,$c,[Text.Encoding]::ASCII)",
        new Dictionary<string, string>
        {
            ["IS74_PLAIN"] = legacyJson,
            ["IS74_PATH"] = paths.SecretsFile
        });
    Assert(store.Load() == legacy, "C# could not read PowerShell 5.1 DPAPI secret format");

    var reverse = new StoredSecrets("csharp-token", "9876543210");
    store.Save(reverse);
    var reverseOutput = Path.Combine(temp.Path, "legacy-plaintext.txt");
    RunWindowsPowerShell(
        "$c=[IO.File]::ReadAllText($env:IS74_PATH).Trim(); " +
        "$s=ConvertTo-SecureString -String $c; $p=[IntPtr]::Zero; " +
        "try {$p=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($s); " +
        "$v=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($p); " +
        "[IO.File]::WriteAllText($env:IS74_OUT,$v,[Text.Encoding]::UTF8)} " +
        "finally {if($p -ne [IntPtr]::Zero){[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p)}}",
        new Dictionary<string, string>
        {
            ["IS74_PATH"] = paths.SecretsFile,
            ["IS74_OUT"] = reverseOutput
        });
    var reverseJson = File.ReadAllText(reverseOutput, Encoding.UTF8);
    Assert(reverseJson.Contains(reverse.Token, StringComparison.Ordinal), "PowerShell 5.1 could not read C# DPAPI token");
    Assert(reverseJson.Contains(reverse.Phone, StringComparison.Ordinal), "PowerShell 5.1 could not read C# DPAPI phone");
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

static Task TestWindowsWlanAsync()
{
    var ssids = WindowsWifiService.GetConnectedSsids();
    Assert(ssids.All(ssid => !string.IsNullOrWhiteSpace(ssid)), "WLAN API returned an empty SSID entry");
    return Task.CompletedTask;
}

static async Task TestMutexAsync()
{
    var name = $"Local\\IS74Wifi.Contract.{Guid.NewGuid():N}";
    using var first = NamedMutexLease.TryAcquire(name);
    Assert(first is not null, "first mutex acquisition failed");

    var blockedOnOtherThread = await Task.Run(() =>
    {
        using var second = NamedMutexLease.TryAcquire(name);
        return second is null;
    });

    Assert(blockedOnOtherThread, "second mutex acquisition on another thread unexpectedly succeeded");
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

static async Task TestInternetProbeAsync()
{
    using var onlineClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("Microsoft Connect Test\r\n")
    })));
    var online = await new InternetConnectivityProbe(new HttpTransport(onlineClient))
        .ProbeAsync(TimeSpan.FromSeconds(1));
    Assert(online.Online, "exact Microsoft Connect Test response was rejected");
    Assert(online.HttpResponseReceived, "HTTP response was not recorded");

    using var captiveClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("<html>captive portal</html>")
    })));
    var captive = await new InternetConnectivityProbe(new HttpTransport(captiveClient))
        .ProbeAsync(TimeSpan.FromSeconds(1));
    Assert(!captive.Online, "captive HTML was accepted as Internet access");
    Assert(captive.HttpResponseReceived, "captive HTTP response should remain observable");

    using var dnsClient = new HttpClient(new DelegateHandler((_, _) =>
        throw new HttpRequestException(HttpRequestError.NameResolutionError, "host unknown", null, null)));
    var dns = await new InternetConnectivityProbe(new HttpTransport(dnsClient))
        .ProbeAsync(TimeSpan.FromSeconds(1));
    Assert(!dns.Online && !dns.HttpResponseReceived, "DNS failure was treated as captive HTTP evidence");
    Assert(dns.FailureKind == TransportFailureKind.DnsUnavailable, "probe lost DNS failure classification");
}

static void RunWindowsPowerShell(string command, IReadOnlyDictionary<string, string> environment)
{
    var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    var startInfo = new ProcessStartInfo
    {
        FileName = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"),
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("-NoProfile");
    startInfo.ArgumentList.Add("-NonInteractive");
    startInfo.ArgumentList.Add("-EncodedCommand");
    startInfo.ArgumentList.Add(encodedCommand);
    foreach (var pair in environment)
    {
        startInfo.Environment[pair.Key] = pair.Value;
    }

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Windows PowerShell");
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Assert(process.ExitCode == 0, $"Windows PowerShell DPAPI compatibility probe failed: {error}");
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
