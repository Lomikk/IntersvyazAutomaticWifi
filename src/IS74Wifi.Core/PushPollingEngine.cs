using System.Diagnostics;

namespace IS74Wifi.Core;

public enum PushPollKind
{
    Primary,
    Fallback
}

public sealed record PushPollObservation(
    PushPollKind Kind,
    int TargetMilliseconds,
    int StartMilliseconds,
    int ObservedMilliseconds,
    long? MessageId,
    Is74ApiFailure? Failure);

public sealed record PushPollingResult(
    WifiCodeCandidate? Candidate,
    Is74ApiFailure? TerminalFailure,
    IReadOnlyList<PushPollObservation> Observations)
{
    public bool CodeFound => Candidate is not null;
}

public sealed class PushPollingEngine
{
    private readonly IIs74PushClient api;
    private readonly int[] offsetsMilliseconds;
    private readonly TimeSpan requestTimeout;

    public PushPollingEngine(
        IIs74PushClient api,
        IEnumerable<int>? offsetsMilliseconds = null,
        TimeSpan? requestTimeout = null)
    {
        this.api = api;
        this.offsetsMilliseconds = (offsetsMilliseconds ?? ProtocolContract.PushPollOffsetsMilliseconds.ToArray()).ToArray();
        if (this.offsetsMilliseconds.Length == 0 ||
            this.offsetsMilliseconds.Any(value => value < 0) ||
            !this.offsetsMilliseconds.SequenceEqual(this.offsetsMilliseconds.OrderBy(value => value)))
        {
            throw new ArgumentException("Poll offsets must be a non-empty ascending sequence of non-negative milliseconds.", nameof(offsetsMilliseconds));
        }

        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<PushPollingResult> WaitForFreshCodeAsync(
        string bearerToken,
        string deviceId,
        long baselineId,
        Stopwatch clock,
        CancellationToken cancellationToken = default)
    {
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var observations = new List<PushPollObservation>();
        var pending = offsetsMilliseconds
            .Select(target => RunPollAsync(
                PushPollKind.Primary,
                target,
                pageSize: 1,
                bearerToken,
                deviceId,
                baselineId,
                clock,
                pollCts.Token))
            .ToList();

        var fallbackStarted = false;

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);
            var poll = await completed.ConfigureAwait(false);

            if (poll.Cancelled)
            {
                continue;
            }

            observations.Add(poll.Observation);

            if (poll.TerminalFailure is not null)
            {
                pollCts.Cancel();
                return new PushPollingResult(null, poll.TerminalFailure, observations);
            }

            if (poll.Candidate is not null)
            {
                pollCts.Cancel();
                return new PushPollingResult(poll.Candidate, null, observations);
            }

            if (poll.FreshNonCode && !fallbackStarted)
            {
                fallbackStarted = true;
                var now = (int)Math.Max(0, Math.Round(clock.Elapsed.TotalMilliseconds));
                pending.Add(RunPollAsync(
                    PushPollKind.Fallback,
                    now,
                    pageSize: 5,
                    bearerToken,
                    deviceId,
                    baselineId,
                    clock,
                    pollCts.Token));
            }
        }

        return new PushPollingResult(null, null, observations);
    }

    private async Task<PollCompletion> RunPollAsync(
        PushPollKind kind,
        int targetMilliseconds,
        int pageSize,
        string bearerToken,
        string deviceId,
        long baselineId,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        try
        {
            await DelayUntilAsync(clock, targetMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PollCompletion.CancelledPoll();
        }

        var startMilliseconds = (int)Math.Max(0, Math.Round(clock.Elapsed.TotalMilliseconds));
        var result = await api.GetPushMessagesAsync(
            bearerToken,
            deviceId,
            pageSize,
            requestTimeout,
            cancellationToken).ConfigureAwait(false);
        var observedMilliseconds = (int)Math.Max(0, Math.Round(clock.Elapsed.TotalMilliseconds));

        if (!result.IsSuccess)
        {
            var failure = result.Failure!;
            var terminal = failure.Kind == Is74ApiFailureKind.Unauthorized ? failure : null;
            return new PollCompletion(
                Candidate: null,
                FreshNonCode: false,
                TerminalFailure: terminal,
                Observation: new PushPollObservation(
                    kind,
                    targetMilliseconds,
                    startMilliseconds,
                    observedMilliseconds,
                    MessageId: null,
                    Failure: failure),
                Cancelled: false);
        }

        var page = result.Value!;
        if (kind == PushPollKind.Fallback)
        {
            var candidate = PushMessageParser.FindWifiCodeAfterBaseline(page, baselineId);
            return new PollCompletion(
                candidate,
                FreshNonCode: false,
                TerminalFailure: null,
                Observation: new PushPollObservation(
                    kind,
                    targetMilliseconds,
                    startMilliseconds,
                    observedMilliseconds,
                    candidate?.MessageId,
                    Failure: null),
                Cancelled: false);
        }

        var top = page.Messages.FirstOrDefault();
        if (top is null || top.Id <= baselineId)
        {
            return new PollCompletion(
                Candidate: null,
                FreshNonCode: false,
                TerminalFailure: null,
                Observation: new PushPollObservation(
                    kind,
                    targetMilliseconds,
                    startMilliseconds,
                    observedMilliseconds,
                    top?.Id,
                    Failure: null),
                Cancelled: false);
        }

        var code = PushMessageParser.GetWifiCode(top);
        if (code is not null)
        {
            return new PollCompletion(
                new WifiCodeCandidate(code, top.Id),
                FreshNonCode: false,
                TerminalFailure: null,
                Observation: new PushPollObservation(
                    kind,
                    targetMilliseconds,
                    startMilliseconds,
                    observedMilliseconds,
                    top.Id,
                    Failure: null),
                Cancelled: false);
        }

        return new PollCompletion(
            Candidate: null,
            FreshNonCode: true,
            TerminalFailure: null,
            Observation: new PushPollObservation(
                kind,
                targetMilliseconds,
                startMilliseconds,
                observedMilliseconds,
                top.Id,
                Failure: null),
            Cancelled: false);
    }

    private static async Task DelayUntilAsync(Stopwatch clock, int targetMilliseconds, CancellationToken cancellationToken)
    {
        var remaining = targetMilliseconds - clock.Elapsed.TotalMilliseconds;
        if (remaining > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(remaining), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record PollCompletion(
        WifiCodeCandidate? Candidate,
        bool FreshNonCode,
        Is74ApiFailure? TerminalFailure,
        PushPollObservation Observation,
        bool Cancelled)
    {
        public static PollCompletion CancelledPoll() => new(
            Candidate: null,
            FreshNonCode: false,
            TerminalFailure: null,
            Observation: new PushPollObservation(PushPollKind.Primary, 0, 0, 0, null, null),
            Cancelled: true);
    }
}
