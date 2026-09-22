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
    ("bounded-http-content", TestBoundedHttpContentAsync),
    ("is74-api", TestIs74ApiAsync),
    ("captive-portal", TestCaptivePortalAsync),
    ("authorization-flow", AuthorizationFlowContractTests.RunAsync),
    ("telemetry-local-first", TelemetryContractTests.RunAsync),
    ("speedtest-is74-librespeed", SpeedTestContractTests.RunAsync),
    ("agent-policy", AgentContractTests.RunAsync),
    ("cached-dns", DnsContractTests.RunAsync),
    ("internet-probe", TestInternetProbeAsync),
    ("self-update", UpdateContractTests.RunAsync)
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
    Assert(settings.AnonymousStatisticsConsent == AnonymousStatisticsConsent.Unknown,
        "anonymous statistics consent must default to unknown");
    Assert(settings.MaxAutomaticStepOneAttempts == 4, "automatic attempt limit changed");
    Assert(settings.AutomaticRetryDelaysSeconds.SequenceEqual([15, 30, 60]), "retry schedule changed");
    Assert(File.Exists(paths.SettingsFile), "default settings were not persisted");

    var settingsStore = new SettingsStore(paths, json);
    settingsStore.Save(settings with { AnonymousStatisticsConsent = AnonymousStatisticsConsent.Declined });
    Assert(settingsStore.Load().AnonymousStatisticsConsent == AnonymousStatisticsConsent.Declined,
        "anonymous statistics consent did not persist");

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

    using var oversizedClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[BoundedHttpContent.DefaultBodyLimitBytes + 1]) })));
    using var oversizedRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
    var oversized = await new HttpTransport(oversizedClient).SendAsync(oversizedRequest, TimeSpan.FromSeconds(1));
    Assert(oversized.FailureKind == TransportFailureKind.ResponseTooLarge, "oversized transport body was not rejected");
}

static async Task TestBoundedHttpContentAsync()
{
    using var exact = new ByteArrayContent(new byte[32]);
    Assert((await BoundedHttpContent.ReadBytesAsync(exact, 32)).Length == 32, "exactly-limit body was rejected");

    using var declaredOversized = new ByteArrayContent(new byte[1]);
    declaredOversized.Headers.ContentLength = 33;
    try
    {
        await BoundedHttpContent.ReadBytesAsync(declaredOversized, 32);
        throw new InvalidOperationException("oversized declared Content-Length was accepted");
    }
    catch (ResponseBodyTooLargeException) { }

    // Non-seekable StreamContent cannot advertise Content-Length; the real byte
    // count must still be enforced even for streaming/chunked responses.
    using var unknownLength = new StreamContent(new NonSeekableMemoryStream(new byte[33]));
    Assert(unknownLength.Headers.ContentLength is null, "test body unexpectedly advertised Content-Length");
    try
    {
        await BoundedHttpContent.ReadBytesAsync(unknownLength, 32);
        throw new InvalidOperationException("oversized unknown-length body was accepted");
    }
    catch (ResponseBodyTooLargeException) { }
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
    Assert(seen.Any(x => x.Path == "/mobile/auth/get-token" &&
        x.Body.Contains("uniqueDeviceId=device-1", StringComparison.Ordinal) &&
        x.Body.Contains("userId=", StringComparison.Ordinal) &&
        !x.Body.Contains("userId=17", StringComparison.Ordinal) &&
        !x.Body.Contains("userId=23", StringComparison.Ordinal)),
        "get-token must remain phone/device scoped with an empty userId");
    Assert(seen.Any(x => x.Path == "/mobile/pushtoken/add-with-device-id" && x.Authorization == "Bearer bearer-xyz"), "metadata Bearer header missing");
    Assert(seen.Any(x => x.Path == "/mobile/pushtoken/add-with-device-id" &&
        x.Body.Contains("\"AUTHORIZE_PHONE\":\"9123456789\"", StringComparison.Ordinal) &&
        x.Body.Contains("\"ASSEMBLY_CODE\":2026061111", StringComparison.Ordinal)),
        "device metadata JSON contract changed");
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

    var accountRequests = new List<string>();
    using var multipleClient = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        accountRequests.Add(body);
        return request.RequestUri!.AbsolutePath switch
        {
            "/mobile/auth/check-confirm" => JsonResponse("{\"authId\":\"a\",\"addresses\":[{\"userId\":17,\"address\":\"ул. Первая, 1\"},{\"USER_ID\":23,\"short_address\":\"ул. Вторая, 2\"}]}"),
            "/mobile/auth/get-token" => JsonResponse("{\"TOKEN\":\"phone-scoped-token\",\"USER_ID\":23}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }));
    var multipleApi = new Is74ApiClient(new HttpTransport(multipleClient));
    var multiple = await multipleApi.CheckConfirmationAsync("9123456789", "123456", "device-1");
    Assert(multiple.IsSuccess && multiple.Value?.AuthId == "a",
        "linked account metadata must not change confirmation success");

    var phoneScopedToken = await multipleApi.GetTokenAsync("a", "device-1");
    Assert(phoneScopedToken.IsSuccess && phoneScopedToken.Value?.Token == "phone-scoped-token",
        "phone-scoped token request failed when linked accounts were present");
    Assert(accountRequests.Any(body =>
            body.Contains("userId=", StringComparison.Ordinal) &&
            !body.Contains("userId=17", StringComparison.Ordinal) &&
            !body.Contains("userId=23", StringComparison.Ordinal)),
        "linked account userId leaked into the Campus Wi-Fi token request");

    using var malformedAddressClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
        JsonResponse("{\"authId\":\"a\",\"addresses\":[{\"address\":\"без userId\"}]}"))));
    var malformedAddress = await new Is74ApiClient(new HttpTransport(malformedAddressClient))
        .CheckConfirmationAsync("9123456789", "123456", "device-1");
    Assert(malformedAddress.IsSuccess && malformedAddress.Value?.AuthId == "a",
        "irrelevant address metadata must not break Campus Wi-Fi registration");

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
    Assert(stepOne.Value?.StepTwoUri?.AbsoluteUri == "https://w.is74.ru/stepTwo?phone=9123456789&isMp=true", "relative stepTwo redirect was not resolved against w.is74.ru");
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
           "https://w.is74.ru/stepTwo?phone=9123456789&isMp=true",
        "direct stepTwo URI contract changed");

    var observedUris = new List<Uri>();
    using var httpDowngradeClient = new HttpClient(new DelegateHandler((request, _) =>
    {
        observedUris.Add(request.RequestUri!);
        return Task.FromResult(RedirectResponse(HttpStatusCode.Found,
            request.RequestUri!.AbsolutePath == "/stepOne"
                ? "http://w.is74.ru/stepTwo?phone=9123456789&isMp=true"
                : "http://w.is74.ru/stepThree"));
    }));
    var protectedPortal = new CaptivePortalClient(new HttpTransport(httpDowngradeClient));
    var httpLocation = await protectedPortal.SendStepOneAsync("9123456789");
    Assert(httpLocation.IsSuccess && httpLocation.Value?.StepTwoUri?.Scheme == Uri.UriSchemeHttps,
        "HTTP stepTwo Location was not canonicalized to HTTPS");
    var postedCode = await protectedPortal.SendStepTwoAsync("9123456789", "4321", httpLocation.Value!.StepTwoUri);
    Assert(postedCode.IsSuccess && observedUris.Count == 2 && observedUris.All(uri =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "w.is74.ru"),
        "phone or confirmation code was posted over HTTP after downgrade Location");

    Assert(!PortalRedirectClassifier.TryResolveStepTwo(new Uri("http://evil.example/stepTwo?phone=9123456789"), out _),
        "foreign stepTwo host was accepted");
    Assert(!PortalRedirectClassifier.TryResolveStepTwo(new Uri("http://w.is74.ru:8080/stepTwo?phone=9123456789"), out _),
        "non-default stepTwo port was accepted");
    Assert(!PortalRedirectClassifier.TryResolveStepTwo(new Uri("https://user@w.is74.ru/stepTwo?phone=9123456789"), out _),
        "stepTwo Location with user info was accepted");

    using var tlsFailureClient = new HttpClient(new DelegateHandler((_, _) =>
        throw new HttpRequestException(HttpRequestError.SecureConnectionError, "certificate rejected", null, null)));
    var tlsFailure = await new CaptivePortalClient(new HttpTransport(tlsFailureClient)).SendStepOneAsync("9123456789");
    Assert(tlsFailure.Failure?.TransportFailure == TransportFailureKind.TlsFailure &&
           !tlsFailure.Failure.SideEffectMayHaveOccurred,
        "TLS failure was not classified as pre-POST transport failure");

    using var oversizedRedirectClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("stepTwo?phone=9123456789&isMp=true", UriKind.Relative) },
            Content = new ByteArrayContent(new byte[BoundedHttpContent.DefaultBodyLimitBytes + 1])
        })));
    var skippedBody = await new CaptivePortalClient(new HttpTransport(oversizedRedirectClient)).SendStepOneAsync("9123456789");
    Assert(skippedBody.IsSuccess, "stepOne redirect read an unnecessary oversized response body");
}

static HttpResponseMessage RedirectResponse(HttpStatusCode status, string location)
{
    var response = new HttpResponseMessage(status);
    response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
    return response;
}

static async Task TestInternetProbeAsync()
{
    using var onlineClient = new HttpClient(new DelegateHandler((request, _) =>
    {
        Assert(request.RequestUri == InternetConnectivityProbe.CanonicalUri,
            "SUSU probe URI changed");
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri("https://online.susu.ru/");
        return Task.FromResult(response);
    }));
    var online = await new InternetConnectivityProbe(new HttpTransport(onlineClient))
        .ProbeAsync(TimeSpan.FromSeconds(1));
    Assert(online.Online, "validated SUSU HTTP->HTTPS redirect was rejected");
    Assert(online.HttpResponseReceived, "HTTP response was not recorded");
    Assert(online.Body is null, "timing-critical SUSU probe unexpectedly retained a response body");

    using var captiveClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("<html><script>window.location.href='http://w.is74.ru/'</script></html>")
    })));
    var captive = await new InternetConnectivityProbe(new HttpTransport(captiveClient))
        .ProbeAsync(TimeSpan.FromSeconds(1));
    Assert(!captive.Online, "InterSvyaz captive HTTP 200 was accepted as Internet access");
    Assert(captive.HttpResponseReceived, "captive HTTP response should remain observable");

    using var wrongRedirectClient = new HttpClient(new DelegateHandler((_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri("http://w.is74.ru/");
        return Task.FromResult(response);
    }));
    var wrongRedirect = await new InternetConnectivityProbe(new HttpTransport(wrongRedirectClient))
        .ProbeAsync(TimeSpan.FromSeconds(1));
    Assert(!wrongRedirect.Online, "unexpected redirect target was accepted as Internet access");

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

sealed class NonSeekableMemoryStream(byte[] bytes) : MemoryStream(bytes)
{
    public override bool CanSeek => false;
}
