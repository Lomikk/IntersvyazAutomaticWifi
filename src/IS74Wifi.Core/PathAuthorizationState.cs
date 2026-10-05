using System.Net.NetworkInformation;

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

    public PathAuthorizationStateDocument Load()
    {
        var existing = json.Read(paths.PathAuthorizationStateFile, PersistenceJsonContext.Default.PathAuthorizationStateDocument);
        if (existing is not null)
        {
            return Normalize(existing);
        }

        var initial = CreateInitialDocument();
        if (!File.Exists(paths.PathAuthorizationStateFile))
        {
            Save(initial);
        }
        return initial;
    }

    public PathAuthorizationState? Find(NetworkPathIdentity identity) =>
        Load().Paths.FirstOrDefault(state => SameIdentity(state.Identity, identity));

    public PathAuthorizationState Observe(NetworkPathSnapshot path, DateTimeOffset? observedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        EnsurePhysical(path);
        var now = (observedAtUtc ?? clock.GetUtcNow()).ToUniversalTime();
        return Mutate(path.Identity, state => SnapshotState(path, state, now) with
        {
            Status = state?.Status == PathAuthorizationStatus.Disconnected
                ? PathAuthorizationStatus.Unknown
                : state?.Status ?? PathAuthorizationStatus.Unknown
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
        return Mutate(path.Identity, state => SnapshotState(path, state, now) with
        {
            LastProbeUtc = now,
            Status = MapProbeStatus(probe.Status)
        });
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
            Status = PathAuthorizationStatus.Internet
        });
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
        var document = RequireWritable(Load());
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
            Save(document with { Paths = pathsState });
        }
    }

    private PathAuthorizationState Mutate(
        NetworkPathIdentity identity,
        Func<PathAuthorizationState?, PathAuthorizationState> update)
    {
        var document = RequireWritable(Load());
        var list = document.Paths.ToList();
        var index = list.FindIndex(state => SameIdentity(state.Identity, identity));
        var next = update(index >= 0 ? list[index] : null);
        if (index >= 0) list[index] = next;
        else list.Add(next);
        Save(document with { Paths = list.ToArray() });
        return next;
    }

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

    private void Save(PathAuthorizationStateDocument document)
    {
        paths.EnsureDirectories();
        json.Write(paths.PathAuthorizationStateFile, document, PersistenceJsonContext.Default.PathAuthorizationStateDocument);
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