using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IS74Wifi.Core;

public sealed record UpdateDescriptor(
    string TagName,
    string ReleasePageUrl,
    string ZipAssetName,
    Uri ZipDownloadUrl,
    string ChecksumAssetName,
    Uri ChecksumDownloadUrl);

public sealed record PreparedUpdate(
    UpdateDescriptor Descriptor,
    string WorkingDirectory,
    string ExecutablePath,
    string ZipSha256);

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
        CancellationToken cancellationToken = default)
    {
        if (!SemanticVersion.TryParse(currentVersion, out var current))
            throw new InvalidOperationException($"Не удалось разобрать текущую версию: {currentVersion}");

        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd($"IS74Wifi/{currentVersion.TrimStart('v')}");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var releases = await JsonSerializer.DeserializeAsync(
            stream,
            UpdateJsonContext.Default.GitHubReleaseDocumentArray,
            cancellationToken).ConfigureAwait(false) ?? [];

        UpdateDescriptor? selected = null;
        SemanticVersion selectedVersion = default;
        var hasSelected = false;

        foreach (var release in releases)
        {
            if (release.Draft || (!includePrerelease && release.Prerelease)) continue;
            if (!SemanticVersion.TryParse(release.TagName, out var version)) continue;
            if (version.CompareTo(current) <= 0) continue;

            var zipName = $"IS74Wifi-{release.TagName}-win-x64.zip";
            var checksumName = zipName + ".sha256";
            var zip = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, zipName, StringComparison.Ordinal));
            var checksum = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, checksumName, StringComparison.Ordinal));
            if (zip is null || checksum is null) continue;
            if (!Uri.TryCreate(zip.BrowserDownloadUrl, UriKind.Absolute, out var zipUri) ||
                !Uri.TryCreate(checksum.BrowserDownloadUrl, UriKind.Absolute, out var checksumUri)) continue;

            if (!hasSelected || version.CompareTo(selectedVersion) > 0)
            {
                selectedVersion = version;
                hasSelected = true;
                selected = new UpdateDescriptor(
                    release.TagName,
                    release.HtmlUrl,
                    zip.Name,
                    zipUri,
                    checksum.Name,
                    checksumUri);
            }
        }

        return selected;
    }

    public async Task<PreparedUpdate> DownloadAndVerifyAsync(
        UpdateDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "IS74Wifi-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, descriptor.ZipAssetName);
        var checksumPath = Path.Combine(work, descriptor.ChecksumAssetName);

        try
        {
            await DownloadFileAsync(descriptor.ZipDownloadUrl, zipPath, cancellationToken).ConfigureAwait(false);
            await DownloadFileAsync(descriptor.ChecksumDownloadUrl, checksumPath, cancellationToken).ConfigureAwait(false);

            var expected = ParseChecksum(await File.ReadAllTextAsync(checksumPath, cancellationToken).ConfigureAwait(false), descriptor.ZipAssetName);
            var actual = await ComputeSha256Async(zipPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SHA-256 обновления не совпал. Ожидалось {expected}, получено {actual}.");
            }

            var executablePath = Path.Combine(work, "IS74Wifi-new.exe");
            ExtractSingleExecutable(zipPath, executablePath);
            return new PreparedUpdate(descriptor, work, executablePath, actual);
        }
        catch
        {
            TryDeleteDirectory(work);
            throw;
        }
    }

    private async Task DownloadFileAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("IS74Wifi/updater");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
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
