namespace IS74Wifi.Core;

public sealed class TelemetryUploader(
    TelemetryQueue queue,
    TelemetryUploadStateStore stateStore,
    TelemetryClient? client,
    AppSettings settings,
    DiagnosticLogger logger,
    Func<bool>? anonymousStatisticsAllowed = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Func<bool> statisticsAllowed = anonymousStatisticsAllowed ??
        (() => settings.AnonymousStatisticsConsent == AnonymousStatisticsConsent.Allowed);

    public bool Enabled => client is not null && statisticsAllowed();

    public static TimeSpan GetHttpTimeout(AppSettings settings) => TimeSpan.FromMilliseconds(
        Math.Clamp(settings.TelemetryHttpTimeoutMilliseconds, 10000, 30000));

    public async Task TryFlushIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (client is null || !statisticsAllowed() || !queue.HasPending || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var state = stateStore.Load();
        var now = clock.GetUtcNow();
        if (state.NextAttemptUtc is { } next)
        {
            if (now < next)
            {
                return;
            }
        }
        else
        {
            var interval = TimeSpan.FromHours(Math.Clamp(settings.TelemetryUploadIntervalHours, 1, 72));
            if (state.LastSuccessfulUploadUtc is { } last && now - last < interval)
            {
                return;
            }
        }

        using var lease = NamedSemaphoreLease.TryAcquire("Local\\IS74Wifi.TelemetryUpload");
        if (lease is null)
        {
            return;
        }

        // Google Apps Script can take several seconds to acquire its sheet lock
        // and return the final redirect. Apply the floor at runtime as well:
        // existing settings.json files may still contain the old 1–3s value.
        var timeout = GetHttpTimeout(settings);
        var sentAny = false;
        var maxBatches = Math.Clamp(settings.TelemetryMaxBatchesPerFlush, 1, 8);

        for (var index = 0; index < maxBatches; index++)
        {
            var batch = queue.ReadOldestBatch();
            if (batch is null)
            {
                break;
            }

            var result = await client.SendTelemetryBatchAsync(batch, timeout, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                var failures = Math.Min(20, state.ConsecutiveFailures + 1);
                var retry = GetRetryDelay(result.Error, failures);
                stateStore.Save(new TelemetryUploadState(
                    state.LastSuccessfulUploadUtc,
                    now + retry,
                    failures));
                logger.Write(DiagnosticLevel.Warn,
                    $"telemetry.upload deferred reason={result.Error ?? "unknown"} retryMinutes={retry.TotalMinutes:0}");
                return;
            }

            queue.Complete(batch);
            sentAny = true;
        }

        if (sentAny)
        {
            DateTimeOffset? nextAttempt = queue.HasPending
                ? now + TimeSpan.FromHours(6)
                : null;
            stateStore.Save(new TelemetryUploadState(now, nextAttempt, 0));
            logger.Write(DiagnosticLevel.Info,
                $"telemetry.upload success pending={queue.HasPending}");
        }
    }

    private static TimeSpan GetRetryDelay(string? error, int failures)
    {
        if (string.Equals(error, "rate_limited", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(error, "daily_limit_reached", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromHours(6);
        }

        return failures switch
        {
            <= 1 => TimeSpan.FromMinutes(5),
            2 => TimeSpan.FromMinutes(30),
            3 => TimeSpan.FromHours(2),
            _ => TimeSpan.FromHours(6)
        };
    }
}
