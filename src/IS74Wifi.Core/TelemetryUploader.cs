namespace IS74Wifi.Core;

public sealed class TelemetryUploader(
    TelemetryQueue queue,
    TelemetryUploadStateStore stateStore,
    TelemetryClient? client,
    AppSettings settings,
    DiagnosticLogger logger,
    Func<bool>? anonymousStatisticsAllowed = null,
    string uploaderVersion = "dev",
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

        if (!string.Equals(state.UploaderVersion, uploaderVersion, StringComparison.Ordinal))
        {
            // A release that changes telemetry recovery semantics gets one bounded
            // chance to shorten an inherited long client-side failure backoff. This
            // never bypasses a fresh server delay: once the current version receives
            // rate_limited/daily_limit_reached, the stored version matches and that
            // authoritative retry deadline is preserved.
            var recoveryAt = now + TimeSpan.FromMinutes(2);
            var nextAttempt = state.NextAttemptUtc;
            if (state.ConsecutiveFailures > 0 &&
                nextAttempt is { } scheduled &&
                scheduled > recoveryAt)
            {
                nextAttempt = recoveryAt;
                logger.Write(DiagnosticLevel.Info,
                    $"telemetry.upload recovery-scheduled fromVersion={state.UploaderVersion ?? "unknown"} toVersion={uploaderVersion} failures={state.ConsecutiveFailures}");
            }

            state = state with
            {
                NextAttemptUtc = nextAttempt,
                UploaderVersion = uploaderVersion
            };
            stateStore.Save(state);
        }
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
        var rejectedAny = false;
        TelemetryQueueBatch? lastSuccessfulBatch = null;
        var isolatePermanentFailure = state.IsolatingPermanentFailure;
        var maxRequests = Math.Clamp(settings.TelemetryMaxBatchesPerFlush, 1, 8);

        for (var index = 0; index < maxRequests; index++)
        {
            var batch = queue.ReadOldestBatch(maxFiles: isolatePermanentFailure ? 1 : int.MaxValue);
            if (batch is null)
            {
                break;
            }

            var result = await client.SendTelemetryBatchAsync(batch, timeout, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                if (IsPermanentContractError(result.Error))
                {
                    if (batch.Files.Count > 1)
                    {
                        // A batch may contain several independently queued traces. Do
                        // not quarantine all of them just because one event is invalid:
                        // isolate the oldest trace and identify the poison file first.
                        isolatePermanentFailure = true;
                        var isolateRetryAt = clock.GetUtcNow() + TimeSpan.FromMinutes(1);
                        stateStore.Save(new TelemetryUploadState(
                            state.LastSuccessfulUploadUtc,
                            isolateRetryAt,
                            0,
                            true,
                            uploaderVersion));
                        logger.Write(DiagnosticLevel.Warn,
                            $"telemetry.upload isolate reason={result.Error ?? "unknown"} files={batch.Files.Count} events={batch.EventJson.Count}");
                        continue;
                    }

                    queue.Reject(batch);
                    rejectedAny = true;
                    isolatePermanentFailure = false;
                    logger.Write(DiagnosticLevel.Warn,
                        $"telemetry.upload rejected reason={result.Error ?? "unknown"} files={batch.Files.Count} events={batch.EventJson.Count}");
                    continue;
                }

                var failures = Math.Min(20, state.ConsecutiveFailures + 1);
                var retry = GetRetryDelay(result, failures);
                stateStore.Save(new TelemetryUploadState(
                    state.LastSuccessfulUploadUtc,
                    clock.GetUtcNow() + retry,
                    failures,
                    isolatePermanentFailure,
                    uploaderVersion));
                logger.Write(DiagnosticLevel.Warn,
                    $"telemetry.upload deferred reason={result.Error ?? "unknown"} retryMinutes={retry.TotalMinutes:0}");
                return;
            }

            queue.Complete(batch);
            sentAny = true;
            lastSuccessfulBatch = batch;
        }

        if (sentAny || rejectedAny)
        {
            var completedAt = clock.GetUtcNow();
            DateTimeOffset? nextAttempt = queue.HasPending
                ? completedAt + (rejectedAny || isolatePermanentFailure
                    ? TimeSpan.FromMinutes(1)
                    : GetBacklogDrainDelay(lastSuccessfulBatch?.BatchId))
                : null;
            stateStore.Save(new TelemetryUploadState(
                sentAny ? completedAt : state.LastSuccessfulUploadUtc,
                nextAttempt,
                0,
                queue.HasPending && isolatePermanentFailure,
                uploaderVersion));
            logger.Write(DiagnosticLevel.Info,
                $"telemetry.upload progress sent={sentAny} rejected={rejectedAny} pending={queue.HasPending}");
        }
    }

    private static TimeSpan GetBacklogDrainDelay(string? batchId)
    {
        // Spread recovering clients across a small 1-3 minute window. The batch
        // identity is stable across retries but differs between installations/traces,
        // avoiding a synchronized post-update burst without using mutable randomness.
        if (!string.IsNullOrEmpty(batchId))
        {
            var c = char.ToLowerInvariant(batchId[^1]);
            var value = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                _ => 1
            };
            return TimeSpan.FromMinutes(1 + value % 3);
        }

        return TimeSpan.FromMinutes(2);
    }

    private static bool IsPermanentContractError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        if (error.StartsWith("invalid_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return error.ToLowerInvariant() is
            "missing_payload" or
            "payload_too_large" or
            "object_required" or
            "empty_payload" or
            "unknown_field" or
            "route_event_mismatch" or
            "duplicate_event_id_in_batch" or
            "path_context_requires_schema_5";
    }

    private static TimeSpan GetRetryDelay(TelemetryWriteResult result, int failures)
    {
        if (string.Equals(result.Error, "rate_limited", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromSeconds(Math.Clamp(result.RetryAfterSeconds ?? 60, 1, 21600));
        }

        if (string.Equals(result.Error, "daily_limit_reached", StringComparison.OrdinalIgnoreCase))
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
