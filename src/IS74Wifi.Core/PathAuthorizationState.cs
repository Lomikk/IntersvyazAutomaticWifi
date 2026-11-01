using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IS74Wifi.Core;

public enum PathAuthorizationStatus
{
    Unknown = 0,
    Internet = 1,
    Captive = 2,
    Unreachable = 3,
    Ambiguous = 4,
    Disconnected = 5
}

public sealed record PathAuthorizationState
{
    public required NetworkPathIdentity Identity { get; init; }
    public string? AdapterName { get; init; }
    public string? Ssid { get; init; }
    public int InterfaceIndex { get; init; }
    public string? SourceIPv4 { get; init; }
    public string? GatewayIPv4 { get; init; }
    public DateTimeOffset? LastSeenUtc { get; init; }
    public DateTimeOffset? LastProbeUtc { get; init; }
    public DateTimeOffset? LastSuccessfulAuthUtc { get; init; }
    public DateTimeOffset? ExpectedExpiryUtc { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public string? LastAttemptReason { get; init; }
    public string? LastResult { get; init; }
    public bool? InternetConfirmed { get; init; }
    public int AutomaticStepOneAttempts { get; init; }
    public int PreStepFailureCount { get; init; }
    public DateTimeOffset? NextAutomaticRetryUtc { get; init; }
    public bool UserActionRequired { get; init; }
    public bool EdgeWatchActive { get; init; }
    // Idempotent per authorization window, including across worker restarts.
    public DateTimeOffset? ConnectivityErrorNotifiedExpiryUtc { get; init; }
    public PathAuthorizationStatus Status { get; init; }
}

public sealed record LegacyGlobalAuthorizationHint
{
    public DateTimeOffset MigratedAtUtc { get; init; }
    public DateTimeOffset? LastAuthUtc { get; init; }
    public DateTimeOffset? ExpectedExpiryUtc { get; init; }
    public string? LastResult { get; init; }
    public bool? InternetConfirmed { get; init; }
}

public sealed record PathAuthorizationStateDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public LegacyGlobalAuthorizationHint? LegacyGlobalHint { get; init; }
    public PathAuthorizationState[] Paths { get; init; } = [];
}

/// <summary>
/// Durable per-path authorization observations. The old global runtime expiry is
/// retained only as a diagnostic migration hint and is never assigned to an
/// arbitrary physical path.
/// </summary>
public sealed class PathAuthorizationStateStore(
    AppPaths paths,
    JsonFileStore json,
    RuntimeStateStore? legacyRuntimeState = null,
    TimeProvider? timeProvider = null)
{
    private readonly RuntimeStateStore legacyState = legacyRuntimeState ?? new RuntimeStateStore(paths, json);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly DiagnosticLogger logger = new(paths);
    private readonly string lockName = "Local\\IS74Wifi.PathState." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(paths.PathAuthorizationStateFile).ToUpperInvariant())));

    private string BackupPath => paths.PathAuthorizationStateFile + ".bak";

    public PathAuthorizationStateDocument Load() => WithStateLock(LoadLocked);

    private PathAuthorizationStateDocument LoadLocked()
    {
        Exception? primaryInvalid = null;
        PathAuthorizationStateDocument? primary = null;
        try
        {
            primary = ReadOptional(paths.PathAuthorizationStateFile);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            primaryInvalid = ex;
            logger.Write(DiagnosticLevel.Warn, $"path-state.read invalid=primary type={ex.GetType().Name}");
        }

        if (primary is not null)
        {
            return Normalize(primary);
        }

        Exception? backupInvalid = null;
        PathAuthorizationStateDocument? backup = null;
        try
        {
            backup = ReadOptional(BackupPath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            backupInvalid = ex;
            logger.Write(DiagnosticLevel.Warn, $"path-state.read invalid=backup type={ex.GetType().Name}");
        }

        if (backup is not null)
        {
            // Restore without rotating the broken primary into the good backup.
            // In particular, a transient IOException is never treated as missing.
            var restored = Normalize(backup);
            json.Write(paths.PathAuthorizationStateFile, restored,
                PersistenceJsonContext.Default.PathAuthorizationStateDocument);
            logger.Write(DiagnosticLevel.Warn, "path-state.recovered source=backup");
            return restored;
        }

        if (primaryInvalid is not null || backupInvalid is not null)
        {
            // Do not manufacture fresh state and overwrite unreadable history.
            throw new InvalidDataException(
                "Состояние сетевой авторизации повреждено, а проверенной резервной копии нет.",
                primaryInvalid ?? backupInvalid);
        }

        var initial = CreateInitialDocument();
        SaveLocked(initial);
        logger.Write(DiagnosticLevel.Info, "path-state.initialized source=legacy-hint");
        return initial;
    }

    private PathAuthorizationStateDocument? ReadOptional(string path)
    {
        try
        {
            var state = json.ReadStrict(path,
                PersistenceJsonContext.Default.PathAuthorizationStateDocument);
            if (state is null || state.Paths is null)
            {
                throw new InvalidDataException("Invalid path authorization state document.");
            }
            return state;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var source = string.Equals(path, BackupPath, StringComparison.OrdinalIgnoreCase)
                ? "backup" : "primary";
            logger.Write(DiagnosticLevel.Warn,
                $"path-state.read io-error source={source} type={ex.GetType().Name}");
            throw;
        }
    }

    public PathAuthorizationState? Find(NetworkPathIdentity identity) =>
        Load().Paths.FirstOrDefault(state => SameIdentity(state.Identity, identity));

    public PathAuthorizationState Observe(NetworkPathSnapshot path, DateTimeOffset? observedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        EnsurePhysical(path);
        var now = (observedAtUtc ?? clock.GetUtcNow()).ToUniversalTime();
        return Mutate(path.Identity, state =>
        {
            var bindingChanged = state is not null && PathAuthorizationSchedule.SnapshotChanged(state, path);
            var observed = SnapshotState(path, state, now) with
            {
                LastProbeUtc = bindingChanged ? null : state?.LastProbeUtc,
                ConnectivityErrorNotifiedExpiryUtc = bindingChanged ? null : state?.ConnectivityErrorNotifiedExpiryUtc,
                Status = bindingChanged || state?.Status == PathAuthorizationStatus.Disconnected
                    ? PathAuthorizationStatus.Unknown
                    : state?.Status ?? PathAuthorizationStatus.Unknown
            };
            return state is not null && !PathAuthorizationSchedule.SnapshotChanged(state, path)
                ? ClearObservedInternetRetry(observed) : observed;
        });
    }

    public PathAuthorizationState RecordProbe(
        NetworkPathSnapshot path,
        NetworkPathProbeResult probe,
        DateTimeOffset? probedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(probe);
        EnsurePhysical(path);
        if (!SameIdentity(path.Identity, probe.Identity))
        {
            throw new ArgumentException("Результат probe относится к другому сетевому пути.", nameof(probe));
        }

        var now = (probedAtUtc ?? clock.GetUtcNow()).ToUniversalTime();
        return Mutate(path.Identity, state => ClearObservedInternetRetry(SnapshotState(path, state, now) with
        {
            LastProbeUtc = now,
            Status = MapProbeStatus(probe.Status)
        }));
    }

    public PathAuthorizationState RecordConfirmedAuthorization(
        NetworkPathSnapshot path,
        DateTimeOffset authorizedAtUtc,
        DateTimeOffset expectedExpiryUtc,
        DateTimeOffset? confirmedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        EnsurePhysical(path);
        var authorizedAt = authorizedAtUtc.ToUniversalTime();
        var expiry = expectedExpiryUtc.ToUniversalTime();
        if (expiry <= authorizedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedExpiryUtc),
                "Ожидаемая граница должна быть позже подтвержденной авторизации.");
        }

        var now = (confirmedAtUtc ?? clock.GetUtcNow()).ToUniversalTime();
        return Mutate(path.Identity, state => SnapshotState(path, state, now) with
        {
            LastProbeUtc = now,
            LastSuccessfulAuthUtc = authorizedAt,
            ExpectedExpiryUtc = expiry,
            LastAttemptUtc = now,
            LastAttemptReason = "success",
            LastResult = "success",
            InternetConfirmed = true,
            AutomaticStepOneAttempts = 0,
            PreStepFailureCount = 0,
            NextAutomaticRetryUtc = null,
            UserActionRequired = false,
            EdgeWatchActive = false,
            ConnectivityErrorNotifiedExpiryUtc = null,
            Status = PathAuthorizationStatus.Internet
        });
    }


    public bool MarkConnectivityErrorNotified(NetworkPathSnapshot path, DateTimeOffset expiry)
    {
        ArgumentNullException.ThrowIfNull(path);
        EnsurePhysical(path);
        var updated = false;
        Mutate(path.Identity, previous =>
        {
            if (previous is null || previous.ExpectedExpiryUtc != expiry ||
                previous.ConnectivityErrorNotifiedExpiryUtc == expiry ||
                !PathAuthorizationSchedule.HasCompletedObservationWindow(previous) ||
                previous.Status is not (PathAuthorizationStatus.Unreachable or PathAuthorizationStatus.Ambiguous))
            {
                return previous ?? SnapshotState(path, null, clock.GetUtcNow());
            }
            updated = true;
            return previous with { ConnectivityErrorNotifiedExpiryUtc = expiry };
        });
        return updated;
    }

    public RuntimeState LoadRuntime(NetworkPathSnapshot path)
    {
        ArgumentNullException.ThrowIfNull(path);
        EnsurePhysical(path);
        var state = Find(path.Identity) ?? Observe(path);
        return ToRuntimeState(state);
    }

    public void SaveRuntime(NetworkPathSnapshot path, RuntimeState runtime)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(runtime);
        EnsurePhysical(path);
        var now = clock.GetUtcNow().ToUniversalTime();
        Mutate(path.Identity, previous => SnapshotState(path, previous, now) with
        {
            LastSuccessfulAuthUtc = runtime.LastAuthUtc?.ToUniversalTime(),
            ExpectedExpiryUtc = runtime.ExpectedExpiryUtc?.ToUniversalTime(),
            LastAttemptUtc = runtime.LastAttemptUtc?.ToUniversalTime(),
            LastAttemptReason = runtime.LastAttemptReason,
            LastResult = runtime.LastResult,
            InternetConfirmed = runtime.InternetConfirmed,
            AutomaticStepOneAttempts = runtime.AutomaticStepOneAttempts,
            PreStepFailureCount = runtime.PreStepFailureCount,
            NextAutomaticRetryUtc = runtime.NextAutomaticRetryUtc?.ToUniversalTime(),
            UserActionRequired = runtime.UserActionRequired,
            EdgeWatchActive = runtime.EdgeWatchActive,
            ConnectivityErrorNotifiedExpiryUtc = previous?.ExpectedExpiryUtc == runtime.ExpectedExpiryUtc
                ? previous?.ConnectivityErrorNotifiedExpiryUtc : null,
            Status = string.Equals(runtime.LastResult, "success", StringComparison.OrdinalIgnoreCase)
                ? PathAuthorizationStatus.Internet
                : previous?.Status ?? PathAuthorizationStatus.Unknown
        });
    }

    public static RuntimeState ToRuntimeState(PathAuthorizationState state) => new()
    {
        LastAuthUtc = state.LastSuccessfulAuthUtc,
        ExpectedExpiryUtc = state.ExpectedExpiryUtc,
        LastAttemptUtc = state.LastAttemptUtc,
        LastAttemptReason = state.LastAttemptReason,
        LastResult = state.LastResult,
        InternetConfirmed = state.InternetConfirmed,
        AutomaticStepOneAttempts = state.AutomaticStepOneAttempts,
        PreStepFailureCount = state.PreStepFailureCount,
        NextAutomaticRetryUtc = state.NextAutomaticRetryUtc,
        UserActionRequired = state.UserActionRequired,
        EdgeWatchActive = state.EdgeWatchActive
    };

    public void MarkMissingAsDisconnected(
        IReadOnlyCollection<NetworkPathIdentity> currentlyPresent)
    {
        ArgumentNullException.ThrowIfNull(currentlyPresent);
        WithStateLock(() =>
        {
            var document = RequireWritable(LoadLocked());
            var changed = false;
            var pathsState = document.Paths.Select(state =>
            {
                if (currentlyPresent.Any(identity => SameIdentity(identity, state.Identity)) ||
                    state.Status == PathAuthorizationStatus.Disconnected)
                {
                    return state;
                }

                changed = true;
                return state with { Status = PathAuthorizationStatus.Disconnected };
            }).ToArray();

            if (changed)
            {
                SaveLocked(document with { Paths = pathsState });
            }
            return true;
        });
    }

    private PathAuthorizationState Mutate(
        NetworkPathIdentity identity,
        Func<PathAuthorizationState?, PathAuthorizationState> update) =>
        WithStateLock(() =>
        {
            var document = RequireWritable(LoadLocked());
            var list = document.Paths.ToList();
            var index = list.FindIndex(state => SameIdentity(state.Identity, identity));
            var next = update(index >= 0 ? list[index] : null);
            if (index >= 0) list[index] = next;
            else list.Add(next);
            SaveLocked(document with { Paths = list.ToArray() });
            return next;
        });

    private PathAuthorizationStateDocument CreateInitialDocument()
    {
        var legacy = legacyState.Load();
        var hasLegacyAuthorization = legacy.LastAuthUtc is not null ||
                                     legacy.ExpectedExpiryUtc is not null ||
                                     string.Equals(legacy.LastResult, "success", StringComparison.OrdinalIgnoreCase);
        return new PathAuthorizationStateDocument
        {
            SchemaVersion = PathAuthorizationStateDocument.CurrentSchemaVersion,
            LegacyGlobalHint = hasLegacyAuthorization
                ? new LegacyGlobalAuthorizationHint
                {
                    MigratedAtUtc = clock.GetUtcNow().ToUniversalTime(),
                    LastAuthUtc = legacy.LastAuthUtc?.ToUniversalTime(),
                    ExpectedExpiryUtc = legacy.ExpectedExpiryUtc?.ToUniversalTime(),
                    LastResult = legacy.LastResult,
                    InternetConfirmed = legacy.InternetConfirmed
                }
                : null,
            Paths = []
        };
    }

    private static PathAuthorizationStateDocument Normalize(PathAuthorizationStateDocument document)
    {
        if (document.SchemaVersion > PathAuthorizationStateDocument.CurrentSchemaVersion)
        {
            return document;
        }

        return document with
        {
            SchemaVersion = PathAuthorizationStateDocument.CurrentSchemaVersion,
            Paths = document.Paths ?? []
        };
    }

    private static PathAuthorizationStateDocument RequireWritable(PathAuthorizationStateDocument document)
    {
        if (document.SchemaVersion > PathAuthorizationStateDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Неподдерживаемая версия path authorization state: {document.SchemaVersion}.");
        }
        return Normalize(document);
    }

    private void SaveLocked(PathAuthorizationStateDocument document)
    {
        paths.EnsureDirectories();
        json.WriteWithBackup(paths.PathAuthorizationStateFile, BackupPath, document,
            PersistenceJsonContext.Default.PathAuthorizationStateDocument);
    }

    private T WithStateLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, name: lockName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
                logger.Write(DiagnosticLevel.Warn, "path-state.lock abandoned=true");
            }

            if (!acquired)
            {
                throw new IOException("Timed out waiting for path authorization state lock.");
            }

            return action();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static PathAuthorizationState SnapshotState(
        NetworkPathSnapshot path,
        PathAuthorizationState? previous,
        DateTimeOffset now) => new()
    {
        Identity = path.Identity,
        AdapterName = path.Name,
        Ssid = path.Ssid,
        InterfaceIndex = path.InterfaceIndex,
        SourceIPv4 = path.SourceIPv4?.ToString(),
        GatewayIPv4 = path.GatewayIPv4?.ToString(),
        LastSeenUtc = now,
        LastProbeUtc = previous?.LastProbeUtc,
        LastSuccessfulAuthUtc = previous?.LastSuccessfulAuthUtc,
        ExpectedExpiryUtc = previous?.ExpectedExpiryUtc,
        LastAttemptUtc = previous?.LastAttemptUtc,
        LastAttemptReason = previous?.LastAttemptReason,
        LastResult = previous?.LastResult,
        InternetConfirmed = previous?.InternetConfirmed,
        AutomaticStepOneAttempts = previous?.AutomaticStepOneAttempts ?? 0,
        PreStepFailureCount = previous?.PreStepFailureCount ?? 0,
        NextAutomaticRetryUtc = previous?.NextAutomaticRetryUtc,
        UserActionRequired = previous?.UserActionRequired ?? false,
        EdgeWatchActive = previous?.EdgeWatchActive ?? false,
        ConnectivityErrorNotifiedExpiryUtc = previous?.ConnectivityErrorNotifiedExpiryUtc,
        Status = previous?.Status ?? PathAuthorizationStatus.Unknown
    };

    private static PathAuthorizationStatus MapProbeStatus(NetworkPathProbeStatus status) => status switch
    {
        NetworkPathProbeStatus.Internet => PathAuthorizationStatus.Internet,
        NetworkPathProbeStatus.Captive => PathAuthorizationStatus.Captive,
        NetworkPathProbeStatus.Unreachable => PathAuthorizationStatus.Unreachable,
        NetworkPathProbeStatus.Ambiguous => PathAuthorizationStatus.Ambiguous,
        _ => PathAuthorizationStatus.Unknown
    };

    private static PathAuthorizationState ClearObservedInternetRetry(PathAuthorizationState state)
    {
        if (PathAuthorizationSchedule.HasObservedInternetPastExpiry(state))
        {
            // Internet cancels speculative portal retries immediately, NOT the
            // 3-minute observation window after expiry.
            return state with
            {
                NextAutomaticRetryUtc = null,
                PreStepFailureCount = 0,
                EdgeWatchActive = state.EdgeWatchActive && !PathAuthorizationSchedule.HasCompletedObservationWindow(state)
            };
        }
        if (PathAuthorizationSchedule.HasCompletedObservationWindow(state))
        {
            // An unresolved probe also ends the finite watch. User intervention
            // is offered via a one-off notification, not speculative stepOne.
            return state with { NextAutomaticRetryUtc = null, EdgeWatchActive = false, PreStepFailureCount = 0 };
        }
        return state;
    }

    private static void EnsurePhysical(NetworkPathSnapshot path)
    {
        if (!path.IsPhysicalCandidate)
        {
            throw new DirectNetworkUnavailableException("VPN/виртуальный интерфейс не получает состояние captive-авторизации.");
        }
    }

    private static bool SameIdentity(NetworkPathIdentity left, NetworkPathIdentity right) =>
        left.InterfaceType == right.InterfaceType &&
        string.Equals(left.AdapterId, right.AdapterId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.NetworkDiscriminator, right.NetworkDiscriminator, StringComparison.OrdinalIgnoreCase);
}
