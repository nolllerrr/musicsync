using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MusicSync.Core;

namespace MusicSync.Tests;

[TestClass]
public sealed class GroupPlayerTests
{
    private static readonly TrackId First = new("10030", "38634573");
    private static readonly TrackId Second = new("42185896", "151629851");

    [TestMethod]
    public async Task LeaderAndNineListenersApplyTrackSeekPauseAndPlayIndependently()
    {
        await using var group = new PlayerGroup(First);
        await group.ReadyAsync();
        group.AssertApplied(1, First, Playback.Paused);
        group.Broadcast(group.State with { Revision = 2, PositionMs = 51_000 });
        await group.PollAsync();
        group.AssertApplied(2, First, Playback.Paused);
        Assert.IsTrue(group.Clients.All(c => c.Worker.View.Reading.PositionMs == 51_000));
        group.Broadcast(group.State with { Revision = 3, Playback = Playback.Playing, ServerTimestamp = group.Now });
        await group.PollAsync();
        group.AssertApplied(3, First, Playback.Playing);
        group.Broadcast(group.State with { Revision = 4, Playback = Playback.Paused, PositionMs = 53_000 });
        await group.PollAsync();
        group.AssertApplied(4, First, Playback.Paused);
        group.Broadcast(group.State with { Revision = 5, Selection = 2, Track = Second, PositionMs = 0 });
        await group.PollAsync();
        group.AssertApplied(5, Second, Playback.Paused);
        Assert.IsTrue(group.Clients.All(c => c.Player.Loads == 2));
        Assert.IsTrue(group.Clients.All(c => c.Connection.Actions.IsEmpty));
    }

    [TestMethod]
    public async Task SlowListenerUsesFreshServerPositionWithoutBlockingOthers()
    {
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var group = new PlayerGroup(First, Playback.Playing,
            special: now => new FakePlayer(now) { HoldFirst = load.Task });
        await group.ReadyAsync(9);
        await group.Clients[^1].Player.FirstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, group.Clients[^1].Player.Loads);
        group.AssertApplied(1, First, Playback.Playing, count: 9);
        // Five seconds of loading, with no wall-clock wait.
        for (int i = 0; i < 12; i++)
            await group.PollAsync(9);
        load.SetResult();
        await group.ReadyAsync();
        group.AssertApplied(1, First, Playback.Playing);
        var slow = group.Clients[^1];
        Assert.AreEqual(group.State.PositionAt(group.Now), slow.Worker.View.Reading.PositionMs);
        Assert.AreEqual(1, slow.Player.Seeks);
        Assert.AreEqual(1, slow.Player.Loads);
    }

    [TestMethod]
    public async Task FailedListenerCannotChangeOtherPlayersAndRecoversOnNextTrack()
    {
        await using var group = new PlayerGroup(First,
            special: now => new FakePlayer(now) { Unavailable = First });
        await group.ReadyAsync();
        group.AssertApplied(1, First, Playback.Paused, count: 9);
        Assert.AreEqual("track-unavailable", group.Clients[^1].Worker.View.Detail);
        for (int i = 0; i < 25; i++)
            await group.PollAsync();
        Assert.IsTrue(group.Clients.All(c => c.Player.Loads == 1));
        Assert.IsTrue(group.Clients.All(c => c.Connection.Actions.IsEmpty));
        group.Broadcast(group.State with { Revision = 2, Track = Second, Selection = 2 });
        await group.PollAsync();
        group.AssertApplied(2, Second, Playback.Paused);
    }

    [TestMethod]
    public async Task MembershipAndReportsDoNotReapplyARevisionOrControlPlayback()
    {
        await using var group = new PlayerGroup(First);
        await group.ReadyAsync();
        var commands = group.CommandCounts();
        var member = group.Members[^1];
        group.Members[^1] = member with { Connected = false };
        group.Broadcast(group.State);
        await group.PollAsync();
        group.Members[^1] = member with { Report = new(1, ReportKind.Failed, Detail: "track-unavailable") };
        // Exercise reordering and a burst of presence-only snapshots.
        group.Members.Reverse();
        for (int i = 0; i < 100; i++)
            group.Broadcast(group.State);
        await group.PollAsync();
        CollectionAssert.AreEqual(commands, group.CommandCounts());
        Assert.IsTrue(group.Clients.All(c => c.Connection.View.Participants.Count == 10));
        Assert.IsTrue(group.Clients.All(c => c.Connection.View.Participants.Any(
            p => p.ParticipantId == c.Connection.View.SelfParticipantId)));
    }

    [TestMethod]
    public async Task PresenceChangesDoNotCancelAnInFlightLoad()
    {
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var group = new PlayerGroup(First,
            special: now => new FakePlayer(now) { HoldFirst = load.Task });
        await group.ReadyAsync(9);
        await group.Clients[^1].Player.FirstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        group.Members[2] = group.Members[2] with { Connected = false };
        group.Broadcast(group.State);
        await group.PollAsync(9);
        Assert.AreEqual(ReportKind.Applying, group.Clients[^1].Worker.View.Kind);
        load.SetResult();
        await group.ReadyAsync();
        group.AssertApplied(1, First, Playback.Paused);
        Assert.AreEqual(1, group.Clients[^1].Player.Loads);
    }

    [TestMethod]
    public async Task NewRevisionsCancelBlockedLoadAndSkipStalePlaybackCommands()
    {
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var group = new PlayerGroup(First,
            special: now => new FakePlayer(now) { HoldFirst = load.Task });
        await group.ReadyAsync(9);
        await group.Clients[^1].Player.FirstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var slow = group.Clients[^1];
        group.Broadcast(group.State with { Revision = 2, Track = Second, Selection = 2 });
        group.Broadcast(group.State with { Revision = 3, Playback = Playback.Playing });
        await group.ReadyAsync(); // Cancellation releases the load without completing its gate.
        await group.PollAsync();
        group.AssertApplied(3, Second, Playback.Playing);
        Assert.IsFalse(load.Task.IsCompleted);
        Assert.AreEqual(2, slow.Player.Loads);
        Assert.AreEqual(0, slow.Player.PlaybackCommands); // No pause for revision 1 or 2.
        Assert.IsFalse(slow.Connection.Reports.Any(r => r.Revision < 3 && r.Kind == ReportKind.Applied));
    }

    [TestMethod]
    public async Task ListenerReconnectsToLatestStateAfterLongOutage()
    {
        await using var group = new PlayerGroup(First);
        await group.ReadyAsync();
        var listener = group.Clients[^1];
        var id = listener.Connection.View.SelfParticipantId;
        listener.Connection.Set(listener.Connection.View with { Status = ConnectionStatus.Reconnecting });
        for (int i = 0; i < 200; i++)
            await group.PollAsync();
        Assert.AreEqual(1, listener.Player.Loads);
        group.Broadcast(group.State with { Revision = 2, Track = Second, Selection = 2, PositionMs = 45_000 });
        await group.PollAsync();
        Assert.AreEqual(First, listener.Worker.View.Reading.Track);
        listener.Connection.Set(listener.Connection.View with { Status = ConnectionStatus.Connected, Generation = 2 });
        await group.PollAsync();
        group.AssertApplied(2, Second, Playback.Paused);
        Assert.AreEqual(id, listener.Connection.View.SelfParticipantId);
        Assert.AreEqual(2, listener.Player.Loads);
    }

    [TestMethod]
    public async Task ListenerJoiningMidTrackUsesCurrentServerTime()
    {
        await using var group = new PlayerGroup(First, Playback.Playing, listeners: 0);
        await group.ReadyAsync();
        for (int i = 0; i < 20; i++)
            await group.PollAsync();
        group.AddListener();
        await group.ReadyAsync();
        var joined = group.Clients[^1];
        Assert.AreEqual(group.State.PositionAt(group.Now), joined.Worker.View.Reading.PositionMs);
        Assert.AreEqual(First, joined.Worker.View.Reading.Track);
        Assert.AreEqual(1, joined.Player.Loads);
        Assert.AreEqual(1, joined.Player.Seeks);
    }

    [TestMethod]
    public async Task RoleChangeAtSameRevisionReappliesListenerStateAndStopsPublishing()
    {
        await using var group = new PlayerGroup(First);
        await group.ReadyAsync();
        var leader = group.Clients[0];
        var promoted = group.Clients[1];
        // A formerly authoritative player differs from the canonical position.
        leader.Player.ManualPlayback(80_000, Playback.Playing);
        group.Members[0] = group.Members[0] with { Role = Role.Listener };
        group.Members[1] = group.Members[1] with { Role = Role.Leader };
        group.Broadcast(group.State);
        await group.PollAsync();
        Assert.AreEqual(Playback.Paused, leader.Worker.View.Reading.Playback);
        Assert.AreEqual(10_000L, leader.Worker.View.Reading.PositionMs);
        Assert.IsTrue(leader.Connection.Actions.IsEmpty);
        promoted.Player.Manual(Second, 25_000, Playback.Playing);
        await group.PollAsync();
        Assert.AreEqual(ActionKind.Adopt, promoted.Connection.Actions.Single().Kind);
        Assert.AreEqual(Second, promoted.Connection.Actions.Single().Track);
    }

    [TestMethod]
    public async Task RoleChangeCancelsAnInFlightOperationWithoutWaitingForLoad()
    {
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var group = new PlayerGroup(First, listeners: 1,
            special: now => new FakePlayer(now) { HoldFirst = load.Task });
        await group.ReadyAsync(1);
        await group.Clients[^1].Player.FirstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        group.Members[0] = group.Members[0] with { Role = Role.Listener };
        group.Members[1] = group.Members[1] with { Role = Role.Leader };
        group.Broadcast(group.State);
        await group.ReadyAsync();
        await group.PollAsync();
        Assert.AreEqual(2, group.Clients[1].Player.Loads);
        Assert.IsFalse(load.Task.IsCompleted);
        Assert.AreEqual(ReportKind.Applied, group.Clients[1].Worker.View.Kind);
    }

    [TestMethod]
    public async Task LeaderPresenceDoesNotRestartListenersAndUnknownTrackPausesSafely()
    {
        await using var group = new PlayerGroup(First, Playback.Playing);
        await group.ReadyAsync();
        var commands = group.CommandCounts();
        group.Members[0] = group.Members[0] with { Connected = false };
        group.Broadcast(group.State);
        await group.PollAsync();
        group.Members[0] = group.Members[0] with { Connected = true };
        group.Broadcast(group.State);
        await group.PollAsync();
        CollectionAssert.AreEqual(commands, group.CommandCounts());
        group.Broadcast(group.State with { Revision = 2, Unknown = true, Track = null });
        await group.PollAsync();
        Assert.IsTrue(group.Clients.Skip(1).All(c => c.Worker.View.Kind == ReportKind.Unknown &&
            c.Worker.View.Reading.Playback == Playback.Paused));
        group.Broadcast(group.State with { Revision = 3, Unknown = false, Track = Second, Selection = 2 });
        await group.PollAsync();
        group.AssertApplied(3, Second, Playback.Playing);
    }

    [TestMethod]
    public async Task ListenersHoldAtEndUntilNextCanonicalTrack()
    {
        await using var group = new PlayerGroup(First, Playback.Playing);
        await group.ReadyAsync();
        group.Broadcast(group.State with
        {
            Revision = 2,
            PositionMs = FakePlayer.DurationMs - 600,
            ServerTimestamp = group.Now
        });
        await group.PollAsync();
        Assert.IsTrue(group.Clients.Skip(1).All(c => c.Worker.View is
        { Kind: ReportKind.Waiting, Detail: "waiting-next-track", Reading.Playback: Playback.Paused }));
        group.Broadcast(group.State with
        {
            Revision = 3,
            Selection = 2,
            Track = Second,
            PositionMs = 0,
            ServerTimestamp = group.Now
        });
        await group.PollAsync();
        group.AssertApplied(3, Second, Playback.Playing);
    }
}

// These are controlled client inputs, not a replacement room server. Network
// admission, fan-out and identity/resume integration belong to PR 1.
internal sealed class PlayerGroup : IAsyncDisposable
{
    private readonly PollTime time = new();
    public sealed record Client(ControlledConnection Connection, FakePlayer Player, RoomPlayer Worker);
    public List<Client> Clients { get; } = [];
    public List<Participant> Members { get; } = [];
    public RoomState State { get; private set; }
    public long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    public PlayerGroup(TrackId track, Playback playback = Playback.Paused, int listeners = 9,
        Func<Func<long>, FakePlayer>? special = null)
    {
        State = new("ROOM", 1, 1, track, playback, 10_000, Now, false);
        Add(Role.Leader);
        for (int i = 0; i < listeners; i++)
            Add(Role.Listener, i == listeners - 1 ? special : null);
    }

    public void AddListener() => Add(Role.Listener);

    private void Add(Role role, Func<Func<long>, FakePlayer>? factory = null)
    {
        var id = Guid.NewGuid();
        Members.Add(new(id, role, true, null));
        var connection = new ControlledConnection(new SyncClock(new MonotonicClock(time)));
        connection.Set(new(ConnectionStatus.Connected, role, new(State, Members.ToArray(), 10), 1)
        {
            SelfParticipantId = id,
            Participants = Members.ToArray(),
            Capacity = 10
        });
        var player = factory?.Invoke(() => Now) ?? new FakePlayer(() => Now);
        var worker = new RoomPlayer(player, () => connection, 1200, timeProvider: time);
        Clients.Add(new(connection, player, worker));
        Broadcast(State);
    }

    public void Broadcast(RoomState state)
    {
        State = state;
        foreach (var client in Clients)
        {
            var view = client.Connection.View;
            var role = Members.Single(p => p.ParticipantId == view.SelfParticipantId).Role;
            client.Connection.Set(view with
            {
                Room = view.Room! with { State = state },
                Role = role,
                Participants = Members.ToArray()
            });
        }
    }

    public Task ReadyAsync(int? count = null) => time.WaitForTimersAsync(count ?? Clients.Count);

    public async Task PollAsync(int? count = null)
    {
        await ReadyAsync(count);
        time.Advance(TimeSpan.FromMilliseconds(450));
        await ReadyAsync(count);
    }

    public int[] CommandCounts() => Clients.SelectMany(c => new[]
        { c.Player.Loads, c.Player.Seeks, c.Player.PlaybackCommands }).ToArray();

    public void AssertApplied(long revision, TrackId track, Playback playback, int? count = null)
    {
        foreach (var client in Clients.Take(count ?? Clients.Count))
        {
            Assert.AreEqual(track, client.Worker.View.Reading.Track);
            Assert.AreEqual(playback, client.Worker.View.Reading.Playback);
            Assert.IsTrue(client.Connection.Reports.Any(r => r.Revision == revision &&
                r.Kind == ReportKind.Applied), $"Missing applied report for revision {revision}");
        }
    }

    public async ValueTask DisposeAsync() =>
        await Task.WhenAll(Clients.Select(c => c.Worker.DisposeAsync().AsTask()));
}

internal sealed class ControlledConnection(SyncClock clock) : IRoomConnection
{
    private volatile ConnectionView view = new(ConnectionStatus.Connecting);
    public ConnectionView View => view;
    public SyncClock Clock => clock;
    public event Action? Changed;
    public ConcurrentQueue<PlayerReport> Reports { get; } = new();
    public ConcurrentQueue<RoomAction> Actions { get; } = new();
    public void Set(ConnectionView next) { view = next; Changed?.Invoke(); }
    public Task ReportAsync(PlayerReport report, long generation)
    {
        if (View.Generation == generation)
            Reports.Enqueue(report);
        return Task.CompletedTask;
    }
    public Task<string?> ActionAsync(ActionKind kind, TrackId? track = null, long? position = null,
        Playback? playback = null, long? expectedRevision = null)
    {
        Actions.Enqueue(new(kind, expectedRevision ?? View.Room!.State.Revision, track, position, playback));
        return Task.FromResult<string?>(null);
    }
}

// Only RoomPlayer's one-shot polling delay is scheduled here. Tests explicitly
// advance time, and timer registration is a barrier proving a poll has finished.
internal sealed class PollTime : TimeProvider
{
    private readonly object gate = new();
    private readonly List<PollTimer> timers = [];
    private long milliseconds = 1_000_000;
    private TaskCompletionSource changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() { lock (gate) return milliseconds; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Assert.AreEqual(Timeout.InfiniteTimeSpan, period);
        var timer = new PollTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public async Task WaitForTimersAsync(int count)
    {
        while (true)
        {
            Task signal;
            lock (gate)
            {
                if (timers.Count >= count) return;
                signal = changed.Task;
            }
            await signal.WaitAsync(TimeSpan.FromSeconds(10)); // Deadlock guard only, not simulated time.
        }
    }

    public void Advance(TimeSpan elapsed)
    {
        PollTimer[] due;
        lock (gate)
        {
            milliseconds += (long)elapsed.TotalMilliseconds;
            due = timers.Where(t => t.Due <= milliseconds).ToArray();
            foreach (var timer in due) timers.Remove(timer);
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class PollTimer(PollTime owner, TimerCallback callback, object? state) : ITimer
    {
        public long Due { get; private set; }
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                owner.timers.Remove(this);
                Due = owner.milliseconds + (long)dueTime.TotalMilliseconds;
                if (dueTime != Timeout.InfiniteTimeSpan) owner.timers.Add(this);
                var signal = owner.changed;
                owner.changed = NewSignal();
                signal.TrySetResult();
                return true;
            }
        }
        public void Dispose() { lock (owner.gate) owner.timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
