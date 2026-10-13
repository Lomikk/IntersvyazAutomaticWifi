using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using IS74Wifi.Core;

namespace IS74Wifi.App;

internal static class AgentNetworkOperationGate
{
    internal static async Task<NamedSemaphoreLease> AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lease = NamedSemaphoreLease.TryAcquire(AgentProcessControl.NetworkOperationGateName);
            if (lease is not null)
            {
                return lease;
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal interface IAgentNetworkOperations
{
    Task<AgentNetworkSnapshot> RefreshAsync(
        bool allowAutomaticAuthorization,
        CancellationToken cancellationToken = default);

    Task<AgentNetworkAdapterOption[]> GetAdapterOptionsAsync(
        CancellationToken cancellationToken = default);

    Task<AuthorizationOutcome> AuthorizeNowAsync(
        Action<AuthorizationProgressStage>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns authorization-related network operations and the canonical network/auth
/// snapshot for both the long-lived agent and the short-lived on-demand worker.
/// Interactive processes only use AgentNetworkClient and the persisted snapshot;
/// they never enumerate/probe captive paths or mutate PathAuthorizationState.
/// </summary>
internal sealed class AgentNetworkOperations(
    ApplicationRuntime app,
    Func<bool> automaticAuthorizationEnabled) : IAgentNetworkOperations
{
    private static readonly TimeSpan DiagnosticProbeTimeout = TimeSpan.FromSeconds(3);
    private readonly AgentNetworkSnapshotStore snapshotStore = new(app.Paths, app.Json);

    public async Task<AgentNetworkSnapshot> RefreshAsync(
        bool allowAutomaticAuthorization,
        CancellationToken cancellationToken = default)
    {
        using var crossProcessGate = await AgentNetworkOperationGate
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        var diagnostics = await app.NetworkPathDiagnostics
            .InspectAsync(DiagnosticProbeTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (allowAutomaticAuthorization &&
            automaticAuthorizationEnabled() &&
            diagnostics.Any(item => item.Status == NetworkPathProbeStatus.Captive))
        {
            app.Agent.NotifyNetworkConnectivityChanged();
            await app.Agent.TickAsync(cancellationToken).ConfigureAwait(false);
        }

        return CaptureAndPersistSnapshot();
    }

    public Task<AgentNetworkAdapterOption[]> GetAdapterOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = PhysicalAdapterSelection.Enumerate()
            .Select(adapter => new AgentNetworkAdapterOption(
                adapter.Id,
                adapter.Name,
                adapter.Ssid,
                adapter.CanConnect,
                adapter.LooksVirtual))
            .ToArray();
        return Task.FromResult(options);
    }

    public async Task<AuthorizationOutcome> AuthorizeNowAsync(
        Action<AuthorizationProgressStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var crossProcessGate = await AgentNetworkOperationGate
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        app.Logger.Write(DiagnosticLevel.Info, "agent.ipc authorize requested runtime=csharp");
        var secrets = app.Secrets.Load() ??
            throw new InvalidOperationException("Устройство не зарегистрировано. Сначала выполните register.");
        var deviceId = app.DeviceIdentity.GetOrCreate();

        var selection = app.ManualAuthorizationPathSelector.Select(app.Settings.DirectNetworkAdapterId);
        if (selection is null)
        {
            app.Logger.Write(DiagnosticLevel.Warn,
                "manual.path-selection unavailable reason=no-unambiguous-physical-path");
            CaptureAndPersistSnapshot();
            return new AuthorizationOutcome(
                AuthorizationOutcomeKind.RetryableBeforeStepOne,
                InternetConfirmed: null,
                AuthorizedAtUtc: null,
                RetryAfter: null,
                Timing: null);
        }

        var stateBefore = app.PathState.Find(selection.Path.Identity) ?? app.PathState.Observe(selection.Path);
        app.Logger.Write(DiagnosticLevel.Info,
            $"manual.path-selected adapter={Safe(selection.Path.Name)} preferred={selection.Preferred} probeGate=none");
        try
        {
            return await app.PathAuthorization.RunAsync(
                selection.Path,
                new AuthorizationRequest(
                    secrets.Token,
                    secrets.Phone,
                    deviceId,
                    AuthorizationAttemptReason.Manual,
                    Force: true,
                    Progress: progress),
                new PathAuthorizationExecutionContext(
                    selection.Preferred,
                    NetworkVpnDetector.IsActive(),
                    stateBefore),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CaptureAndPersistSnapshot();
        }
    }

    internal AgentNetworkSnapshot CaptureAndPersistSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var adapters = PhysicalAdapterSelection.Enumerate();
        var paths = adapters
            .Select(NetworkPathSnapshot.FromAdapter)
            .Where(path => path.IsAutomaticAuthorizationCandidate && path.CanAutomaticallyAuthorize)
            .GroupBy(path => path.Identity)
            .Select(group => group.First())
            .ToArray();
        var preferred = new PreferredNetworkPathResolver().Resolve(paths);
        var state = app.PathState.Load();
        var schedulingActive = automaticAuthorizationEnabled() && AgentProcessControl.IsAgentRunning();

        var pathSnapshots = paths
            .OrderByDescending(path => preferred is not null && SameIdentity(path.Identity, preferred))
            .ThenBy(path => path.Ssid ?? path.Name, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var pathState = state.Paths.FirstOrDefault(item => SameIdentity(item.Identity, path.Identity));
                return new AgentNetworkPathSnapshot(
                    Name: path.IsWifi ? $"Wi-Fi · {path.Ssid ?? path.Name}" : path.Name,
                    Ssid: path.Ssid,
                    InterfaceIndex: path.InterfaceIndex,
                    SourceIPv4: path.SourceIPv4?.ToString(),
                    GatewayIPv4: path.GatewayIPv4?.ToString(),
                    Status: pathState?.Status ?? PathAuthorizationStatus.Unknown,
                    Preferred: preferred is not null && SameIdentity(path.Identity, preferred),
                    ExpectedExpiryUtc: pathState?.ExpectedExpiryUtc,
                    LastResult: pathState?.LastResult,
                    LastAttemptUtc: pathState?.LastAttemptUtc,
                    NextCheckUtc: PathAuthorizationSchedule.GetNextCheckUtc(
                        pathState,
                        path,
                        app.Settings,
                        now,
                        schedulingActive),
                    CheckOnConnectivityEvent: schedulingActive && pathState is not null &&
                        PathAuthorizationSchedule.IsConnectivityEventDriven(pathState, path),
                    LastSuccessfulAuthUtc: pathState?.LastSuccessfulAuthUtc,
                    UserActionRequired: pathState?.UserActionRequired == true,
                    AuthorizationWindowApproximate: IsAuthorizationWindowApproximate(path),
                    EdgeWatchActive: pathState?.EdgeWatchActive == true);
            })
            .ToArray();

        var wifiSsid = adapters
            .Where(adapter => adapter.IsWifi && !string.IsNullOrWhiteSpace(adapter.Ssid))
            .Select(adapter => adapter.Ssid)
            .FirstOrDefault(SsidPolicy.IsTarget) ??
            adapters.Where(adapter => adapter.IsWifi && !string.IsNullOrWhiteSpace(adapter.Ssid))
                .Select(adapter => adapter.Ssid)
                .FirstOrDefault();
        bool? internetAvailable = pathSnapshots.Length == 0
            ? null
            : pathSnapshots.Any(path => path.Status == PathAuthorizationStatus.Internet);
        var snapshot = new AgentNetworkSnapshot(
            CapturedAtUtc: now,
            InternetAvailable: internetAvailable,
            VpnActive: SafeVpnActive(),
            WifiSsid: wifiSsid,
            Paths: pathSnapshots);
        snapshotStore.Save(snapshot);
        return snapshot;
    }

    internal static bool IsAuthorizationWindowApproximate(NetworkPathSnapshot path) =>
        !(path.IsWifi && SsidPolicy.IsTarget(path.Ssid));

    private static bool SameIdentity(NetworkPathIdentity left, NetworkPathIdentity right) =>
        left.InterfaceType == right.InterfaceType &&
        string.Equals(left.AdapterId, right.AdapterId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.NetworkDiscriminator, right.NetworkDiscriminator, StringComparison.OrdinalIgnoreCase);

    private static bool SafeVpnActive()
    {
        try { return NetworkVpnDetector.IsActive(); }
        catch { return false; }
    }

    private static string Safe(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}

internal static class AgentNetworkProtocol
{
    internal const string Refresh = "refresh";
    internal const string ProbeOnly = "probe-only";
    internal const string Authorize = "authorize";
    internal const string Adapters = "adapters";
    internal const string StatusPrefix = "status|";
    internal const string AdaptersPrefix = "adapters|";
    internal const string ProgressPrefix = "progress|";
    internal const string ResultPrefix = "result|";
    internal const string ErrorPrefix = "error|";

    internal static string EncodeError(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        return ErrorPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
    }

    internal static string DecodeError(string line)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(line[ErrorPrefix.Length..]));
        }
        catch (FormatException)
        {
            return "Фоновый агент вернул некорректную ошибку.";
        }
    }

    internal static string FormatStatus(AgentNetworkSnapshot snapshot)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, AppJsonContext.Default.AgentNetworkSnapshot);
        return StatusPrefix + Convert.ToBase64String(json);
    }

    internal static AgentNetworkSnapshot ParseStatus(string line)
    {
        if (!line.StartsWith(StatusPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Фоновый агент вернул некорректный статус сети.");
        }

        try
        {
            var bytes = Convert.FromBase64String(line[StatusPrefix.Length..]);
            return JsonSerializer.Deserialize(bytes, AppJsonContext.Default.AgentNetworkSnapshot) ??
                throw new InvalidDataException("Фоновый агент вернул пустой статус сети.");
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Фоновый агент вернул некорректный статус сети.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Фоновый агент вернул некорректный статус сети.", ex);
        }
    }

    internal static string FormatAdapters(AgentNetworkAdapterOption[] adapters)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new AgentNetworkAdapterList(adapters),
            AppJsonContext.Default.AgentNetworkAdapterList);
        return AdaptersPrefix + Convert.ToBase64String(json);
    }

    internal static AgentNetworkAdapterOption[] ParseAdapters(string line)
    {
        if (!line.StartsWith(AdaptersPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Фоновый агент вернул некорректный список адаптеров.");
        }

        try
        {
            var bytes = Convert.FromBase64String(line[AdaptersPrefix.Length..]);
            return JsonSerializer.Deserialize(bytes, AppJsonContext.Default.AgentNetworkAdapterList)?.Adapters ?? [];
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Фоновый агент вернул некорректный список адаптеров.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Фоновый агент вернул некорректный список адаптеров.", ex);
        }
    }

    internal static string FormatResult(AuthorizationOutcome outcome) =>
        $"{ResultPrefix}{outcome.Kind}|{FormatNullableBool(outcome.InternetConfirmed)}";

    internal static AuthorizationOutcome ParseResult(string line)
    {
        var fields = line.Split('|');
        if (fields.Length != 3 ||
            !string.Equals(fields[0], "result", StringComparison.Ordinal) ||
            !Enum.TryParse<AuthorizationOutcomeKind>(fields[1], ignoreCase: false, out var kind))
        {
            throw new InvalidDataException("Фоновый агент вернул некорректный результат авторизации.");
        }

        bool? internet = fields[2] switch
        {
            "1" => true,
            "0" => false,
            "-" => null,
            _ => throw new InvalidDataException("Фоновый агент вернул некорректный статус Интернета.")
        };

        return new AuthorizationOutcome(
            kind,
            internet,
            AuthorizedAtUtc: null,
            RetryAfter: null,
            Timing: null);
    }

    private static string FormatNullableBool(bool? value) => value switch
    {
        true => "1",
        false => "0",
        null => "-"
    };
}

internal static class AgentNetworkCommandServer
{
    internal static async Task RunLoopAsync(
        string pipeName,
        IAgentNetworkOperations operations,
        SemaphoreSlim operationGate,
        DiagnosticLogger logger,
        CancellationToken cancellationToken,
        Action? commandCompleted = null)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunSingleAsync(
                    pipeName,
                    operations,
                    operationGate,
                    cancellationToken,
                    commandCompleted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Write(DiagnosticLevel.Warn,
                    $"agent.ipc server error={ex.GetType().Name}:{ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    internal static async Task RunSingleAsync(
        string pipeName,
        IAgentNetworkOperations operations,
        SemaphoreSlim operationGate,
        CancellationToken cancellationToken,
        Action? commandCompleted = null)
    {
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true
        };

        var command = await reader.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(command))
        {
            await writer.WriteLineAsync(AgentNetworkProtocol.EncodeError(
                new InvalidDataException("Пустая команда фонового агента."))).ConfigureAwait(false);
            return;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (command)
            {
                case AgentNetworkProtocol.Refresh:
                {
                    var result = await operations.RefreshAsync(
                        allowAutomaticAuthorization: true,
                        cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync(AgentNetworkProtocol.FormatStatus(result)).ConfigureAwait(false);
                    break;
                }

                case AgentNetworkProtocol.ProbeOnly:
                {
                    var result = await operations.RefreshAsync(
                        allowAutomaticAuthorization: false,
                        cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync(AgentNetworkProtocol.FormatStatus(result)).ConfigureAwait(false);
                    break;
                }

                case AgentNetworkProtocol.Adapters:
                {
                    var adapters = await operations.GetAdapterOptionsAsync(cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync(AgentNetworkProtocol.FormatAdapters(adapters)).ConfigureAwait(false);
                    break;
                }

                case AgentNetworkProtocol.Authorize:
                    await RunAuthorizationAsync(operations, writer, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    await writer.WriteLineAsync(AgentNetworkProtocol.EncodeError(
                        new InvalidDataException("Неизвестная команда фонового агента."))).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                await writer.WriteLineAsync(AgentNetworkProtocol.EncodeError(ex)).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The interactive caller may have exited. The server remains healthy.
            }
        }
        finally
        {
            operationGate.Release();
            try
            {
                commandCompleted?.Invoke();
            }
            catch
            {
                // Scheduler wake is best-effort and must not break the IPC host.
            }
        }
    }

    private static async Task RunAuthorizationAsync(
        IAgentNetworkOperations operations,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        var progress = new ConcurrentQueue<AuthorizationProgressStage>();
        var operation = operations.AuthorizeNowAsync(progress.Enqueue, cancellationToken);
        var pipeWritable = true;

        async Task DrainProgressAsync()
        {
            while (progress.TryDequeue(out var stage))
            {
                if (!pipeWritable)
                {
                    continue;
                }

                try
                {
                    await writer.WriteLineAsync(AgentNetworkProtocol.ProgressPrefix + stage).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Never interrupt an in-flight captive transaction merely
                    // because the UI process disappeared.
                    pipeWritable = false;
                }
            }
        }

        while (!operation.IsCompleted)
        {
            await DrainProgressAsync().ConfigureAwait(false);
            await Task.WhenAny(operation, Task.Delay(50, CancellationToken.None)).ConfigureAwait(false);
        }

        await DrainProgressAsync().ConfigureAwait(false);
        var outcome = await operation.ConfigureAwait(false);
        if (pipeWritable)
        {
            await writer.WriteLineAsync(AgentNetworkProtocol.FormatResult(outcome)).ConfigureAwait(false);
        }
    }
}

internal sealed class AgentNetworkPipeUnavailableException : IOException
{
    internal AgentNetworkPipeUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal static class AgentNetworkClient
{
    private static readonly TimeSpan ExistingAgentConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OneShotConnectTimeout = TimeSpan.FromSeconds(8);

    internal static Task<AgentNetworkSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
        ExecuteRefreshAsync(AgentNetworkProtocol.Refresh, cancellationToken);

    internal static Task<AgentNetworkSnapshot> ProbeOnlyAsync(CancellationToken cancellationToken = default) =>
        ExecuteRefreshAsync(AgentNetworkProtocol.ProbeOnly, cancellationToken);

    internal static Task<AgentNetworkAdapterOption[]> GetAdapterOptionsAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAdaptersAsync(cancellationToken);

    internal static Task<AuthorizationOutcome> AuthorizeNowAsync(
        Action<AuthorizationProgressStage>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAuthorizationAsync(progress, cancellationToken);

    private static async Task<AgentNetworkSnapshot> ExecuteRefreshAsync(
        string command,
        CancellationToken cancellationToken)
    {
        return await ExecuteWithAgentAsync(
            async (pipeName, timeout) =>
            {
                using var pipe = await ConnectAsync(pipeName, timeout, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(command).ConfigureAwait(false);
                var line = await reader.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (line is not null && line.StartsWith(AgentNetworkProtocol.StatusPrefix, StringComparison.Ordinal))
                {
                    return AgentNetworkProtocol.ParseStatus(line);
                }
                if (line is not null && line.StartsWith(AgentNetworkProtocol.ErrorPrefix, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(AgentNetworkProtocol.DecodeError(line));
                }
                throw new IOException("Фоновый агент закрыл соединение без статуса сети.");
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AgentNetworkAdapterOption[]> ExecuteAdaptersAsync(
        CancellationToken cancellationToken)
    {
        return await ExecuteWithAgentAsync(
            async (pipeName, timeout) =>
            {
                using var pipe = await ConnectAsync(pipeName, timeout, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(AgentNetworkProtocol.Adapters).ConfigureAwait(false);
                var line = await reader.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (line is not null && line.StartsWith(AgentNetworkProtocol.AdaptersPrefix, StringComparison.Ordinal))
                {
                    return AgentNetworkProtocol.ParseAdapters(line);
                }
                if (line is not null && line.StartsWith(AgentNetworkProtocol.ErrorPrefix, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(AgentNetworkProtocol.DecodeError(line));
                }
                throw new IOException("Фоновый агент закрыл соединение без списка адаптеров.");
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AuthorizationOutcome> ExecuteAuthorizationAsync(
        Action<AuthorizationProgressStage>? progress,
        CancellationToken cancellationToken)
    {
        return await ExecuteWithAgentAsync(
            async (pipeName, timeout) =>
            {
                using var pipe = await ConnectAsync(pipeName, timeout, cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(AgentNetworkProtocol.Authorize).ConfigureAwait(false);

                while (true)
                {
                    var line = await reader.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        throw new IOException("Фоновый агент закрыл соединение до завершения авторизации.");
                    }
                    if (line.StartsWith(AgentNetworkProtocol.ProgressPrefix, StringComparison.Ordinal))
                    {
                        var value = line[AgentNetworkProtocol.ProgressPrefix.Length..];
                        if (Enum.TryParse<AuthorizationProgressStage>(value, ignoreCase: false, out var stage))
                        {
                            progress?.Invoke(stage);
                        }
                        continue;
                    }
                    if (line.StartsWith(AgentNetworkProtocol.ResultPrefix, StringComparison.Ordinal))
                    {
                        return AgentNetworkProtocol.ParseResult(line);
                    }
                    if (line.StartsWith(AgentNetworkProtocol.ErrorPrefix, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(AgentNetworkProtocol.DecodeError(line));
                    }

                    throw new InvalidDataException("Фоновый агент вернул неизвестный ответ.");
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ExecuteWithAgentAsync<T>(
        Func<string, TimeSpan, Task<T>> execute,
        CancellationToken cancellationToken)
    {
        if (AgentProcessControl.IsAgentRunning())
        {
            try
            {
                return await execute(
                    AgentProcessControl.NetworkCommandPipeName,
                    ExistingAgentConnectTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A wedged/mid-restart background worker must not force the UI
                // to perform network work itself. Fall through to a short-lived
                // agent-owned request worker instead.
            }
            catch (AgentNetworkPipeUnavailableException)
            {
                // Same fallback for a worker that exited between gate detection
                // and named-pipe connection. Once connected, I/O failures are
                // never retried automatically: authorize may already have sent
                // stepOne even if its final IPC response was lost.
            }
        }

        var oneShotPipeName = $"IS74Wifi.CSharp.AgentNetwork.Request.{Guid.NewGuid():N}";
        using var process = StartOneShotAgent(oneShotPipeName);
        try
        {
            return await execute(oneShotPipeName, OneShotConnectTimeout).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _ = process.WaitForExit(2000);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            return pipe;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            pipe.Dispose();
            throw new TimeoutException("Фоновый агент не ответил вовремя.");
        }
        catch (IOException ex)
        {
            pipe.Dispose();
            throw new AgentNetworkPipeUnavailableException(
                "Не удалось подключиться к фоновому агенту.",
                ex);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static Process StartOneShotAgent(string pipeName)
    {
        // Use the same executable/protocol version as the caller. Normal user
        // commands are already forwarded to the installed copy; explicit dev
        // diagnostics intentionally stay on the invoked build. Framework-
        // dependent `dotnet IS74Wifi.dll ...` runs need the DLL argument too.
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new InvalidOperationException("Не удалось найти исполняемый файл для сетевого агента.");
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (string.Equals(
                Path.GetFileNameWithoutExtension(executable),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssembly) || !File.Exists(entryAssembly))
            {
                throw new InvalidOperationException("Не удалось найти DLL текущей сборки для сетевого агента.");
            }
            startInfo.ArgumentList.Add(entryAssembly);
        }
        startInfo.ArgumentList.Add("agent-network-worker");
        startInfo.ArgumentList.Add(pipeName);
        return Process.Start(startInfo) ??
               throw new InvalidOperationException("Не удалось запустить сетевой агент по запросу.");
    }


}