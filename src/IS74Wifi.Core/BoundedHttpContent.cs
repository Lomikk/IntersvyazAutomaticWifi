using System.Text;

namespace IS74Wifi.Core;

/// <summary>
/// Enforces response limits even when a server omits or lies about Content-Length.
/// Call after ResponseHeadersRead so an oversized Content-Length fails before allocation.
/// </summary>
public static class BoundedHttpContent
{
    public const int DefaultBodyLimitBytes = 256 * 1024;
    public const int TelemetryBodyLimitBytes = 256 * 1024;
    public const int DiagnosticBodyLimitBytes = 32 * 1024;

    public static async Task<string> ReadAsStringAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ReadBytesAsync(content, maxBytes, cancellationToken).ConfigureAwait(false);
        // All current API, captive portal, and Google Apps Script endpoints use UTF-8.
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    public static async Task<byte[]> ReadBytesAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (content.Headers.ContentLength is long length && length > maxBytes)
        {
            throw new ResponseBodyTooLargeException(maxBytes);
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            // Read one byte beyond the cap when needed; never trust Content-Length.
            var allowance = Math.Min(chunk.Length, maxBytes - (int)buffer.Length + 1);
            var count = await input.ReadAsync(chunk.AsMemory(0, allowance), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > maxBytes)
            {
                throw new ResponseBodyTooLargeException(maxBytes);
            }
            buffer.Write(chunk, 0, count);
        }

        return buffer.ToArray();
    }
}

public sealed class ResponseBodyTooLargeException(int maxBytes)
    : IOException($"HTTP response body exceeds {maxBytes} bytes.");
