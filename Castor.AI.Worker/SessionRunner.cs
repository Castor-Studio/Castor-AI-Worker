using System.Diagnostics;
using System.Threading.Channels;
using Castor.AI.Contracts.V1;
using Castor.AI.Worker.Strategies;
using Grpc.Core;

namespace Castor.AI.Worker;

/// <summary>
/// Runs one session over the bidirectional stream with three decoupled loops:
/// <list type="bullet">
/// <item>read: consumes app messages, updates the strategy, wakes inference;</item>
/// <item>inference: evaluates the strategy on wake-up or on a periodic tick;</item>
/// <item>write: sole writer of the response stream (gRPC writes are not concurrent-safe).</item>
/// </list>
/// Inference never runs inside the read loop, so a slow model never stalls reception.
/// </summary>
internal sealed class SessionRunner(
    SessionStart start,
    IDomainStrategy strategy,
    SessionMedia media,
    WorkerCapabilities capabilities,
    ILogger logger)
{
    // Time-based rules (silence, min talk duration) need evaluation even without new input.
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    // Fast enough to drive live level meters in the app.
    private static readonly TimeSpan StatsInterval = TimeSpan.FromMilliseconds(250);

    private readonly Channel<WorkerMessage> _outbox =
        Channel.CreateUnbounded<WorkerMessage>(new UnboundedChannelOptions { SingleReader = true });

    // Capacity 1 + DropOldest: many inputs between two evaluations collapse into one wake-up (latest-wins).
    private readonly Channel<bool> _wake =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly Lock _gate = new();
    private readonly SessionClock _clock = new();
    private double _inferenceMsTotal;
    private long _evaluations;

    public async Task RunAsync(
        IAsyncStreamReader<ClientMessage> requests,
        IServerStreamWriter<WorkerMessage> responses,
        CancellationToken callToken)
    {
        var startLogs = new List<string>();
        try
        {
            lock (_gate)
            {
                strategy.Start(start, media, startLogs.Add);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Session {SessionId} refused: {Message}", start.SessionId, ex.Message);
            await responses.WriteAsync(new WorkerMessage
            {
                Error = new SessionError { Code = SessionError.Types.Code.BadFormat, Message = ex.Message, Fatal = true },
            });
            return;
        }

        Post(new WorkerMessage { Accepted = new SessionAccepted { SessionId = start.SessionId, Capabilities = capabilities } });
        Log(WorkerLog.Types.Level.Info,
            $"Session {start.SessionId} started: domain '{start.Domain}', {start.Cameras.Count} cameras");
        foreach (var line in startLogs)
        {
            Log(WorkerLog.Types.Level.Info, line);
        }

        using var loops = CancellationTokenSource.CreateLinkedTokenSource(callToken);
        var writer = WriteLoopAsync(responses, callToken);
        var inference = InferenceLoopAsync(loops.Token);
        var stats = StatsLoopAsync(loops.Token);

        var endReason = "client disconnected";
        try
        {
            endReason = await ReadLoopAsync(requests, callToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException || callToken.IsCancellationRequested)
        {
            // Client went away: nothing to report back.
        }
        finally
        {
            loops.Cancel();
            await Task.WhenAll(inference, stats);

            Post(new WorkerMessage { Ended = new SessionEnded { Reason = endReason } });
            _outbox.Writer.TryComplete();
            try
            {
                await writer;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            {
                // Stream already closed by the client.
            }

            logger.LogInformation("Session {SessionId} ended: {Reason}", start.SessionId, endReason);
        }
    }

    private async Task<string> ReadLoopAsync(IAsyncStreamReader<ClientMessage> requests, CancellationToken ct)
    {
        await foreach (var message in requests.ReadAllAsync(ct))
        {
            switch (message.BodyCase)
            {
                case ClientMessage.BodyOneofCase.Payload:
                    _clock.Observe(message.Payload.PtsUs);
                    lock (_gate)
                    {
                        strategy.OnPayload(message.Payload);
                    }
                    Wake();
                    break;

                case ClientMessage.BodyOneofCase.OnAir:
                    _clock.Observe(message.OnAir.PtsUs);
                    lock (_gate)
                    {
                        strategy.OnAir(message.OnAir);
                    }
                    Wake();
                    break;

                case ClientMessage.BodyOneofCase.VideoFrame:
                    // New frame in a camera region: the strategy reads the latest one (latest-wins).
                    _clock.Observe(message.VideoFrame.PtsUs);
                    Wake();
                    break;

                case ClientMessage.BodyOneofCase.Audio:
                    // New samples in the audio ring: run the VAD now rather than at the next tick.
                    _clock.Observe(message.Audio.PtsUs);
                    Wake();
                    break;

                case ClientMessage.BodyOneofCase.Stop:
                    return string.IsNullOrEmpty(message.Stop.Reason) ? "stopped by app" : message.Stop.Reason;

                case ClientMessage.BodyOneofCase.Start:
                    Log(WorkerLog.Types.Level.Warning, "Duplicate SessionStart ignored");
                    break;
            }
        }

        return "stream completed by app";
    }

    private async Task InferenceLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                using (var tick = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    tick.CancelAfter(TickInterval);
                    try
                    {
                        await _wake.Reader.ReadAsync(tick.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Periodic tick.
                    }
                }

                ct.ThrowIfCancellationRequested();

                var started = Stopwatch.GetTimestamp();
                SceneDecision? decision;
                lock (_gate)
                {
                    decision = strategy.Evaluate(_clock.NowUs);
                    _inferenceMsTotal += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    _evaluations++;
                }

                if (decision is not null)
                {
                    decision.ProcessingMs = (float)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Post(new WorkerMessage { Decision = decision });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task StatsLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(StatsInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var stats = new WorkerStats { PtsUs = _clock.NowUs };
                lock (_gate)
                {
                    stats.InferenceMsAvg = (float)(_evaluations == 0 ? 0 : _inferenceMsTotal / _evaluations);
                    _inferenceMsTotal = 0;
                    _evaluations = 0;
                    strategy.FillStats(stats);
                }

                Post(new WorkerMessage { Stats = stats });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WriteLoopAsync(IServerStreamWriter<WorkerMessage> responses, CancellationToken ct)
    {
        await foreach (var message in _outbox.Reader.ReadAllAsync(ct))
        {
            await responses.WriteAsync(message);
        }
    }

    private void Wake() => _wake.Writer.TryWrite(true);

    private void Post(WorkerMessage message) => _outbox.Writer.TryWrite(message);

    private void Log(WorkerLog.Types.Level level, string message)
    {
        logger.LogInformation("{Message}", message);
        Post(new WorkerMessage { Log = new WorkerLog { Level = level, Message = message } });
    }
}
