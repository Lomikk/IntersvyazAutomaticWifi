using System.IO.Compression;
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
    string ChecksumAssetName,
    Uri ChecksumDownloadUrl,
    bool IsArchive);

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
    ExtractingPackage,
    PackageExtracted
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

public sealed class GitHubUpdateClient(HttpClient httpClient)
{
    public const string Repository = "Lomikk/IntersvyazAutomaticWifi";
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
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var releases = await JsonSerializer.DeserializeAsync(
            stream,
            UpdateJsonContext.Default.GitHubReleaseDocumentArray,
            cancellationToken).ConfigureAwait(false) ?? [];
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
            var zipName = $"IS74Wifi-{release.TagName}-win-x64.zip";
            var zipChecksumName = zipName + ".sha256";

            var package = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, exeName, StringComparison.Ordinal));
            var checksum = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, exeChecksumName, StringComparison.Ordinal));
            var isArchive = false;

            if (package is null || checksum is null)
            {
                package = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, zipName, StringComparison.Ordinal));
                checksum = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, zipChecksumName, StringComparison.Ordinal));
                isArchive = true;
            }

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
                    checksumUri,
                    isArchive);
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
        var checksumPath = Path.Combine(work, descriptor.ChecksumAssetName);

        try
        {
            ReportProgress(progress, UpdateProgressStage.DownloadingPackage);
            await DownloadFileAsync(
                descriptor.PackageDownloadUrl,
                packagePath,
                UpdateProgressStage.DownloadingPackage,
                transferProgress,
                cancellationToken).ConfigureAwait(false);
            ReportProgress(progress, UpdateProgressStage.PackageDownloaded);
            ReportProgress(progress, UpdateProgressStage.DownloadingChecksum);
            await DownloadFileAsync(
                descriptor.ChecksumDownloadUrl,
                checksumPath,
                UpdateProgressStage.DownloadingChecksum,
                transferProgress,
                cancellationToken).ConfigureAwait(false);
            ReportProgress(progress, UpdateProgressStage.ChecksumDownloaded);

            ReportProgress(progress, UpdateProgressStage.VerifyingChecksum);
            var expected = ParseChecksum(await File.ReadAllTextAsync(checksumPath, cancellationToken).ConfigureAwait(false), descriptor.PackageAssetName);
            var actual = await ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SHA-256 обновления не совпал. Ожидалось {expected}, получено {actual}.");
            }
            ReportProgress(progress, UpdateProgressStage.ChecksumVerified);

            var executablePath = Path.Combine(work, "IS74Wifi-new.exe");
            ReportProgress(progress, UpdateProgressStage.ExtractingPackage);
            if (descriptor.IsArchive)
            {
                ExtractSingleExecutable(packagePath, executablePath);
            }
            else
            {
                File.Copy(packagePath, executablePath, overwrite: true);
            }
            ReportProgress(progress, UpdateProgressStage.PackageExtracted);
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
        Action<UpdateTransferProgress>? transferProgress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("IS74Wifi/updater");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;
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

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ExtractSingleExecutable(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var fileEntries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (fileEntries.Length != 1 || !string.Equals(fileEntries[0].FullName.Replace('\\', '/'), "IS74Wifi.exe", StringComparison.Ordinal))
            throw new InvalidDataException("Архив обновления должен содержать ровно один файл IS74Wifi.exe.");

        fileEntries[0].ExtractToFile(destination, overwrite: true);
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
