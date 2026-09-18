using System.Text.Json.Serialization;

namespace IS74Wifi.App;

internal sealed record DeviceMetadataSnapshot(
    string DeviceId,
    string DeviceModel,
    string OsVersion,
    DateTimeOffset RegisteredAtUtc);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(DeviceMetadataSnapshot))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
