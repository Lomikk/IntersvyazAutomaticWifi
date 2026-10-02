using System.Net;
using System.Security.Cryptography;
using System.Text;
using IS74Wifi.Core;
using IS74Wifi.App;

internal static class UpdateContractTests
{
    public static async Task RunAsync()
    {
        TestSemanticVersions();
        TestProgramInstallation();
        TestInstalledAppRegistration();
        TestUpdatePolicy();
        TestUpdateStateStore();
        TestChecksumParser();
        await TestManifestPrimaryAndChannelSelectionAsync();
        await TestManifestEmptyStableChannelAsync();
        await TestManifestFallbackAsync();
        await TestInvalidManifestRejectedAsync();
        await TestRateLimitPersistenceAndSuppressionAsync();
        await TestGitHub429RetryAfterAsync();
        await TestManifestChecksumMismatchAsync();
        await TestReleaseChannelSelectionAsync();
        await TestDirectExecutableSelectionAndVerifiedDownloadAsync();
        await TestOversizedReleaseAndPackageAsync();
    }

    private static void TestSemanticVersions()
    {
        Assert(SemanticVersion.TryParse("v0.1.0-alpha.8", out var alpha8), "alpha.8 version did not parse");
        Assert(SemanticVersion.TryParse("v0.1.0-alpha.10", out var alpha10), "alpha.10 version did not parse");
        Assert(SemanticVersion.TryParse("v0.1.0-alpha.11", out var alpha11), "alpha.11 version did not parse");
        Assert(SemanticVersion.TryParse("v0.1.0", out var stable), "stable version did not parse");
        Assert(alpha10.CompareTo(alpha8) > 0, "alpha ordering is lexical instead of numeric");
        Assert(alpha11.CompareTo(alpha10) > 0, "alpha.11 did not sort after alpha.10");
        Assert(stable.CompareTo(alpha11) > 0, "stable release did not sort after prerelease");
        Assert(!SemanticVersion.TryParse("alpha.9", out _), "invalid version was accepted");
    }

    private static void TestProgramInstallation()
    {
        using var temp = TempDirectory.Create();
        var source = Path.Combine(temp.Path, "downloaded.exe");
        File.WriteAllText(source, "binary-placeholder");

        var local = Path.Combine(temp.Path, "local-app-data");
        var installation = new ProgramInstallation(local);
        var installed = installation.InstallFrom(source, "v0.1.0-alpha.9");

        Assert(installed == Path.Combine(local, "Programs", "IS74Wifi", "IS74Wifi.exe"), "installed executable path changed");
        Assert(File.Exists(installed), "program was not copied to the per-user install directory");
        Assert(installation.ReadInstalledVersion() == "v0.1.0-alpha.9", "installed version marker was not written");
        Assert(installation.IsInstalledExecutable(installed), "installed executable path was not recognized");

        try
        {
            installation.InstallFrom(source, "v0.1.0-alpha.8");
            throw new InvalidOperationException("older build overwrote a newer installed version");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("новее", StringComparison.Ordinal))
        {
        }
    }


    private static void TestInstalledAppRegistration()
    {
        var executable = Path.Combine("C:\\Users", "Test User", "AppData", "Local", "Programs", "IS74Wifi", "IS74Wifi.exe");
        Assert(WindowsInstalledAppRegistration.BuildUninstallCommand(executable) ==
               $"\"{Path.GetFullPath(executable)}\" uninstall",
            "Windows uninstall command does not target the canonical installed EXE");
        Assert(WindowsInstalledAppRegistration.ToDisplayVersion("v0.1.0-alpha.11") == "0.1.0-alpha.11",
            "Windows display version normalization changed");

        if (!OperatingSystem.IsWindows()) return;

        using var temp = TempDirectory.Create();
        var source = Path.Combine(temp.Path, "downloaded.exe");
        File.WriteAllText(source, "binary-placeholder");
        var installation = new ProgramInstallation(Path.Combine(temp.Path, "local-app-data"));
        installation.InstallFrom(source, "v0.1.0-alpha.11");

        var registration = new WindowsInstalledAppRegistration("IS74WifiContract-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert(registration.Reconcile(installation, "v0.1.0-alpha.11"),
                "missing installed-app registration was not created");
            Assert(registration.IsRegisteredFor(installation, "v0.1.0-alpha.11"),
                "installed-app registration did not point at the canonical install");
            Assert(!registration.Reconcile(installation, "v0.1.0-alpha.11"),
                "already-canonical installed-app registration was rewritten");

            installation.DeleteInstalledFilesIfNotRunning(Environment.ProcessPath);
            Assert(registration.Reconcile(installation, "v0.1.0-alpha.11"),
                "stale installed-app registration was not removed after the install disappeared");
            Assert(!registration.IsRegisteredFor(installation, "v0.1.0-alpha.11"),
                "stale installed-app registration remained after reconciliation");
        }
        finally
        {
            registration.Unregister();
        }
    }

    private static void TestUpdatePolicy()
    {
        var defaultSettings = new AppSettings();
        Assert(UpdatePolicy.IncludePrereleases(defaultSettings, "v0.1.0-alpha.18"),
            "alpha build stopped following prereleases before the user chose a channel");
        Assert(!UpdatePolicy.IncludePrereleases(defaultSettings, "v0.1.0"),
            "stable build followed prereleases before the user chose a channel");
        Assert(!UpdatePolicy.IncludePrereleases(
                defaultSettings with { IncludePrereleaseUpdates = false },
                "v0.1.0-alpha.18"),
            "explicit stable-only channel was ignored on an alpha build");
        Assert(UpdatePolicy.IncludePrereleases(
                defaultSettings with { IncludePrereleaseUpdates = true },
                "v0.1.0"),
            "explicit prerelease channel was ignored on a stable build");

        Assert(UpdatePolicy.CheckInterval == TimeSpan.FromHours(6),
            "background update check interval changed");
        Assert(UpdatePolicy.FailureBackoff(1) == TimeSpan.FromMinutes(15),
            "first update failure backoff changed");
        Assert(UpdatePolicy.FailureBackoff(4) == TimeSpan.FromHours(2),
            "update failure backoff cap changed");
    }

    private static void TestUpdateStateStore()
    {
        using var temp = TempDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var store = new UpdateStateStore(paths, new JsonFileStore());
        var expected = new UpdateState
        {
            LastCheckedUtc = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
            NextCheckUtc = new DateTimeOffset(2026, 9, 21, 18, 0, 0, TimeSpan.Zero),
            AvailableVersion = "v0.1.0-alpha.19",
            AvailableReleasePageUrl = "https://github.test/releases/alpha19",
            LastNotifiedVersion = "v0.1.0-alpha.19",
            PendingInstalledNotificationVersion = "v0.1.0-alpha.18",
            GitHubRateLimitResetUtc = new DateTimeOffset(2026, 9, 21, 19, 0, 0, TimeSpan.Zero),
            ConsecutiveFailures = 2,
            LastError = "HttpRequestException"
        };

        store.Save(expected);
        var actual = store.Load();
        Assert(actual == expected, "update state did not round-trip");
    }

    private static void TestChecksumParser()
    {
        var hash = new string('a', 64);
        var parsed = GitHubUpdateClient.ParseChecksum($"{hash}  IS74Wifi-v0.1.0-alpha.9-win-x64.exe\n", "IS74Wifi-v0.1.0-alpha.9-win-x64.exe");
        Assert(parsed == hash, "valid checksum did not parse");

        try
        {
            GitHubUpdateClient.ParseChecksum($"{hash}  other.exe", "IS74Wifi-v0.1.0-alpha.9-win-x64.exe");
            throw new InvalidOperationException("checksum for a different asset was accepted");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static async Task TestManifestPrimaryAndChannelSelectionAsync()
    {
        var hash = new string('a', 64);
        var githubCalls = 0;
        using var client = new HttpClient(new UpdateHandler(request =>
        {
            if (request.RequestUri!.Host == "script.example.test")
            {
                Assert(request.RequestUri.Query.Contains("route=update", StringComparison.Ordinal),
                    "manifest request lost the update route");
                if (request.RequestUri.Query.Contains("channel=stable", StringComparison.Ordinal))
                {
                    return TextResponse(ManifestJson("stable", "v0.1.0", hash), "application/json");
                }
                return TextResponse(ManifestJson("prerelease", "v0.1.1-alpha.1", hash), "application/json");
            }
            if (request.RequestUri.Host == "api.github.com")
            {
                githubCalls++;
                return TextResponse("[]", "application/json");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var updater = new UpdateClient(client, new Uri("https://script.example.test/exec?deployment=test"));

        var stable = await updater.CheckForUpdateAsync("v0.1.0-alpha.26", includePrerelease: false);
        Assert(stable.Source == UpdateMetadataSource.Manifest && stable.Descriptor?.TagName == "v0.1.0",
            "stable manifest channel did not preserve release selection");

        var prerelease = await updater.CheckForUpdateAsync("v0.1.0", includePrerelease: true);
        Assert(prerelease.Source == UpdateMetadataSource.Manifest && prerelease.Descriptor?.TagName == "v0.1.1-alpha.1",
            "prerelease manifest channel did not preserve release selection");

        var current = await updater.CheckForUpdateAsync("v0.1.1-alpha.1", includePrerelease: true);
        Assert(current.Source == UpdateMetadataSource.Manifest && current.Descriptor is null,
            "current manifest version was offered as an update");
        Assert(githubCalls == 0, "valid manifest still consumed anonymous GitHub API budget");
    }

    private static async Task TestManifestEmptyStableChannelAsync()
    {
        var githubCalls = 0;
        using var client = new HttpClient(new UpdateHandler(request =>
        {
            if (request.RequestUri!.Host == "script.example.test")
                return TextResponse("{\"channel\":\"stable\",\"available\":false}", "application/json");
            if (request.RequestUri.Host == "api.github.com")
            {
                githubCalls++;
                return TextResponse("[]", "application/json");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var updater = new UpdateClient(client, new Uri("https://script.example.test/exec"));
        var result = await updater.CheckForUpdateAsync("v0.1.0-alpha.26", includePrerelease: false);
        Assert(result.Source == UpdateMetadataSource.Manifest && result.Descriptor is null,
            "empty stable manifest channel did not behave like no available release");
        Assert(githubCalls == 0, "empty but valid stable manifest unexpectedly used GitHub fallback");
    }

    private static async Task TestManifestFallbackAsync()
    {
        var githubCalls = 0;
        var releasesJson = """
        [
          {
            "tag_name":"v0.1.0-alpha.27",
            "draft":false,
            "prerelease":true,
            "html_url":"https://github.test/releases/alpha27",
            "assets":[
              {"name":"IS74Wifi-v0.1.0-alpha.27-win-x64.exe","browser_download_url":"https://download.test/alpha27.exe"},
              {"name":"IS74Wifi-v0.1.0-alpha.27-win-x64.exe.sha256","browser_download_url":"https://download.test/alpha27.exe.sha256"}
            ]
          }
        ]
        """;

        using var client = new HttpClient(new UpdateHandler(request =>
        {
            if (request.RequestUri!.Host == "script.example.test")
                return TextResponse("{\"ok\":false,\"error\":\"invalid_route\"}", "application/json");
            if (request.RequestUri.Host == "api.github.com")
            {
                githubCalls++;
                return TextResponse(releasesJson, "application/json");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var updater = new UpdateClient(client, new Uri("https://script.example.test/exec"));
        var result = await updater.CheckForUpdateAsync("v0.1.0-alpha.26", includePrerelease: true);

        Assert(result.Source == UpdateMetadataSource.GitHub && result.Descriptor?.TagName == "v0.1.0-alpha.27",
            "unavailable manifest did not fall back to GitHub releases");
        Assert(githubCalls == 1, "manifest fallback made an unexpected number of GitHub calls");
    }

    private static async Task TestInvalidManifestRejectedAsync()
    {
        using var client = new HttpClient(new UpdateHandler(request =>
        {
            if (request.RequestUri!.Host == "script.example.test")
            {
                return TextResponse(
                    "{\"channel\":\"prerelease\",\"version\":\"v0.1.0-alpha.27\",\"url\":\"https://evil.example/update.exe\",\"sha256\":\"" + new string('a', 64) + "\"}",
                    "application/json");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        try
        {
            await new UpdateManifestClient(client, new Uri("https://script.example.test/exec"))
                .CheckForUpdateAsync("v0.1.0-alpha.26", includePrerelease: true);
            throw new InvalidOperationException("invalid manifest package URL was accepted");
        }
        catch (UpdateManifestUnavailableException)
        {
        }
    }

    private static async Task TestRateLimitPersistenceAndSuppressionAsync()
    {
        using var temp = TempDirectory.Create();
        var paths = new AppPaths(temp.Path);
        var json = new JsonFileStore();
        var logger = new DiagnosticLogger(paths);
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var resetUtc = now.AddHours(1);
        var clock = new MutableTimeProvider(now);
        var githubCalls = 0;
        var manifestCalls = 0;
        var manifestAvailable = false;
        var hash = new string('b', 64);

        using var http = new HttpClient(new UpdateHandler(request =>
        {
            if (request.RequestUri!.Host == "script.example.test")
            {
                manifestCalls++;
                return manifestAvailable
                    ? TextResponse(ManifestJson("prerelease", "v0.1.0-alpha.27", hash), "application/json")
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            if (request.RequestUri.Host == "api.github.com")
            {
                githubCalls++;
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                response.Headers.TryAddWithoutValidation(
                    "X-RateLimit-Reset",
                    resetUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var updater = new UpdateClient(http, new Uri("https://script.example.test/exec"), clock);
        var maintenance = new UpdateMaintenanceService(
            "v0.1.0-alpha.26", paths, json, logger, clock);

        try
        {
            await maintenance.CheckAsync(updater, force: true);
            throw new InvalidOperationException("GitHub rate limit was not surfaced");
        }
        catch (GitHubUpdateRateLimitException)
        {
        }

        var limited = new UpdateStateStore(paths, json).Load();
        Assert(limited.GitHubRateLimitResetUtc == resetUtc,
            "GitHub reset time was not persisted after a 403 rate limit");
        Assert(githubCalls == 1 && manifestCalls == 1,
            "initial manifest/fallback request count changed");

        clock.Set(now.AddMinutes(16));
        try
        {
            await maintenance.CheckAsync(updater, force: false);
            throw new InvalidOperationException("manifest failure during known GitHub limit unexpectedly succeeded");
        }
        catch (UpdateMetadataUnavailableException)
        {
        }
        Assert(githubCalls == 1,
            "background retry called GitHub before the persisted reset time");

        manifestAvailable = true;
        var manual = await maintenance.CheckAsync(updater, force: true);
        Assert(manual.Descriptor?.TagName == "v0.1.0-alpha.27",
            "manual check did not use the manifest during GitHub rate limit");
        Assert(githubCalls == 1,
            "manual force bypassed the known GitHub rate-limit window");
        Assert(manual.State.GitHubRateLimitResetUtc == resetUtc,
            "manifest success cleared an active GitHub rate-limit window too early");
    }

    private static async Task TestGitHub429RetryAfterAsync()
    {
        var now = new DateTimeOffset(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        using var client = new HttpClient(new UpdateHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return response;
        }));

        try
        {
            await new GitHubUpdateClient(client, clock).CheckForUpdateAsync("v0.1.0-alpha.26", true);
            throw new InvalidOperationException("GitHub 429 was not treated as rate limit");
        }
        catch (GitHubUpdateRateLimitException ex)
        {
            Assert(ex.ResetUtc == now.AddMinutes(2), "Retry-After was not converted into reset time");
        }
    }

    private static async Task TestManifestChecksumMismatchAsync()
    {
        var expectedHash = new string('c', 64);
        var checksumRequests = 0;
        var githubApiCalls = 0;
        using var client = new HttpClient(new UpdateHandler(request =>
        {
            if (request.RequestUri!.Host == "script.example.test")
                return TextResponse(ManifestJson("prerelease", "v0.1.0-alpha.27", expectedHash), "application/json");
            if (request.RequestUri.Host == "api.github.com")
            {
                githubApiCalls++;
                return TextResponse("[]", "application/json");
            }
            if (request.RequestUri.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal))
            {
                checksumRequests++;
                return TextResponse(expectedHash, "text/plain");
            }
            if (request.RequestUri.Host == "github.com")
                return BytesResponse(Encoding.UTF8.GetBytes("tampered-package"), "application/octet-stream");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var updater = new UpdateClient(client, new Uri("https://script.example.test/exec"));
        var discovery = await updater.CheckForUpdateAsync("v0.1.0-alpha.26", true);
        Assert(discovery.Source == UpdateMetadataSource.Manifest && discovery.Descriptor is not null,
            "valid manifest was not selected before checksum test");
        try
        {
            await updater.DownloadAndVerifyAsync(discovery.Descriptor!);
            throw new InvalidOperationException("manifest SHA-256 mismatch did not block update");
        }
        catch (InvalidDataException)
        {
        }
        Assert(checksumRequests == 0,
            "inline manifest SHA-256 unexpectedly downloaded a separate checksum asset");
        Assert(githubApiCalls == 0,
            "manifest-backed package verification unexpectedly called GitHub API");
    }

    private static async Task TestReleaseChannelSelectionAsync()
    {
        var releasesJson = """
        [
          {
            "tag_name":"v0.1.1-alpha.1",
            "draft":false,
            "prerelease":true,
            "html_url":"https://github.test/releases/alpha1",
            "assets":[
              {"name":"IS74Wifi-v0.1.1-alpha.1-win-x64.exe","browser_download_url":"https://download.test/alpha1.exe"},
              {"name":"IS74Wifi-v0.1.1-alpha.1-win-x64.exe.sha256","browser_download_url":"https://download.test/alpha1.exe.sha256"}
            ]
          },
          {
            "tag_name":"v0.1.0",
            "draft":false,
            "prerelease":false,
            "html_url":"https://github.test/releases/stable",
            "assets":[
              {"name":"IS74Wifi-v0.1.0-win-x64.exe","browser_download_url":"https://download.test/stable.exe"},
              {"name":"IS74Wifi-v0.1.0-win-x64.exe.sha256","browser_download_url":"https://download.test/stable.exe.sha256"}
            ]
          }
        ]
        """;

        using var client = new HttpClient(new UpdateHandler(request =>
            request.RequestUri!.Host == "api.github.com"
                ? TextResponse(releasesJson, "application/json")
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var updater = new GitHubUpdateClient(client);

        var stableFromAlpha = await updater.CheckForUpdateAsync(
            "v0.1.0-alpha.18",
            includePrerelease: false);
        Assert(stableFromAlpha?.TagName == "v0.1.0",
            "stable-only channel did not offer the stable release to an alpha build");

        var noPrereleaseFromStable = await updater.CheckForUpdateAsync(
            "v0.1.0",
            includePrerelease: false);
        Assert(noPrereleaseFromStable is null,
            "stable-only channel offered a prerelease");

        var prereleaseFromStable = await updater.CheckForUpdateAsync(
            "v0.1.0",
            includePrerelease: true);
        Assert(prereleaseFromStable?.TagName == "v0.1.1-alpha.1",
            "prerelease channel did not offer a newer prerelease to a stable build");
    }

    private static async Task TestDirectExecutableSelectionAndVerifiedDownloadAsync()
    {
        var exeBytes = Encoding.UTF8.GetBytes("new-native-aot-exe");
        var exeHash = Convert.ToHexString(SHA256.HashData(exeBytes)).ToLowerInvariant();
        var releasesJson = """
        [
          {
            "tag_name":"v0.1.0-alpha.8",
            "draft":false,
            "prerelease":true,
            "html_url":"https://github.test/releases/alpha8",
            "assets":[]
          },
          {
            "tag_name":"v0.1.0-alpha.10",
            "draft":false,
            "prerelease":true,
            "html_url":"https://github.test/releases/alpha10",
            "assets":[
              {"name":"IS74Wifi-v0.1.0-alpha.10-win-x64.exe","browser_download_url":"https://download.test/alpha10.exe"},
              {"name":"IS74Wifi-v0.1.0-alpha.10-win-x64.exe.sha256","browser_download_url":"https://download.test/alpha10.exe.sha256"}
            ]
          },
          {
            "tag_name":"v0.1.0-alpha.11",
            "draft":true,
            "prerelease":true,
            "html_url":"https://github.test/releases/alpha11",
            "assets":[]
          }
        ]
        """;

        using var client = new HttpClient(new UpdateHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
                return TextResponse(releasesJson, "application/json");
            if (request.RequestUri.AbsoluteUri == "https://download.test/alpha10.exe")
                return BytesResponse(exeBytes, "application/octet-stream");
            if (request.RequestUri.AbsoluteUri == "https://download.test/alpha10.exe.sha256")
                return TextResponse($"{exeHash}  IS74Wifi-v0.1.0-alpha.10-win-x64.exe", "text/plain");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var updater = new GitHubUpdateClient(client);
        var update = await updater.CheckForUpdateAsync("v0.1.0-alpha.9", includePrerelease: true);
        Assert(update?.TagName == "v0.1.0-alpha.10", "latest usable prerelease was not selected");
        Assert(update?.PackageAssetName == "IS74Wifi-v0.1.0-alpha.10-win-x64.exe", "direct EXE asset was not preferred");

        var transfer = new List<UpdateTransferProgress>();
        var prepared = await updater.DownloadAndVerifyAsync(update!, transferProgress: transfer.Add);
        try
        {
            Assert(File.Exists(prepared.ExecutablePath), "verified direct update executable was not prepared");
            Assert(File.ReadAllText(prepared.ExecutablePath) == "new-native-aot-exe", "unexpected direct update executable payload");
            Assert(prepared.PackageSha256 == exeHash, "verified direct update hash changed");
            var packageProgress = transfer.Where(item => item.Stage == UpdateProgressStage.DownloadingPackage).ToArray();
            Assert(packageProgress.Length >= 2, "package transfer progress was not reported");
            Assert(packageProgress[0].BytesReceived == 0, "package transfer progress did not start at zero");
            Assert(packageProgress[^1].BytesReceived == exeBytes.Length, "package transfer progress did not reach the full payload");
            Assert(packageProgress[^1].TotalBytes == exeBytes.Length, "package transfer total length changed");
        }
        finally
        {
            GitHubUpdateClient.TryDeleteDirectory(prepared.WorkingDirectory);
        }
    }

    private static async Task TestOversizedReleaseAndPackageAsync()
    {
        using (var releasesClient = new HttpClient(new UpdateHandler(_ =>
        {
            var response = TextResponse("[]", "application/json");
            response.Content.Headers.ContentLength = GitHubUpdateClient.MaximumReleaseJsonBytes + 1;
            return response;
        })))
        {
            try
            {
                await new GitHubUpdateClient(releasesClient).CheckForUpdateAsync("v0.1.0-alpha.1", true);
                throw new InvalidOperationException("oversized release metadata was accepted");
            }
            catch (ResponseBodyTooLargeException) { }
        }

        var descriptor = new UpdateDescriptor(
            "v0.1.0-alpha.21", "https://github.test/releases/alpha21",
            "IS74Wifi-v0.1.0-alpha.21-win-x64.exe", new Uri("https://download.test/oversized.exe"),
            "IS74Wifi-v0.1.0-alpha.21-win-x64.exe.sha256", new Uri("https://download.test/oversized.exe.sha256"));
        using var packageClient = new HttpClient(new UpdateHandler(_ =>
        {
            var response = BytesResponse(new byte[1], "application/octet-stream");
            response.Content.Headers.ContentLength = GitHubUpdateClient.MaximumPackageBytes + 1;
            return response;
        }));
        var workBefore = Directory.GetDirectories(Path.GetTempPath(), "IS74Wifi-update-*").ToHashSet(StringComparer.Ordinal);
        try
        {
            await new GitHubUpdateClient(packageClient).DownloadAndVerifyAsync(descriptor);
            throw new InvalidOperationException("oversized release package was accepted");
        }
        catch (InvalidDataException) { }
        var leftBehind = Directory.GetDirectories(Path.GetTempPath(), "IS74Wifi-update-*")
            .Where(path => !workBefore.Contains(path)).ToArray();
        Assert(leftBehind.Length == 0, "oversized update left a temporary work directory");
    }

    private static string ManifestJson(string channel, string version, string sha256)
    {
        var package = $"IS74Wifi-{version}-win-x64.exe";
        var url = $"https://github.com/{GitHubUpdateClient.Repository}/releases/download/{version}/{package}";
        return $"{{\"channel\":\"{channel}\",\"version\":\"{version}\",\"url\":\"{url}\",\"sha256\":\"{sha256}\"}}";
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;
        public override DateTimeOffset GetUtcNow() => current;
        public void Set(DateTimeOffset value) => current = value;
    }

    private static HttpResponseMessage TextResponse(string value, string mediaType) =>
        new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, mediaType) };

    private static HttpResponseMessage BytesResponse(byte[] value, string mediaType) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) } } };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class UpdateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
