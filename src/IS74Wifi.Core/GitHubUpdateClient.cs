using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IS74Wifi.Core;

public sealed record UpdateDescriptor(
    string TagName,
    string ReleasePageUrl,
    string PackageAssetName,
    Uri PackageDownloadUrl,
    string? ChecksumAssetName = null,
    Uri? ChecksumDownloadUrl = null,
    string? ExpectedSha256 = null);

public sealed record PreparedUpdate(
    UpdateDescriptor Descriptor,
    string WorkingDirectory,
    string ExecutablePath,
    string PackageSha256);

public sealed record UpdateTransferProgress(
    UpdateProgressStage Stage,
    long BytesReceived,
    long? TotalBytes);

public enum UpdateProgressStage
{
    RequestingReleases,
    ReleasesLoaded,
    DownloadingPackage,
    PackageDownloaded,
    DownloadingChecksum,
    ChecksumDownloaded,
    VerifyingChecksum,
    ChecksumVerified,
    PreparingExecutable,
    ExecutablePrepared
}

internal sealed record GitHubReleaseDocument(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("assets")] GitHubReleaseAssetDocument[] Assets);

internal sealed record GitHubReleaseAssetDocument(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

public sealed class GitHubUpdateClient(HttpClient httpClient, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public const string Repository = "Lomikk/IntersvyazAutomaticWifi";
    // Release JSON is normally <100 KiB, checksum ~100 bytes, and the current
    // NativeAOT executable is ~11 MiB. These limits leave room for growth but
    // bound untrusted downloads before they can exhaust memory or disk.
    public const int MaximumReleaseJsonBytes = 2 * 1024 * 1024;
    public const long MaximumPackageBytes = 128L * 1024 * 1024;
    public const long MaximumChecksumBytes = 4096;
    private static readonly Uri ReleasesUri = new($"https://api.github.com/repos/{Repository}/releases?per_page=30");

    public async Task<UpdateDescriptor?> CheckForUpdateAsync(
        string currentVersion,
        bool includePrerelease,
        Action<UpdateProgressStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!SemanticVersion.TryParse(currentVersion, out var current))
            throw new InvalidOperationException($"Не удалось разобрать текущую версию: {currentVersion}");

        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd($"IS74Wifi/{currentVersion.TrimStart('v')}");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");

        ReportProgress(progress, UpdateProgressStage.RequestingReleases);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (TryGetRateLimitReset(response, out var rateLimitResetUtc))
        {
            throw new GitHubUpdateRateLimitException(response.StatusCode, rateLimitResetUtc);
        }
        response.EnsureSuccessStatusCode();

        var releaseBytes = await BoundedHttpContent.ReadBytesAsync(
            response.Content, MaximumReleaseJsonBytes, cancellationToken).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize(
            releaseBytes,
            UpdateJsonContext.Default.GitHubReleaseDocumentArray
            ) ?? [];
        ReportProgress(progress, UpdateProgressStage.ReleasesLoaded);

        UpdateDescriptor? selected = null;
        SemanticVersion selectedVersion = default;
        var hasSelected = false;

        foreach (var release in releases)
        {
            if (release.Draft || (!includePrerelease && release.Prerelease)) continue;
            if (!SemanticVersion.TryParse(release.TagName, out var version)) continue;
            if (version.CompareTo(current) <= 0) continue;

            var exeName = $"IS74Wifi-{release.TagName}-win-x64.exe";
            var exeChecksumName = exeName + ".sha256";
            var package = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, exeName, StringComparison.Ordinal));
            var checksum = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, exeChecksumName, StringComparison.Ordinal));

            if (package is null || checksum is null) continue;
            if (!Uri.TryCreate(package.BrowserDownloadUrl, UriKind.Absolute, out var packageUri) ||
                !Uri.TryCreate(checksum.BrowserDownloadUrl, UriKind.Absolute, out var checksumUri)) continue;

            if (!hasSelected || version.CompareTo(selectedVersion) > 0)
            {
                selectedVersion = version;
                hasSelected = true;
                selected = new UpdateDescriptor(
                    release.TagName,
                    release.HtmlUrl,
                    package.Name,
                    packageUri,
                    checksum.Name,
                    checksumUri);
            }
        }

        return selected;
    }

    public async Task<PreparedUpdate> DownloadAndVerifyAsync(
        UpdateDescriptor descriptor,
        Action<UpdateProgressStage>? progress = null,
        Action<UpdateTransferProgress>? transferProgress = null,
        CancellationToken cancellationToken = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "IS74Wifi-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var packagePath = Path.Combine(work, descriptor.PackageAssetName);

        try
        {
            ReportProgress(progress, UpdateProgressStage.DownloadingPackage);
            await DownloadFileAsync(
                descriptor.PackageDownloadUrl,
                packagePath,
                UpdateProgressStage.DownloadingPackage,
                MaximumPackageBytes,
                transferProgress,
                cancellationToken).ConfigureAwait(false);
            ReportProgress(progress, UpdateProgressStage.PackageDownloaded);

            string expected;
            if (!string.IsNullOrWhiteSpace(descriptor.ExpectedSha256))
            {
                expected = NormalizeExpectedSha256(descriptor.ExpectedSha256);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(descriptor.ChecksumAssetName) || descriptor.ChecksumDownloadUrl is null)
                {
                    throw new InvalidDataException("Для обновления не задан SHA-256 или файл контрольной суммы.");
                }

                var checksumPath = Path.Combine(work, descriptor.ChecksumAssetName);
                ReportProgress(progress, UpdateProgressStage.DownloadingChecksum);
                await DownloadFileAsync(
                    descriptor.ChecksumDownloadUrl,
                    checksumPath,
                    UpdateProgressStage.DownloadingChecksum,
                    MaximumChecksumBytes,
                    transferProgress,
                    cancellationToken).ConfigureAwait(false);
                ReportProgress(progress, UpdateProgressStage.ChecksumDownloaded);
                expected = ParseChecksum(
                    await File.ReadAllTextAsync(checksumPath, cancellationToken).ConfigureAwait(false),
                    descriptor.PackageAssetName);
            }

            ReportProgress(progress, UpdateProgressStage.VerifyingChecksum);
            var actual = await ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SHA-256 обновления не совпал. Ожидалось {expected}, получено {actual}.");
            }
            ReportProgress(progress, UpdateProgressStage.ChecksumVerified);

            var executablePath = Path.Combine(work, "IS74Wifi-new.exe");
            ReportProgress(progress, UpdateProgressStage.PreparingExecutable);
            File.Copy(packagePath, executablePath, overwrite: true);
            ReportProgress(progress, UpdateProgressStage.ExecutablePrepared);
            return new PreparedUpdate(descriptor, work, executablePath, actual);
        }
        catch
        {
            TryDeleteDirectory(work);
            throw;
        }
    }

    private static void ReportProgress(Action<UpdateProgressStage>? progress, UpdateProgressStage stage)
    {
        try
        {
            progress?.Invoke(stage);
        }
        catch
        {
            // Progress reporting is observational and must not affect update integrity.
        }
    }

    private async Task DownloadFileAsync(
        Uri uri,
        string destination,
        UpdateProgressStage stage,
        long maximumBytes,
        Action<UpdateTransferProgress>? transferProgress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("IS74Wifi/updater");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;
        if (totalBytes > maximumBytes)
        {
            throw new InvalidDataException($"Размер файла обновления превышает лимит {maximumBytes} байт.");
        }
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long receivedBytes = 0;
        long lastReportedBytes = 0;
        ReportTransferProgress(transferProgress, stage, receivedBytes, totalBytes);

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (receivedBytes > maximumBytes - read)
            {
                throw new InvalidDataException($"Размер файла обновления превышает лимит {maximumBytes} байт.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            receivedBytes += read;

            if (receivedBytes - lastReportedBytes >= 256 * 1024 ||
                (totalBytes.HasValue && receivedBytes >= totalBytes.Value))
            {
                ReportTransferProgress(transferProgress, stage, receivedBytes, totalBytes);
                lastReportedBytes = receivedBytes;
            }
        }

        if (lastReportedBytes != receivedBytes)
        {
            ReportTransferProgress(transferProgress, stage, receivedBytes, totalBytes);
        }
    }

    private static void ReportTransferProgress(
        Action<UpdateTransferProgress>? progress,
        UpdateProgressStage stage,
        long bytesReceived,
        long? totalBytes)
    {
        try
        {
            progress?.Invoke(new UpdateTransferProgress(stage, bytesReceived, totalBytes));
        }
        catch
        {
            // Transfer progress is observational and must not affect update integrity.
        }
    }

    public static string ParseChecksum(string content, string expectedFileName)
    {
        var firstLine = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? throw new InvalidDataException("Файл SHA-256 пуст.");
        var parts = firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit))
            throw new InvalidDataException("Некорректный формат SHA-256 файла релиза.");

        var fileName = parts[^1].TrimStart('*');
        if (!string.Equals(fileName, expectedFileName, StringComparison.Ordinal))
            throw new InvalidDataException("SHA-256 относится к другому файлу релиза.");

        return parts[0].ToLowerInvariant();
    }


    private bool TryGetRateLimitReset(HttpResponseMessage response, out DateTimeOffset? resetUtc)
    {
        resetUtc = null;
        // This endpoint is public and requires no authorization. A 403 from the
        // anonymous Releases API is therefore not actionable by the user and, in
        // practice, represents primary/secondary rate limiting or abuse protection.
        // Treat it like 429 even when GitHub omits reset headers so a shared NAT is
        // not hammered by repeated forced checks.
        var rateLimited = response.StatusCode is
            System.Net.HttpStatusCode.TooManyRequests or
            System.Net.HttpStatusCode.Forbidden;

        if (!rateLimited) return false;

        var now = clock.GetUtcNow();
        DateTimeOffset? retryAfterUtc = response.Headers.RetryAfter?.Date;
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            retryAfterUtc = now + delta;
        }

        DateTimeOffset? githubResetUtc = null;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues))
        {
            var raw = resetValues.FirstOrDefault();
            if (long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var unixSeconds))
            {
                try
                {
                    githubResetUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                    githubResetUtc = null;
                }
            }
        }

        resetUtc = retryAfterUtc is null ? githubResetUtc
            : githubResetUtc is null ? retryAfterUtc
            : retryAfterUtc > githubResetUtc ? retryAfterUtc : githubResetUtc;
        return true;
    }

    private static string NormalizeExpectedSha256(string value)
    {
        var hash = value.Trim();
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Некорректный SHA-256 в метаданных обновления.");
        }
        return hash.ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubReleaseDocument[]))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
