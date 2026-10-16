using System.Net;
using System.Text.Json;

namespace IS74Wifi.Core;

/// <summary>Fetches the current public support URL on demand. Never persists or caches it.</summary>
public sealed class SupportLinkClient(HttpClient http, Uri endpoint)
{
    public async Task<Uri?> GetUrlAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var separator = string.IsNullOrEmpty(endpoint.Query) ? "?" : "&";
        var route = new Uri(endpoint.AbsoluteUri + separator + "route=config");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return null;

            var json = await BoundedHttpContent.ReadAsStringAsync(
                response.Content, 4096, timeoutCts.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("support_url", out var property) || property.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = property.GetString();
            if (value is null || value.Length > 2048 ||
                !Uri.TryCreate(value, UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps || url.IsLoopback ||
                !string.IsNullOrEmpty(url.UserInfo) || url.HostNameType != UriHostNameType.Dns)
            {
                return null;
            }

            return url;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
