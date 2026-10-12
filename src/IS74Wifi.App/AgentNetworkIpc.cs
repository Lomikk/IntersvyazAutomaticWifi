using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using IS74Wifi.Core;

namespace IS74Wifi.App;

internal sealed record AgentNetworkRefreshResult(
    bool? InternetAvailable,
    int PathCount,
    int CaptivePathCount);

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
    Task<AgentNetworkRefreshResult> RefreshAsync(
        bool allowAutomaticAuthorization,
        CancellationToken cancellationToken = default);

    Task<AuthorizationOutcome> AuthorizeNowAsync(
        Action<AuthorizationProgressStage>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns authorization-related network operations for both the long-lived agent
/// and the short-lived on-demand agent worker. Interactive processes only use
/// AgentNetworkClient and never probe captive paths or run stepOne/stepTwo.
/// </summary>
internal sealed class AgentNetworkOperations(
    ApplicationRuntime app,
    Func<bool> automaticAuthorizationEnabled) : IAgentNetworkOperations
{
    private static readonly TimeSpan DiagnosticProbeTimeout = TimeSpan.FromSeconds(3);

    public async Task<AgentNetworkRefreshResult> RefreshAsync(
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
            // Diagnostics established that a captive path exists. The agent still
            // performs its own normal probe/confirmation before any SMS-producing
            // authorization flow, so an interactive refresh never becomes a second
            // authorization implementation.
            app.Agent.NotifyNetworkConnectivityChanged();
            await app.Agent.TickAsync(cancellationToken).ConfigureAwait(false);
        }

        var finalStates = diagnostics
            .Select(item => app.PathState.Find(item.Path.Identity) ?? item.State)
            .ToArray();
        var internetAvailable = finalStates.Length == 0
            ? null
            : finalStates.Any(state => state.Status == PathAuthorizationStatus.Internet);
        return new AgentNetworkRefreshResult(
            internetAvailable,
            diagnostics.Count,
            finalStates.Count(state => state.Status == PathAuthorizationStatus.Captive));
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

    private static string Safe(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}

internal static class AgentNetworkProtocol
{
    internal const string Refresh = "refresh";
    internal const string ProbeOnly = "probe-only";
    internal const string Authorize = "authorize";
    internal const string StatusPrefix = "status|";
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

    internal static string FormatStatus(AgentNetworkRefreshResult result) =>
        $"{StatusPrefix}{FormatNullableBool(result.InternetAvailable)}|{result.PathCount}|{result.CaptivePathCount}";

    internal static AgentNetworkRefreshResult ParseStatus(string line)
    {
        var fields = line.Split('|');
        if (fields.Length != 4 || !string.Equals(fields[0], "status", StringComparison.Ordinal) ||
            !int.TryParse(fields[2], out var pathCount) || pathCount < 0 ||
            !int.TryParse(fields[3], out var captivePathCount) || captivePathCount < 0 ||
            captivePathCount > pathCount)
        {
            throw new InvalidDataException("Фоновый агент вернул некорректный статус сети.");
        }

        bool? internet = fields[1] switch
        {
            "1" => true,
            "0" => false,
            "-" => null,
            _ => throw new InvalidDataException("Фоновый агент вернул некорректный статус Интернета.")
        };
        return new AgentNetworkRefreshResult(internet, pathCount, captivePathCount);
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

    internal static Task<AgentNetworkRefreshResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        ExecuteRefreshAsync(AgentNetworkProtocol.Refresh, cancellationToken);

    internal static Task<AgentNetworkRefreshResult> ProbeOnlyAsync(CancellationToken cancellationToken = default) =>
        ExecuteRefreshAsync(AgentNetworkProtocol.ProbeOnly, cancellationToken);

    internal static Task<AuthorizationOutcome> AuthorizeNowAsync(
        Action<AuthorizationProgressStage>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAuthorizationAsync(progress, cancellationToken);

    private static async Task<AgentNetworkRefreshResult> ExecuteRefreshAsync(
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