namespace MusicSync.Core;

public sealed record PlayerView(PlayerReading Reading, ReportKind Kind, string? Detail = null, long? ApplyMs = null);

public sealed class RoomPlayer : IAsyncDisposable
{
    private readonly IPlayer player;
    private readonly Func<IRoomConnection?> connection;
    private readonly long threshold;
    private readonly TimeProvider time;
    private readonly Action<string>? log;
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private volatile PlayerView view = new(new(false), ReportKind.Waiting);
    private int retry;

    public RoomPlayer(IPlayer player, Func<IRoomConnection?> connection, long threshold, Action<string>? log = null,
        TimeProvider? timeProvider = null)
    {
        this.player = player;
        this.connection = connection;
        this.threshold = threshold;
        this.log = log;
        time = timeProvider ?? TimeProvider.System;
        worker = Task.Run(RunAsync);
    }

    public PlayerView View => view;
    public void Retry() => Interlocked.Increment(ref retry);

    private async Task RunAsync()
    {
        var token = stop.Token;
        IRoomConnection? previous = null;
        (long Generation, long Revision, Role? Role, int Retry)? handled = null;
        TrackId? controlled = null;
        long? controlledDuration = null, handoffRevision = null;
        bool applied = false;
        long? publishedRevision = null;
        long lastCorrection = 0, lastReport = 0, nextProbe = 0, changedSince = 0, lastPublish = 0;
        while (!token.IsCancellationRequested)
        {
            var room = connection();
            var network = room?.View;
            var state = network?.Room?.State;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cancellationGate = new object();
            bool observingChanges = true;
            void CancelObsolete()
            {
                lock (cancellationGate)
                {
                    if (!observingChanges)
                        return;
                    var current = room?.View;
                    if (connection() != room || current?.Status != network?.Status ||
                        current?.Generation != network?.Generation || current?.Role != network?.Role ||
                        current?.Room?.State.Revision != state?.Revision)
                        operation.Cancel();
                }
            }
            if (room is not null)
                room.Changed += CancelObsolete;
            CancelObsolete();
            var operationToken = operation.Token;
            if (room != previous)
            {
                handled = null;
                controlled = null;
                controlledDuration = handoffRevision = null;
                publishedRevision = null;
                applied = false;
                lastCorrection = lastReport = nextProbe = changedSince = lastPublish = 0;
                previous = room;
            }
            var reading = View.Reading;
            try
            {
                var observed = await player.ObserveAsync(operationToken);
                view = view with { Reading = observed };
                bool playerReturned = observed.Running && !reading.Running;
                reading = observed;
                if (room is not null && network is { Status: ConnectionStatus.Connected or ConnectionStatus.Reconnecting }
                    && state is not null)
                {
                    var key = (network.Generation, state.Revision, network.Role, Retry: Volatile.Read(ref retry));
                    long Now() => room.Clock.Now;
                    long now = Now();
                    var applier = new PlaybackApplier(player, Now, threshold);
                    bool leader = network.Role == Role.Leader;
                    bool Current() => connection() == room && room.View is { } current && current.Status == network.Status &&
                        current.Generation == key.Generation && current.Role == key.Role &&
                        current.Room?.State.Revision == key.Revision && !operationToken.IsCancellationRequested;

                    if (!Current())
                        continue;

                    // A reconnect with the same revision must not overwrite actions made in the leader's player.
                    if (leader && network.Status == ConnectionStatus.Connected && applied &&
                        handled is { } previousKey && previousKey.Generation != key.Generation &&
                        previousKey.Revision == key.Revision && previousKey.Role == key.Role && previousKey.Retry == key.Retry)
                    {
                        handled = key;
                        lastReport = 0;
                    }

                    if (!leader && (ShouldHoldAtEnd() || state.Unknown && handoffRevision.HasValue))
                    {
                        handoffRevision = state.Revision;
                        if (reading.Playback == Playback.Playing)
                        {
                            reading = await player.PauseForHandoffAsync(operationToken);
                            view = view with { Reading = reading };
                        }
                        await ReportHandoffAsync();
                    }
                    else if (network.Status != ConnectionStatus.Connected)
                    {
                        view = new(reading, ReportKind.Waiting);
                    }
                    else if (handled != key || state.Track is not null && playerReturned && View.Detail == "no-player")
                    {
                        bool newRevision = handled is null || handled.Value.Revision != state.Revision;
                        handled = key;
                        applied = false;
                        handoffRevision = null;
                        if (newRevision)
                            changedSince = 0;
                        if (state.Track is not null)
                        {
                            if (leader && publishedRevision == state.Revision)
                            {
                                // This state came from our player. It may have changed again since publication.
                                applied = true;
                                if (reading.Ready && reading.Track == state.Track && reading.Playback == state.Playback)
                                    await ReportAsync(ReportKind.Applied);
                            }
                            else
                            {
                                await ApplyAsync(ReportKind.Applied);
                            }
                        }
                        else if (state.Unknown)
                        {
                            if (!leader && controlled is not null && reading.Track == controlled &&
                                reading.Playback == Playback.Playing)
                                reading = await player.SetPlaybackAsync(Playback.Paused, operationToken);
                            controlled = null;
                            if (Current())
                                await ReportAsync(ReportKind.Unknown, "unknown-track");
                        }
                        else
                        {
                            await ReportAsync(ReportKind.Waiting);
                        }
                    }
                    else if (leader && reading.Ready &&
                        (state.Track is null || applied && reading.Track != state.Track))
                    {
                        if (changedSince == 0)
                            changedSince = now;
                        if (reading.Track is not null || now >= nextProbe && now - changedSince >= 650)
                        {
                            if (reading.Track is null)
                            {
                                nextProbe = now + 3000;
                                // The queue and live media may have caught up since the initial observation.
                                reading = await player.ObserveAsync(operationToken);
                                view = view with { Reading = reading };
                            }
                            if (reading.Ready && Current())
                            {
                                if (reading.Track is not null && reading.Track != state.Track)
                                {
                                    await PublishLocalAsync("Published the current track from the leader's player.");
                                }
                                else if (reading.Track is null && !state.Unknown)
                                {
                                    await room.ActionAsync(ActionKind.Unknown, expectedRevision: state.Revision);
                                }
                            }
                        }
                    }
                    else
                    {
                        changedSince = 0;
                        if (state.Track is not null && applied && reading.Ready)
                        {
                            if (leader && reading.Track == state.Track && now - lastPublish >= 1000 &&
                                (reading.Playback != state.Playback || now - lastCorrection >= 2000 &&
                                 Math.Abs(state.PositionAt(now, reading.DurationMs) - applier.EstimatedPosition(reading)) > threshold))
                            {
                                await PublishLocalAsync("Published the leader's playback change.");
                            }
                            else if (!leader && (applier.NeedsCorrection(state, reading, now, lastCorrection) ||
                                reading.Track != state.Track && now - lastCorrection >= 8000))
                            {
                                await ApplyAsync(ReportKind.Corrected);
                            }
                            else if (reading.Track == state.Track && reading.Playback == state.Playback &&
                                now - lastReport >= 2000)
                            {
                                await ReportAsync(View.Kind == ReportKind.Corrected ? ReportKind.Corrected : ReportKind.Applied,
                                    applyMs: View.ApplyMs);
                            }
                        }
                        else if (state.Track is not null && applied && !reading.Ready && View.Kind != ReportKind.Failed)
                        {
                            await ReportAsync(ReportKind.Failed, reading.Running ? "no-session" : "no-player");
                        }
                        else if (state.Track is not null && !applied && reading.Track == state.Track &&
                            View.Detail == "position-unconfirmed" && now - lastCorrection >= 8000)
                        {
                            await ApplyAsync(ReportKind.Corrected);
                        }
                    }

                    bool ShouldHoldAtEnd()
                    {
                        if (state.Track is null || state.Playback != Playback.Playing)
                            return false;
                        if (handoffRevision == state.Revision)
                            return true;

                        var localDuration = reading.Track == state.Track ? reading.DurationMs : null;
                        var knownDuration = localDuration ?? (controlled == state.Track ? controlledDuration : null)
                            ?? network.LeaderReport?.DurationMs;
                        if (knownDuration is not (> 0 and var duration))
                            return false;
                        return PlaybackApplier.IsNearEnd(state.PositionAt(now, duration), duration) ||
                            (handled?.Revision == state.Revision && applied && reading.Playback == Playback.Playing &&
                                localDuration.HasValue && PlaybackApplier.IsNearEnd(applier.EstimatedPosition(reading), duration));
                    }

                    async Task ReportHandoffAsync()
                    {
                        const string detail = "waiting-next-track";
                        if (View.Kind != ReportKind.Waiting || View.Detail != detail || now - lastReport >= 2000)
                            await ReportAsync(ReportKind.Waiting, detail);
                    }

                    async Task PublishLocalAsync(string message)
                    {
                        lastPublish = Now();
                        string? error = await room.ActionAsync(ActionKind.Adopt, reading.Track,
                            applier.EstimatedPosition(reading), reading.Playback, state.Revision);
                        if (error is null)
                        {
                            publishedRevision = state.Revision + 1;
                            log?.Invoke(message);
                        }
                    }

                    async Task ApplyAsync(ReportKind kind)
                    {
                        lastCorrection = Now();
                        await ReportAsync(ReportKind.Applying);
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            var result = await applier.ApplyAsync(state, reading, Current, operationToken, holdAtEnd: !leader);
                            reading = result.Reading;
                            if (result.WaitingForNextTrack)
                            {
                                handoffRevision = state.Revision;
                                await ReportHandoffAsync();
                                return;
                            }
                            controlled = reading.Track;
                            controlledDuration = reading.DurationMs;
                            applied = true;
                            await ReportAsync(kind, applyMs: result.ElapsedMs);
                            log?.Invoke($"Revision {state.Revision} {kind.ToString().ToLowerInvariant()} in {result.ElapsedMs} ms.");
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Keep observing the leader for the next track after playback ends.
                            applied = leader && ex is PlayerException { Code: "track-ended" };
                            if (Current())
                                await ReportAsync(ReportKind.Failed, PlayerError(ex), timer.ElapsedMilliseconds);
                            LogError(ex);
                        }
                    }

                    async Task ReportAsync(ReportKind kind, string? detail = null, long? applyMs = null)
                    {
                        if (!Current())
                            return;
                        lastReport = Now();
                        view = new(reading, kind, detail, applyMs);
                        var report = kind is ReportKind.Applied or ReportKind.Corrected
                            ? new PlayerReport(state.Revision, kind, reading.PositionMs, reading.DurationMs,
                                reading.SampledAt ?? lastReport, room.Clock.RttMs, reading.Playback, applyMs, detail)
                            : new PlayerReport(state.Revision, kind, ApplyMs: applyMs, Detail: detail);
                        if (network.Status == ConnectionStatus.Connected)
                            await room.ReportAsync(report, key.Generation);
                    }
                }
                else
                {
                    view = new(reading, ReportKind.Waiting);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (operation.IsCancellationRequested)
                    continue;
                var detail = PlayerError(ex);
                if (view.Detail != detail)
                    LogError(ex);
                view = new(reading, ReportKind.Failed, detail);
                if (room is not null && network is { Status: ConnectionStatus.Connected } && state is not null &&
                    room.View.Room?.State.Revision == state.Revision && room.View.Generation == network.Generation &&
                    room.View.Role == network.Role)
                    await room.ReportAsync(new PlayerReport(state.Revision, ReportKind.Failed, Detail: detail),
                        network.Generation);
            }

            finally
            {
                lock (cancellationGate)
                {
                    observingChanges = false;
                    if (room is not null)
                        room.Changed -= CancelObsolete;
                }
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(room is null ? 900 : 450), time, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static string PlayerError(Exception exception) => exception is PlayerException playerError
        ? playerError.Code : "player-error";

    private void LogError(Exception ex) =>
        log?.Invoke(ex is PlayerException ? $"Player: {ex.Message}" : $"Player error ({ex.GetType().Name}, 0x{ex.HResult:X8}).");

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await worker;
        stop.Dispose();
    }
}
