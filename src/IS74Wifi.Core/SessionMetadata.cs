namespace IS74Wifi.Core;

public sealed record SessionMetadata
{
    public string? DeviceId { get; init; }
    public string? UserId { get; init; }
    public string? ProfileId { get; init; }
    public string? AccessBegin { get; init; }
    public string? AccessEnd { get; init; }
    public DateTimeOffset RegisteredAtUtc { get; init; }
}

public sealed class SessionMetadataStore(AppPaths paths, JsonFileStore json)
{
    public SessionMetadata? Load() => json.Read(paths.SessionMetaFile, PersistenceJsonContext.Default.SessionMetadata);

    public void Save(SessionMetadata metadata)
    {
        paths.EnsureDirectories();
        json.Write(paths.SessionMetaFile, metadata, PersistenceJsonContext.Default.SessionMetadata);
    }
}
