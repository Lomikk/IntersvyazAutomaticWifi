using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace IS74Wifi.Core;

public sealed record Is74SpeedTestOptions
{
    public Uri BaseUri { get; init; } = new("https://s.is74.ru/", UriKind.Absolute);
    public int PingCount { get; init; } = 10;
    public int MaxPingRetries { get; init; } = 3;
    public int DownloadStreams { get; init; } = 5;
    public int UploadStreams { get; init; } = 3;
    public TimeSpan MultistreamDelay { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan InterPhaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan DownloadGraceTime { get; init; } = TimeSpan.FromSeconds(1.5);
    public TimeSpan UploadGraceTime { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan MaxThroughputDuration { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public int DownloadChunkSize { get; init; } = 100;
    public long UploadRequestBytes { get; init; } = 20L * 1024 * 1024;
    public double OverheadCompensationFactor { get; init; } = 1.06;
    public bool AutoShortenFastTests { get; init; } = true;
}

/// <summary>
/// Native implementation of the protocol observed on https://s.is74.ru/.
/// The provider currently deploys LibreSpeed in standalone mode. We reproduce
/// its measurement behavior but deliberately do not call getIP.php or the
/// provider's telemetry endpoint because neither is needed to measure speed.
/// </summary>
public sealed class Is74SpeedTestProvider : ISpeedTestProvider
{
    public const string ProviderName = "is74_librespeed";
    public const string TestVersion = "is74-librespeed-5.4.1-v1";

    private readonly HttpClient http;
    private readonly Is74SpeedTestOptions options;

    public Is74SpeedTestProvider(HttpClient http, Is74SpeedTestOptions? options = null)
    {
        this.http = http;
        this.options = options ?? new Is74SpeedTestOptions();
        ValidateOptions(this.options);
    }

    public async Task<SpeedTestMeasurement> MeasureAsync(
        IProgress<SpeedTestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var latency = await MeasureLatencyAsync(progress, cancellationToken).ConfigureAwait(false);

        await DelayBetweenPhasesAsync(cancellationToken).ConfigureAwait(false);
        var download = await MeasureThroughputAsync(
            upload: false,
            options.DownloadStreams,
            options.DownloadGraceTime,
            progress,
            cancellationToken).ConfigureAwait(false);

        await DelayBetweenPhasesAsync(cancellationToken).ConfigureAwait(false);
        var upload = await MeasureThroughputAsync(
            upload: true,
            options.UploadStreams,
            options.UploadGraceTime,
            progress,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new SpeedTestProgress(SpeedTestStage.Completed, 1));

        return new SpeedTestMeasurement(
            ProviderName,
            TestVersion,
            TestScope: "regional",
            ServerKind: "regional_provider",
            DownloadMbps: download.Mbps,
            UploadMbps: upload.Mbps,
            LatencyMs: latency.LatencyMs,
            JitterMs: latency.JitterMs,
            PacketLossPct: null,
            DownloadDuration: download.Duration,
            UploadDuration: upload.Duration,
            LatencySampleCount: latency.SampleCount,
            DownloadBytes: download.WireBytes,
            UploadBytes: upload.WireBytes);
    }

    private async Task<LatencyMeasurement> MeasureLatencyAsync(
        IProgress<SpeedTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        double? minimum = null;
        var jitter = 0d;
        var previous = 0d;
        var usedSamples = 0;

        for (var index = 0; index < options.PingCount; index++)
        {
            var elapsed = await PingOnceWithRetryAsync(cancellationToken).ConfigureAwait(false);
            var milliseconds = Math.Max(1, elapsed.TotalMilliseconds);

            // LibreSpeed intentionally treats the first request as a warm-up.
            if (index > 0)
            {
                var instantaneousJitter = Math.Abs(milliseconds - previous);
                if (index == 1)
                {
                    minimum = milliseconds;
                }
                else
                {
                    minimum = Math.Min(minimum ?? milliseconds, milliseconds);
                    if (index == 2)
                    {
                        jitter = instantaneousJitter;
                    }
                    else
                    {
                        jitter = instantaneousJitter > jitter
                            ? jitter * 0.3 + instantaneousJitter * 0.7
                            : jitter * 0.8 + instantaneousJitter * 0.2;
                    }
                }

                usedSamples++;
            }

            previous = milliseconds;
            progress?.Report(new SpeedTestProgress(
                SpeedTestStage.Latency,
                (index + 1d) / options.PingCount,
                LatencyMs: minimum,
                JitterMs: index >= 2 ? jitter : null));
        }

        if (minimum is null || usedSamples == 0)
        {
            throw new InvalidOperationException("IS74 latency test did not produce a usable sample.");
        }

        return new LatencyMeasurement(minimum.Value, jitter, usedSamples);
    }

    private async Task<TimeSpan> PingOnceWithRetryAsync(CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= options.MaxPingRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("backend/empty.php"));
            AddNoCacheHeaders(request.Headers);

            try
            {
                using var response = await http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                return Stopwatch.GetElapsedTime(started);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }
        }

        throw new HttpRequestException("IS74 latency endpoint failed after retries.", last);
    }

    private async Task<ThroughputMeasurement> MeasureThroughputAsync(
        bool upload,
        int streamCount,
        TimeSpan graceTime,
        IProgress<SpeedTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var phaseToken = phaseCts.Token;
        var phaseStarted = Stopwatch.GetTimestamp();
        long wireBytes = 0;
        long measuredBytes = 0;
        var measurementEnabled = 0;

        void CountBytes(int count)
        {
            if (count <= 0)
            {
                return;
            }

            Interlocked.Add(ref wireBytes, count);
            if (Volatile.Read(ref measurementEnabled) != 0)
            {
                Interlocked.Add(ref measuredBytes, count);
            }
        }

        var workers = Enumerable.Range(0, streamCount)
            .Select(index => upload
                ? RunUploadStreamAsync(index, CountBytes, phaseToken)
                : RunDownloadStreamAsync(index, CountBytes, phaseToken))
            .ToArray();

        var measurementStarted = phaseStarted;
        var graceFinished = false;
        var bonusMilliseconds = 0d;
        var lastMbps = 0d;

        try
        {
            using var timer = new PeriodicTimer(options.SampleInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var phaseElapsed = Stopwatch.GetElapsedTime(phaseStarted);
                if (!graceFinished)
                {
                    if (phaseElapsed <= graceTime)
                    {
                        continue;
                    }

                    measurementStarted = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref measuredBytes, 0);
                    Volatile.Write(ref measurementEnabled, 1);
                    graceFinished = true;
                    continue;
                }

                var elapsed = Stopwatch.GetElapsedTime(measurementStarted);
                if (elapsed.TotalMilliseconds <= 0)
                {
                    continue;
                }

                var bytes = Math.Max(0, Interlocked.Read(ref measuredBytes));
                var bytesPerSecond = bytes / elapsed.TotalSeconds;
                lastMbps = bytesPerSecond * 8d * options.OverheadCompensationFactor / 1_000_000d;

                if (options.AutoShortenFastTests)
                {
                    var bonus = 5d * bytesPerSecond / 100_000d;
                    bonusMilliseconds += Math.Min(400d, bonus);
                }

                var effectiveMilliseconds = elapsed.TotalMilliseconds + bonusMilliseconds;
                var phaseProgress = Math.Clamp(
                    effectiveMilliseconds / options.MaxThroughputDuration.TotalMilliseconds,
                    0d,
                    1d);

                progress?.Report(new SpeedTestProgress(
                    upload ? SpeedTestStage.Upload : SpeedTestStage.Download,
                    phaseProgress,
                    Mbps: lastMbps));

                if (effectiveMilliseconds > options.MaxThroughputDuration.TotalMilliseconds)
                {
                    break;
                }
            }
        }
        finally
        {
            phaseCts.Cancel();
            await ObserveWorkersAsync(workers, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var duration = Stopwatch.GetElapsedTime(phaseStarted);
        var totalWireBytes = Math.Max(0, Interlocked.Read(ref wireBytes));
        if (!graceFinished || totalWireBytes == 0 || !double.IsFinite(lastMbps))
        {
            throw new IOException(upload
                ? "IS74 upload test did not transfer data."
                : "IS74 download test did not transfer data.");
        }

        return new ThroughputMeasurement(lastMbps, duration, totalWireBytes);
    }

    private async Task RunDownloadStreamAsync(
        int streamIndex,
        Action<int> countBytes,
        CancellationToken cancellationToken)
    {
        await DelayStreamStartAsync(streamIndex, cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        BuildUri("backend/garbage.php", ("ckSize", options.DownloadChunkSize.ToString())));
                    AddNoCacheHeaders(request.Headers);
                    using var response = await http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        await RetryPauseAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }
                        countBytes(read);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (HttpRequestException)
                {
                    await RetryPauseAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException) when (!cancellationToken.IsCancellationRequested)
                {
                    await RetryPauseAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task RunUploadStreamAsync(
        int streamIndex,
        Action<int> countBytes,
        CancellationToken cancellationToken)
    {
        await DelayStreamStartAsync(streamIndex, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("backend/empty.php"));
                AddNoCacheHeaders(request.Headers);
                request.Content = new CountingUploadContent(options.UploadRequestBytes, countBytes);
                request.Content.Headers.ContentEncoding.Add("identity");

                // LibreSpeed restarts the upload stream after the body has been sent.
                // The captured provider currently answers 413 to these large bodies;
                // that status does not invalidate bytes already transmitted.
                using var response = await http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException)
            {
                await RetryPauseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await RetryPauseAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DelayStreamStartAsync(int streamIndex, CancellationToken cancellationToken)
    {
        if (streamIndex <= 0 || options.MultistreamDelay <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(options.MultistreamDelay * streamIndex, cancellationToken).ConfigureAwait(false);
    }

    private Task DelayBetweenPhasesAsync(CancellationToken cancellationToken) =>
        options.InterPhaseDelay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(options.InterPhaseDelay, cancellationToken);

    private static async Task RetryPauseAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
    }

    private static async Task ObserveWorkersAsync(Task[] workers, CancellationToken callerToken)
    {
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            // Normal end of the internally-timed throughput phase.
        }
        catch (HttpRequestException) when (!callerToken.IsCancellationRequested)
        {
            // Individual streams are best-effort; aggregate validation below
            // decides whether enough data was transferred for a usable result.
        }
        catch (IOException) when (!callerToken.IsCancellationRequested)
        {
        }
    }

    private Uri BuildUri(string relativePath, params (string Key, string Value)[] values)
    {
        var uri = new Uri(options.BaseUri, relativePath);
        var builder = new UriBuilder(uri);
        var query = new List<string>
        {
            "r=" + Uri.EscapeDataString(Guid.NewGuid().ToString("N"))
        };
        query.AddRange(values.Select(value =>
            Uri.EscapeDataString(value.Key) + "=" + Uri.EscapeDataString(value.Value)));
        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    private static void AddNoCacheHeaders(HttpRequestHeaders headers)
    {
        headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        headers.Pragma.ParseAdd("no-cache");
    }

    private static void ValidateOptions(Is74SpeedTestOptions value)
    {
        if (!value.BaseUri.IsAbsoluteUri || value.BaseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("IS74 speed-test base URI must be absolute HTTPS.", nameof(value));
        }
        if (value.PingCount < 3 || value.MaxPingRetries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "At least three pings and one retry are required.");
        }
        if (value.DownloadStreams < 1 || value.UploadStreams < 1 ||
            value.DownloadStreams > 16 || value.UploadStreams > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Throughput stream counts must be between 1 and 16.");
        }
        if (value.MaxThroughputDuration <= TimeSpan.Zero || value.SampleInterval <= TimeSpan.Zero ||
            value.DownloadGraceTime < TimeSpan.Zero || value.UploadGraceTime < TimeSpan.Zero ||
            value.DownloadGraceTime >= value.MaxThroughputDuration ||
            value.UploadGraceTime >= value.MaxThroughputDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Invalid speed-test timing configuration.");
        }
        if (value.UploadRequestBytes < 1024 || value.UploadRequestBytes > 256L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Upload request size is outside the supported range.");
        }
        if (value.DownloadChunkSize < 1 || value.DownloadChunkSize > 1024 ||
            value.OverheadCompensationFactor <= 0 || !double.IsFinite(value.OverheadCompensationFactor))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Invalid throughput calibration configuration.");
        }
    }

    private sealed record LatencyMeasurement(double LatencyMs, double JitterMs, int SampleCount);
    private sealed record ThroughputMeasurement(double Mbps, TimeSpan Duration, long WireBytes);

    private sealed class CountingUploadContent(long length, Action<int> countBytes) : HttpContent
    {
        private static readonly byte[] Pattern = CreatePattern();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            long remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(Pattern.Length, remaining);
                await stream.WriteAsync(Pattern.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                countBytes(count);
                remaining -= count;
            }
        }

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = length;
            return true;
        }

        private static byte[] CreatePattern()
        {
            var data = new byte[64 * 1024];
            var random = new Random(0x1574);
            random.NextBytes(data);
            return data;
        }
    }
}
