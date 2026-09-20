using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace IS74Wifi.Core;

public sealed class Is74ApiClient : IIs74PushClient
{
    private static readonly Uri ApiBase = new("https://api.is74.ru/");
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpTransport transport;

    public Is74ApiClient(HttpTransport transport)
    {
        this.transport = transport;
    }

    public async Task<Is74ApiResult<ConfirmationRequested>> RequestConfirmationAsync(
        string phone,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        const string operation = "auth.get-confirm";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase, "mobile/auth/get-confirm"));
        AddApiHeaders(request, deviceId);
        request.Content = JsonContent(
            new ConfirmationRequestPayload(phone, deviceId, 0),
            ApiJsonContext.Default.ConfirmationRequestPayload);

        var call = await transport.SendAsync(request, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        var failure = ClassifyFailure(call, operation);
        return failure is null
            ? Is74ApiResult<ConfirmationRequested>.Success(new ConfirmationRequested())
            : Is74ApiResult<ConfirmationRequested>.Fail(failure);
    }

    public async Task<Is74ApiResult<ConfirmationChecked>> CheckConfirmationAsync(
        string phone,
        string smsCode,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        const string operation = "auth.check-confirm";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase, "mobile/auth/check-confirm"));
        AddApiHeaders(request, deviceId);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["phone"] = phone,
            ["confirmCode"] = smsCode,
            ["authId"] = string.Empty
        });

        var call = await transport.SendAsync(request, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        var failure = ClassifyFailure(call, operation);
        if (failure is not null)
        {
            return Is74ApiResult<ConfirmationChecked>.Fail(failure);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(call.Response!.Body);
        }
        catch (JsonException)
        {
            return InvalidJson<ConfirmationChecked>(operation);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("authId", out var authIdElement))
            {
                return InvalidPayload<ConfirmationChecked>(operation);
            }

            var authId = ReadScalarString(authIdElement);
            if (string.IsNullOrWhiteSpace(authId))
            {
                return InvalidPayload<ConfirmationChecked>(operation);
            }

            // Campus Wi-Fi registration is phone/device scoped. Account/address
            // metadata returned by the shared mobile backend is intentionally ignored.
            return Is74ApiResult<ConfirmationChecked>.Success(new ConfirmationChecked(authId));
        }
    }

    public async Task<Is74ApiResult<Is74ApiSession>> GetTokenAsync(
        string authId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        const string operation = "auth.get-token";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase, "mobile/auth/get-token"));
        AddApiHeaders(request, deviceId, userId: "-1", profileId: "null");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["authId"] = authId,
            ["userId"] = string.Empty,
            ["uniqueDeviceId"] = deviceId
        });

        var call = await transport.SendAsync(request, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        var failure = ClassifyFailure(call, operation);
        if (failure is not null)
        {
            return Is74ApiResult<Is74ApiSession>.Fail(failure);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(call.Response!.Body);
        }
        catch (JsonException)
        {
            return InvalidJson<Is74ApiSession>(operation);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("TOKEN", out var tokenElement))
            {
                return InvalidPayload<Is74ApiSession>(operation);
            }

            var token = ReadScalarString(tokenElement);
            if (string.IsNullOrWhiteSpace(token))
            {
                return InvalidPayload<Is74ApiSession>(operation);
            }

            return Is74ApiResult<Is74ApiSession>.Success(new Is74ApiSession(
                token,
                GetOptionalScalar(document.RootElement, "USER_ID"),
                GetOptionalScalar(document.RootElement, "PROFILE_ID"),
                GetOptionalScalar(document.RootElement, "ACCESS_BEGIN"),
                GetOptionalScalar(document.RootElement, "ACCESS_END")));
        }
    }

    public async Task<Is74ApiResult<DeviceMetadataAccepted>> RegisterDeviceMetadataAsync(
        string bearerToken,
        DeviceMetadataRegistration metadata,
        CancellationToken cancellationToken = default)
    {
        const string operation = "device-metadata";
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(ApiBase, "mobile/pushtoken/add-with-device-id"));
        AddApiHeaders(request, metadata.DeviceId, bearerToken);
        request.Content = JsonContent(
            new DeviceMetadataRequestPayload(
                5,
                metadata.DeviceId,
                metadata.Phone,
                ProtocolContract.AppVersion,
                ProtocolContract.BuildCode,
                metadata.OsVersion,
                metadata.DeviceModel),
            ApiJsonContext.Default.DeviceMetadataRequestPayload);

        var call = await transport.SendAsync(request, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        var failure = ClassifyFailure(call, operation);
        return failure is null
            ? Is74ApiResult<DeviceMetadataAccepted>.Success(new DeviceMetadataAccepted())
            : Is74ApiResult<DeviceMetadataAccepted>.Fail(failure);
    }

    public async Task<Is74ApiResult<PushMessagePage>> GetPushMessagesAsync(
        string bearerToken,
        string deviceId,
        int pageSize,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        const string operation = "pushmessages";
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var uri = new Uri(ApiBase, $"mobile/pushmessages?page=1&pageSize={pageSize}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddApiHeaders(request, deviceId, bearerToken, noCache: true);

        var call = await transport.SendAsync(request, timeout ?? DefaultTimeout, cancellationToken).ConfigureAwait(false);
        var failure = ClassifyFailure(call, operation);
        if (failure is not null)
        {
            return Is74ApiResult<PushMessagePage>.Fail(failure);
        }

        var parseStatus = PushMessageParser.ParsePage(call.Response!.Body, out var page);
        if (parseStatus == PushPageParseStatus.InvalidJson)
        {
            return InvalidJson<PushMessagePage>(operation);
        }
        if (parseStatus == PushPageParseStatus.UnrecognizedSchema)
        {
            return InvalidPayload<PushMessagePage>(operation);
        }

        return Is74ApiResult<PushMessagePage>.Success(page);
    }

    public async Task<Is74ApiResult<PushBaseline>> GetBaselineAsync(
        string bearerToken,
        string deviceId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var pageResult = await GetPushMessagesAsync(
            bearerToken,
            deviceId,
            pageSize: 1,
            timeout: timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!pageResult.IsSuccess)
        {
            return Is74ApiResult<PushBaseline>.Fail(pageResult.Failure!);
        }

        var page = pageResult.Value!;
        if (page.Messages.Count > 0)
        {
            return Is74ApiResult<PushBaseline>.Success(new PushBaseline(page.Messages[0].Id));
        }

        if (page.KnownEmpty)
        {
            return Is74ApiResult<PushBaseline>.Success(new PushBaseline(0));
        }

        return InvalidPayload<PushBaseline>("push.baseline");
    }

    private static void AddApiHeaders(
        HttpRequestMessage request,
        string deviceId,
        string? token = null,
        bool noCache = false,
        string? userId = null,
        string? profileId = null)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json; version=v2");
        request.Headers.TryAddWithoutValidation("X-Device-Id", deviceId);
        request.Headers.TryAddWithoutValidation("X-Api-Source", "com.intersvyaz.lk");
        request.Headers.TryAddWithoutValidation("X-App-version", ProtocolContract.AppVersion);
        request.Headers.TryAddWithoutValidation("Platform", "Android");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            $"4.11.0 com.intersvyaz.lk/{ProtocolContract.AppVersion}.{ProtocolContract.BuildCode}");

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }
        if (noCache)
        {
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        }
        if (userId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Api-User-Id", userId);
        }
        if (profileId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Api-Profile-Id", profileId);
        }
    }

    private static StringContent JsonContent<T>(
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        new(JsonSerializer.Serialize(value, typeInfo), Encoding.UTF8, "application/json");

    private static Is74ApiFailure? ClassifyFailure(HttpCallResult call, string operation)
    {
        if (!call.TransportSucceeded)
        {
            return new Is74ApiFailure(
                Is74ApiFailureKind.Transport,
                operation,
                call.FailureKind);
        }

        var response = call.Response!;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new Is74ApiFailure(Is74ApiFailureKind.Unauthorized, operation, StatusCode: 401);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new Is74ApiFailure(
                Is74ApiFailureKind.HttpStatus,
                operation,
                StatusCode: (int)response.StatusCode);
        }

        return null;
    }

    private static Is74ApiResult<T> InvalidJson<T>(string operation) =>
        Is74ApiResult<T>.Fail(new Is74ApiFailure(Is74ApiFailureKind.InvalidJson, operation));

    private static Is74ApiResult<T> InvalidPayload<T>(string operation) =>
        Is74ApiResult<T>.Fail(new Is74ApiFailure(Is74ApiFailureKind.InvalidPayload, operation));


    private static string NormalizeJsonName(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? GetOptionalScalar(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? ReadScalarString(value) : null;

    private static string? ReadScalarString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
        _ => null
    };
}
