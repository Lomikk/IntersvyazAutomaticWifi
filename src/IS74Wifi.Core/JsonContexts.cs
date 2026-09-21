using System.Text.Json.Serialization;

namespace IS74Wifi.Core;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(RuntimeState))]
[JsonSerializable(typeof(SessionMetadata))]
[JsonSerializable(typeof(HostAddressCacheDocument))]
[JsonSerializable(typeof(StoredSecrets))]
[JsonSerializable(typeof(TelemetryUploadState))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfirmationRequestPayload))]
[JsonSerializable(typeof(DeviceMetadataRequestPayload))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
