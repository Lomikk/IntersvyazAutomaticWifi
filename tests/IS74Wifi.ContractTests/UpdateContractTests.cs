using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using IS74Wifi.Core;

internal static class UpdateContractTests
{
    public static async Task RunAsync()
    {
        TestSemanticVersions();
        TestProgramInstallation();
        TestInstalledAppRegistration();
        TestChecksumParser();
        await TestReleaseSelectionAndVerifiedDownloadAsync();
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

    private static void TestChecksumParser()
    {
        var hash = new string('a', 64);
        var parsed = GitHubUpdateClient.ParseChecksum($"{hash}  IS74Wifi-v0.1.0-alpha.9-win-x64.zip\n", "IS74Wifi-v0.1.0-alpha.9-win-x64.zip");
        Assert(parsed == hash, "valid checksum did not parse");

        try
        {
            GitHubUpdateClient.ParseChecksum($"{hash}  other.zip", "IS74Wifi-v0.1.0-alpha.9-win-x64.zip");
            throw new InvalidOperationException("checksum for a different asset was accepted");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static async Task TestReleaseSelectionAndVerifiedDownloadAsync()
    {
        var zipBytes = CreateReleaseZip();
        var hash = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
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
              {"name":"IS74Wifi-v0.1.0-alpha.10-win-x64.zip","browser_download_url":"https://download.test/alpha10.zip"},
              {"name":"IS74Wifi-v0.1.0-alpha.10-win-x64.zip.sha256","browser_download_url":"https://download.test/alpha10.sha256"}
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
            if (request.RequestUri.AbsoluteUri == "https://download.test/alpha10.zip")
                return BytesResponse(zipBytes, "application/zip");
            if (request.RequestUri.AbsoluteUri == "https://download.test/alpha10.sha256")
                return TextResponse($"{hash}  IS74Wifi-v0.1.0-alpha.10-win-x64.zip", "text/plain");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var updater = new GitHubUpdateClient(client);
        var update = await updater.CheckForUpdateAsync("v0.1.0-alpha.9", includePrerelease: true);
        Assert(update?.TagName == "v0.1.0-alpha.10", "latest usable prerelease was not selected");

        var prepared = await updater.DownloadAndVerifyAsync(update!);
        try
        {
            Assert(File.Exists(prepared.ExecutablePath), "verified update executable was not extracted");
            Assert(File.ReadAllText(prepared.ExecutablePath) == "new-native-aot-exe", "unexpected update executable payload");
            Assert(prepared.ZipSha256 == hash, "verified update hash changed");
        }
        finally
        {
            GitHubUpdateClient.TryDeleteDirectory(prepared.WorkingDirectory);
        }
    }

    private static byte[] CreateReleaseZip()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("IS74Wifi.exe");
            using var output = entry.Open();
            var bytes = Encoding.UTF8.GetBytes("new-native-aot-exe");
            output.Write(bytes);
        }
        return stream.ToArray();
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
