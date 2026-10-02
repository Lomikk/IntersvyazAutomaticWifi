using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IS74Wifi.Core;

public enum UpdateMetadataSource
{
    Manifest,
    GitHub
}

public sealed record UpdateDiscoveryResult(
    UpdateDescriptor? Descriptor,
    UpdateMetadataSource Source);

public sealed class UpdateManifestUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class UpdateMetadataUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class GitHubUpdateRateLimitException(
    System.Net.HttpStatusCode statusCode,
    DateTimeOffset? resetUtc)
    : HttpRequestException(
        resetUtc is { } reset
            ? $"GitHub временно ограничил анонимные проверки обновлений до {reset:O}."
            : "GitHub временно ограничил анонимные проверки обновлений.",
        null,
        statusCode)
{
    public DateTimeOffset? ResetUtc { get; } = resetUtc;
}

internal sealed record UpdateManifestDocument(
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("sha256")] string? Sha256);

public sealed class UpdateManifestClient(HttpClient httpClient, Uri endpoint)
{
    public const int MaximumManifestBytes = 32 * 1024;

    public async Task<UpdateDescriptor?> CheckForUpdateAsync(
        string currentVersion,
        bool includePrerelease,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!SemanticVersion.TryParse(currentVersion, out var current))
            {
                throw new InvalidOperationException($"Не удалось разобрать текущую версию: {currentVersion}");
            }

            var channel = includePrerelease ? "prerelease" : "stable";
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildManifestUri(channel));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd($"IS74Wifi/{currentVersion.TrimStart('v')}");

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var bytes = await BoundedHttpContent.ReadBytesAsync(
                response.Content,
                MaximumManifestBytes,
                cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize(bytes, UpdateDiscoveryJsonContext.Default.UpdateManifestDocument)
                           ?? throw new InvalidDataException("Manifest обновлений пуст.");

            if (!string.Equals(document.Channel, channel, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Manifest обновлений вернул другой канал.");
            }
            if (string.IsNullOrWhiteSpace(document.Version) ||
                !SemanticVersion.TryParse(document.Version, out var available))
            {
                throw new InvalidDataException("Manifest обновлений содержит некорректную версию.");
            }

            var tag = document.Version;
            var packageName = $"IS74Wifi-{tag}-win-x64.exe";
            if (!Uri.TryCreate(document.Url, UriKind.Absolute, out var packageUri) ||
                packageUri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(packageUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Manifest обновлений содержит некорректный URL пакета.");
            }

            var expectedPath = $"/{GitHubUpdateClient.Repository}/releases/download/{tag}/{packageName}";
            if (!string.Equals(packageUri.AbsolutePath, expectedPath, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Manifest обновлений указывает неожиданный release asset.");
            }

            var sha256 = NormalizeSha256(document.Sha256);
            if (available.CompareTo(current) <= 0)
            {
                return null;
            }

            return new UpdateDescriptor(
                tag,
                $"https://github.com/{GitHubUpdateClient.Repository}/releases/tag/{tag}",
                packageName,
                packageUri,
                ExpectedSha256: sha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or ResponseBodyTooLargeException or TaskCanceledException)
        {
            throw new UpdateManifestUnavailableException("Основной manifest обновлений недоступен или некорректен.", ex);
        }
    }

    private Uri BuildManifestUri(string channel)
    {
        var builder = new UriBuilder(endpoint);
        var query = builder.Query.TrimStart('?');
        if (query.Length > 0) query += "&";
        builder.Query = query + "route=update&channel=" + Uri.EscapeDataString(channel);
        return builder.Uri;
    }

    private static string NormalizeSha256(string? value)
    {
        var hash = value?.Trim() ?? string.Empty;
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Manifest обновлений содержит некорректный SHA-256.");
        }
        return hash.ToLowerInvariant();
    }
}

public sealed class UpdateClient(
    HttpClient httpClient,
    Uri manifestEndpoint,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly UpdateManifestClient manifest = new(httpClient, manifestEndpoint);
    private readonly GitHubUpdateClient github = new(httpClient, timeProvider);

    public async Task<UpdateDiscoveryResult> CheckForUpdateAsync(
        string currentVersion,
        bool includePrerelease,
        DateTimeOffset? githubRateLimitResetUtc = null,
        Action<UpdateProgressStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ReportProgress(progress, UpdateProgressStage.RequestingReleases);
        try
        {
            var descriptor = await manifest.CheckForUpdateAsync(
                currentVersion,
                includePrerelease,
                cancellationToken).ConfigureAwait(false);
            ReportProgress(progress, UpdateProgressStage.ReleasesLoaded);
            return new UpdateDiscoveryResult(descriptor, UpdateMetadataSource.Manifest);
        }
        catch (UpdateManifestUnavailableException ex)
        {
            var now = clock.GetUtcNow();
            if (githubRateLimitResetUtc is { } resetUtc && resetUtc > now)
            {
                throw new UpdateMetadataUnavailableException(
                    $"Manifest обновлений недоступен; резервный GitHub API не будет запрошен до {resetUtc:O} из-за rate limit.",
                    ex);
            }
        }

        var fallback = await github.CheckForUpdateAsync(
            currentVersion,
            includePrerelease,
            progress: null,
            cancellationToken).ConfigureAwait(false);
        ReportProgress(progress, UpdateProgressStage.ReleasesLoaded);
        return new UpdateDiscoveryResult(fallback, UpdateMetadataSource.GitHub);
    }

    public Task<PreparedUpdate> DownloadAndVerifyAsync(
        UpdateDescriptor descriptor,
        Action<UpdateProgressStage>? progress = null,
        Action<UpdateTransferProgress>? transferProgress = null,
        CancellationToken cancellationToken = default) =>
        github.DownloadAndVerifyAsync(descriptor, progress, transferProgress, cancellationToken);

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
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UpdateManifestDocument))]
internal sealed partial class UpdateDiscoveryJsonContext : JsonSerializerContext;
