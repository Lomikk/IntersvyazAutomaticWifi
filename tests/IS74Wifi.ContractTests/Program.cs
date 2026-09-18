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
    ("is74-api", TestIs74ApiAsync),
    ("captive-portal", TestCaptivePortalAsync),
    ("authorization-flow", AuthorizationFlowContractTests.RunAsync),
    ("agent-policy", AgentContractTests.RunAsync),
    ("cached-dns", DnsContractTests.RunAsync),
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

static async Task TestIs74ApiAsync()
{
    var seen = new List<(string Path, string Method, string Body, string? Authorization, bool NoCache)>();
    using var client = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var auth = request.Headers.TryGetValues("Authorization", out var authValues) ? authValues.Single() : null;
        seen.Add((request.RequestUri!.PathAndQuery, request.Method.Method, body, auth, request.Headers.CacheControl?.NoCache == true));

        return request.RequestUri.AbsolutePath switch
        {
            "/mobile/auth/get-confirm" => JsonResponse("{}"),
            "/mobile/auth/check-confirm" => JsonResponse("{\"authId\":\"auth-123\",\"addresses\":[]}"),
            "/mobile/auth/get-token" => JsonResponse("{\"TOKEN\":\"bearer-xyz\",\"USER_ID\":17,\"PROFILE_ID\":\"p1\",\"ACCESS_BEGIN\":\"2026-09-18 05:35:51\",\"ACCESS_END\":\"2027-09-18 05:35:51\"}"),
            "/mobile/pushtoken/add-with-device-id" => JsonResponse("{}"),
            "/mobile/pushmessages" => JsonResponse("{\"data\":[{\"id\":\"42\",\"subject\":\"notice\",\"push_message\":\"text\"}]}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }));
    var api = new Is74ApiClient(new HttpTransport(client));

    var confirm = await api.RequestConfirmationAsync("9123456789", "device-1");
    Assert(confirm.IsSuccess, "get-confirm success was rejected");

    var checkedCode = await api.CheckConfirmationAsync("9123456789", "123456", "device-1");
    Assert(checkedCode.IsSuccess && checkedCode.Value?.AuthId == "auth-123", "check-confirm authId was not parsed");

    var token = await api.GetTokenAsync("auth-123", "device-1");
    Assert(token.IsSuccess && token.Value?.Token == "bearer-xyz", "get-token TOKEN was not parsed");
    Assert(token.Value?.UserId == "17" && token.Value?.ProfileId == "p1", "get-token identity fields were not preserved");

    var metadata = await api.RegisterDeviceMetadataAsync(
        "bearer-xyz",
        new DeviceMetadataRegistration("device-1", "9123456789", "Windows 11", "TEST-PC"));
    Assert(metadata.IsSuccess, "device metadata success was rejected");

    var baseline = await api.GetBaselineAsync("bearer-xyz", "device-1");
    Assert(baseline.IsSuccess && baseline.Value?.Id == 42, "baseline top ID was not parsed");

    Assert(seen.Any(x => x.Path == "/mobile/auth/get-confirm" && x.Method == "POST" && x.Body.Contains("\"authType\":0", StringComparison.Ordinal)), "get-confirm request contract changed");
    Assert(seen.Any(x => x.Path == "/mobile/auth/check-confirm" && x.Body.Contains("authId=", StringComparison.Ordinal)), "check-confirm empty authId contract changed");
    Assert(seen.Any(x => x.Path == "/mobile/auth/get-token" && x.Body.Contains("uniqueDeviceId=device-1", StringComparison.Ordinal)), "get-token uniqueDeviceId contract changed");
    Assert(seen.Any(x => x.Path == "/mobile/pushtoken/add-with-device-id" && x.Authorization == "Bearer bearer-xyz"), "metadata Bearer header missing");
    Assert(seen.Any(x => x.Path.StartsWith("/mobile/pushmessages?", StringComparison.Ordinal) && x.NoCache), "pushmessages no-cache header missing");

    var rootArrayStatus = PushMessageParser.ParsePage(
        "[{\"id\":100,\"subject\":\"other\",\"push_message\":\"other\"},{\"id\":101,\"subject\":\"Ваш код авторизации\",\"push_message\":\"4321 код авторизации в приложении \\\"Интерсвязь\\\"\"}]",
        out var rootArrayPage);
    Assert(rootArrayStatus == PushPageParseStatus.Success && rootArrayPage.Messages.Count == 2, "root-array push schema was not parsed");
    var candidate = PushMessageParser.FindWifiCodeAfterBaseline(rootArrayPage, 100);
    Assert(candidate == new WifiCodeCandidate("4321", 101), "fresh Wi-Fi code signature was not recognized");
    Assert(PushMessageParser.FindWifiCodeAfterBaseline(rootArrayPage, 101) is null, "baseline freshness rule regressed");

    var objectStatus = PushMessageParser.ParsePage("{\"result\":{\"messages\":[]}}", out var emptyPage);
    Assert(objectStatus == PushPageParseStatus.Success && emptyPage.KnownEmpty, "known empty object push schema was rejected");
    Assert(PushMessageParser.ParsePage("{\"unexpected\":123}", out _) == PushPageParseStatus.UnrecognizedSchema, "unknown push schema was accepted");
    Assert(PushMessageParser.ParsePage("{", out _) == PushPageParseStatus.InvalidJson, "malformed push JSON was not classified");

    using var multipleClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(JsonResponse("{\"authId\":\"a\",\"addresses\":[{},{}]}"))));
    var multiple = await new Is74ApiClient(new HttpTransport(multipleClient))
        .CheckConfirmationAsync("9123456789", "123456", "device-1");
    Assert(multiple.Failure?.Kind == Is74ApiFailureKind.MultipleAddresses, "multiple-address registration was not stopped");

    using var unauthorizedClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
    var unauthorized = await new Is74ApiClient(new HttpTransport(unauthorizedClient))
        .GetBaselineAsync("expired", "device-1");
    Assert(unauthorized.Failure?.Kind == Is74ApiFailureKind.Unauthorized, "HTTP 401 was not terminal bearer-invalid class");

    using var malformedClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(JsonResponse("{"))));
    var malformed = await new Is74ApiClient(new HttpTransport(malformedClient))
        .GetPushMessagesAsync("bearer", "device-1", 1);
    Assert(malformed.Failure?.Kind == Is74ApiFailureKind.InvalidJson, "malformed push JSON lost classification");

    using var dnsClient = new HttpClient(new DelegateHandler((_, _) =>
        throw new HttpRequestException(HttpRequestError.NameResolutionError, "host unknown", null, null)));
    var dns = await new Is74ApiClient(new HttpTransport(dnsClient))
        .GetBaselineAsync("bearer", "device-1");
    Assert(dns.Failure?.Kind == Is74ApiFailureKind.Transport && dns.Failure.TransportFailure == TransportFailureKind.DnsUnavailable,
        "API client lost DNS failure classification");
}

static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json")
};

static async Task TestCaptivePortalAsync()
{
    var seen = new List<(string PathAndQuery, string Method, string Body)>();
    using var happyClient = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        seen.Add((request.RequestUri!.PathAndQuery, request.Method.Method, body));

        if (request.RequestUri.AbsolutePath == "/stepOne")
        {
            return RedirectResponse(HttpStatusCode.Found, "stepTwo?phone=9123456789&isMp=true");
        }
        if (request.RequestUri.AbsolutePath == "/stepTwo")
        {
            var response = RedirectResponse(HttpStatusCode.Found, "stepThree");
            response.Headers.Date = new DateTimeOffset(2026, 9, 18, 11, 0, 0, TimeSpan.Zero);
            return response;
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }));
    var portal = new CaptivePortalClient(new HttpTransport(happyClient));

    var stepOne = await portal.SendStepOneAsync("9123456789");
    Assert(stepOne.IsSuccess && stepOne.Value?.Disposition == StepOneDisposition.StepTwo, "stepOne redirect to stepTwo was rejected");
    Assert(stepOne.Value?.StepTwoUri?.AbsoluteUri == "http://w.is74.ru/stepTwo?phone=9123456789&isMp=true", "relative stepTwo redirect was not resolved against w.is74.ru");
    Assert(seen.Any(x => x.PathAndQuery == "/stepOne" && x.Method == "POST" &&
                         x.Body.Contains("phone=89123456789", StringComparison.Ordinal) &&
                         x.Body.Contains("dial_code=7", StringComparison.Ordinal) &&
                         x.Body.Contains("country_code=ru", StringComparison.Ordinal) &&
                         x.Body.Contains("sendPush=on", StringComparison.Ordinal)),
        "stepOne form contract changed");

    var stepTwo = await portal.SendStepTwoAsync("9123456789", "4321");
    Assert(stepTwo.IsSuccess, "direct stepTwo success redirect was rejected");
    Assert(stepTwo.Value?.ServerDate == new DateTimeOffset(2026, 9, 18, 11, 0, 0, TimeSpan.Zero), "stepTwo server Date was not preserved");
    Assert(seen.Any(x => x.PathAndQuery == "/stepTwo?phone=9123456789&isMp=true" && x.Method == "POST" &&
                         x.Body.Contains("confirmCode=4321", StringComparison.Ordinal) &&
                         x.Body.Contains("phone=9123456789", StringComparison.Ordinal)),
        "direct stepTwo form contract changed");

    using var appLandingClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
        RedirectResponse(HttpStatusCode.Found, "/home/connect/formy_connect/landing/pages/prilozheniye/?utm_source=wifi"))));
    var appLanding = await new CaptivePortalClient(new HttpTransport(appLandingClient)).SendStepOneAsync("9123456789");
    Assert(appLanding.IsSuccess && appLanding.Value?.Disposition == StepOneDisposition.AlreadyAuthorized,
        "observed application landing was not classified as already authorized");

    using var wifiLandingClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
        RedirectResponse(HttpStatusCode.Found, "https://openwifi.is74.ru/home/connect/formy_connect/landing/pages/wifi/"))));
    var wifiLanding = await new CaptivePortalClient(new HttpTransport(wifiLandingClient)).SendStepOneAsync("9123456789");
    Assert(wifiLanding.IsSuccess && wifiLanding.Value?.Disposition == StepOneDisposition.AlreadyAuthorized,
        "observed openwifi landing was not classified as already authorized");

    using var unexpectedRedirectClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
        RedirectResponse(HttpStatusCode.Found, "https://example.test/unexpected"))));
    var unexpectedRedirect = await new CaptivePortalClient(new HttpTransport(unexpectedRedirectClient)).SendStepOneAsync("9123456789");
    Assert(unexpectedRedirect.Failure?.Kind == CaptivePortalFailureKind.UnexpectedRedirect,
        "unknown stepOne redirect was not failed closed");

    using var wrongStepThreeClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
        RedirectResponse(HttpStatusCode.Found, "stepTwo?phone=9123456789&isMp=true"))));
    var wrongStepThree = await new CaptivePortalClient(new HttpTransport(wrongStepThreeClient))
        .SendStepTwoAsync("9123456789", "4321");
    Assert(wrongStepThree.Failure?.Kind == CaptivePortalFailureKind.UnexpectedRedirect,
        "stepTwo accepted a redirect other than stepThree");

    using var httpErrorClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
    var httpError = await new CaptivePortalClient(new HttpTransport(httpErrorClient)).SendStepOneAsync("9123456789");
    Assert(httpError.Failure?.Kind == CaptivePortalFailureKind.HttpStatus && httpError.Failure.StatusCode == 503,
        "stepOne HTTP failure lost its status");

    using var dnsClient = new HttpClient(new DelegateHandler((_, _) =>
        throw new HttpRequestException(HttpRequestError.NameResolutionError, "host unknown", null, null)));
    var dns = await new CaptivePortalClient(new HttpTransport(dnsClient)).SendStepOneAsync("9123456789");
    Assert(dns.Failure?.Kind == CaptivePortalFailureKind.Transport &&
           dns.Failure.TransportFailure == TransportFailureKind.DnsUnavailable &&
           !dns.Failure.SideEffectMayHaveOccurred,
        "DNS failure was incorrectly treated as an ambiguous POST side effect");

    using var ambiguousClient = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }));
    var ambiguous = await new CaptivePortalClient(new HttpTransport(ambiguousClient))
        .SendStepTwoAsync("9123456789", "4321", timeout: TimeSpan.FromMilliseconds(40));
    Assert(ambiguous.Failure?.Kind == CaptivePortalFailureKind.Transport &&
           ambiguous.Failure.TransportFailure == TransportFailureKind.Timeout &&
           ambiguous.Failure.SideEffectMayHaveOccurred,
        "lost stepTwo response was not marked as potentially side-effectful");

    Assert(CaptivePortalClient.BuildDirectStepTwoUri("9123456789").AbsoluteUri ==
           "http://w.is74.ru/stepTwo?phone=9123456789&isMp=true",
        "direct stepTwo URI contract changed");
}

static HttpResponseMessage RedirectResponse(HttpStatusCode status, string location)
{
    var response = new HttpResponseMessage(status);
    response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
    return response;
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
