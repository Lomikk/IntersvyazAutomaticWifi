using System.IO.Pipes;
using System.Text;
using IS74Wifi.App;
using IS74Wifi.Core;

internal static class AgentNetworkIpcContractTests
{
    public static async Task RunAsync()
    {
        await RefreshCommandsStayAgentOwnedAsync();
        await AdapterEnumerationStaysAgentOwnedAsync();
        await AuthorizationProgressAndResultRoundTripAsync();
        await AgentErrorsRoundTripAsync();
    }

    private static async Task RefreshCommandsStayAgentOwnedAsync()
    {
        var operations = new FakeOperations();
        var schedulerWakes = 0;
        await WithServerAsync(operations, async (pipe, reader, writer) =>
        {
            await writer.WriteLineAsync(AgentNetworkProtocol.Refresh);
            var line = await reader.ReadLineAsync();
            Assert(line is not null && line.StartsWith(AgentNetworkProtocol.StatusPrefix, StringComparison.Ordinal),
                "refresh IPC did not return network status");
            var status = AgentNetworkProtocol.ParseStatus(line!);
            Assert(status.InternetAvailable == true && status.Paths.Length == 2 && status.CaptivePathCount == 1,
                "refresh IPC snapshot payload changed");
            Assert(status.Paths[0].SourceIPv4 == "10.0.0.2" && status.Paths[1].Status == PathAuthorizationStatus.Captive,
                "refresh IPC must return agent-owned path details");
        }, () => schedulerWakes++);
        Assert(schedulerWakes == 1,
            "completed agent IPC command must wake the main scheduler to recompute deadlines");
        Assert(operations.RefreshCalls == 1 && operations.LastAllowAutomaticAuthorization == true,
            "ordinary UI refresh must ask the agent to refresh and permit configured autoauthorization");

        await WithServerAsync(operations, async (pipe, reader, writer) =>
        {
            await writer.WriteLineAsync(AgentNetworkProtocol.ProbeOnly);
            var line = await reader.ReadLineAsync();
            Assert(line is not null && line.StartsWith(AgentNetworkProtocol.StatusPrefix, StringComparison.Ordinal),
                "probe-only IPC did not return network status");
        });
        Assert(operations.RefreshCalls == 2 && operations.LastAllowAutomaticAuthorization == false,
            "diagnostic probe-only command must never authorize");
    }

    private static async Task AdapterEnumerationStaysAgentOwnedAsync()
    {
        var operations = new FakeOperations();
        await WithServerAsync(operations, async (pipe, reader, writer) =>
        {
            await writer.WriteLineAsync(AgentNetworkProtocol.Adapters);
            var line = await reader.ReadLineAsync();
            Assert(line is not null && line.StartsWith(AgentNetworkProtocol.AdaptersPrefix, StringComparison.Ordinal),
                "adapter IPC did not return agent-owned options");
            var adapters = AgentNetworkProtocol.ParseAdapters(line!);
            Assert(adapters.Length == 2 && adapters[0].Id == "wifi-1" && adapters[1].LooksVirtual,
                "adapter options did not round-trip through agent IPC");
        });
        Assert(operations.AdapterCalls == 1,
            "interactive adapter selection must enumerate interfaces only inside the agent owner");
    }

    private static async Task AuthorizationProgressAndResultRoundTripAsync()
    {
        var operations = new FakeOperations
        {
            AuthorizationResult = new AuthorizationOutcome(
                AuthorizationOutcomeKind.Success,
                InternetConfirmed: true,
                AuthorizedAtUtc: DateTimeOffset.UtcNow,
                RetryAfter: null,
                Timing: null)
        };

        var received = new List<string>();
        await WithServerAsync(operations, async (pipe, reader, writer) =>
        {
            await writer.WriteLineAsync(AgentNetworkProtocol.Authorize);
            while (true)
            {
                var line = await reader.ReadLineAsync();
                Assert(line is not null, "authorization IPC closed before final result");
                received.Add(line!);
                if (line!.StartsWith(AgentNetworkProtocol.ResultPrefix, StringComparison.Ordinal))
                {
                    break;
                }
            }
        });

        Assert(received.Contains(AgentNetworkProtocol.ProgressPrefix + AuthorizationProgressStage.BaselineLoaded),
            "authorization progress was not streamed through agent IPC");
        Assert(received.Contains(AgentNetworkProtocol.ProgressPrefix + AuthorizationProgressStage.InternetConfirmed),
            "final authorization progress was not streamed through agent IPC");
        var parsed = AgentNetworkProtocol.ParseResult(received[^1]);
        Assert(parsed.Kind == AuthorizationOutcomeKind.Success && parsed.InternetConfirmed == true,
            "authorization outcome did not round-trip through agent IPC");
        Assert(operations.AuthorizationCalls == 1,
            "agent authorization operation must execute exactly once");
    }

    private static async Task AgentErrorsRoundTripAsync()
    {
        var operations = new FakeOperations { RefreshError = new InvalidOperationException("fake agent failure") };
        await WithServerAsync(operations, async (pipe, reader, writer) =>
        {
            await writer.WriteLineAsync(AgentNetworkProtocol.Refresh);
            var line = await reader.ReadLineAsync();
            Assert(line is not null && line.StartsWith(AgentNetworkProtocol.ErrorPrefix, StringComparison.Ordinal),
                "agent exception must be returned as an IPC error");
            Assert(AgentNetworkProtocol.DecodeError(line!) == "fake agent failure",
                "agent IPC error message did not round-trip");
        });
    }

    private static async Task WithServerAsync(
        FakeOperations operations,
        Func<NamedPipeClientStream, StreamReader, StreamWriter, Task> clientAction,
        Action? commandCompleted = null)
    {
        var pipeName = $"IS74Wifi.Contract.AgentNetwork.{Guid.NewGuid():N}";
        using var gate = new SemaphoreSlim(1, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = AgentNetworkCommandServer.RunSingleAsync(
            pipeName,
            operations,
            gate,
            timeout.Token,
            commandCompleted);

        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true
        };

        await clientAction(pipe, reader, writer);
        await server.WaitAsync(timeout.Token);
    }

    private sealed class FakeOperations : IAgentNetworkOperations
    {
        public int RefreshCalls { get; private set; }
        public int AuthorizationCalls { get; private set; }
        public int AdapterCalls { get; private set; }
        public bool? LastAllowAutomaticAuthorization { get; private set; }
        public Exception? RefreshError { get; init; }
        public AuthorizationOutcome AuthorizationResult { get; init; } = new(
            AuthorizationOutcomeKind.AlreadyOnline,
            InternetConfirmed: true,
            AuthorizedAtUtc: null,
            RetryAfter: null,
            Timing: null);

        public Task<AgentNetworkSnapshot> RefreshAsync(
            bool allowAutomaticAuthorization,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            LastAllowAutomaticAuthorization = allowAutomaticAuthorization;
            if (RefreshError is not null)
            {
                throw RefreshError;
            }
            return Task.FromResult(new AgentNetworkSnapshot(
                CapturedAtUtc: DateTimeOffset.UtcNow,
                InternetAvailable: true,
                VpnActive: false,
                WifiSsid: "Campus Wi-Fi test",
                Paths:
                [
                    new AgentNetworkPathSnapshot(
                        "Wi-Fi · Campus Wi-Fi test", "Campus Wi-Fi test", 7, "10.0.0.2", "10.0.0.1",
                        PathAuthorizationStatus.Internet, true, null, "success", DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow.AddMinutes(1), false, DateTimeOffset.UtcNow, false, false),
                    new AgentNetworkPathSnapshot(
                        "Ethernet", null, 9, "10.1.0.2", "10.1.0.1",
                        PathAuthorizationStatus.Captive, false, null, null, null,
                        null, true, null, false, true)
                ]));
        }

        public Task<AgentNetworkAdapterOption[]> GetAdapterOptionsAsync(
            CancellationToken cancellationToken = default)
        {
            AdapterCalls++;
            return Task.FromResult(new[]
            {
                new AgentNetworkAdapterOption("wifi-1", "Wi-Fi", "Campus Wi-Fi test", true, false),
                new AgentNetworkAdapterOption("vpn-1", "VPN", null, true, true)
            });
        }

        public async Task<AuthorizationOutcome> AuthorizeNowAsync(
            Action<AuthorizationProgressStage>? progress = null,
            CancellationToken cancellationToken = default)
        {
            AuthorizationCalls++;
            progress?.Invoke(AuthorizationProgressStage.BaselineLoaded);
            await Task.Delay(20, cancellationToken);
            progress?.Invoke(AuthorizationProgressStage.InternetConfirmed);
            return AuthorizationResult;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
