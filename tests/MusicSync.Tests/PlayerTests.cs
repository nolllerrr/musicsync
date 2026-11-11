using Microsoft.VisualStudio.TestTools.UnitTesting;
using MusicSync.Core;

namespace MusicSync.Tests;

[TestClass]
public sealed class PlayerTests
{
    private static readonly TrackId First = new("10030", "38634573");
    private static readonly TrackId Second = new("42185896", "151629851");

    [TestMethod]
    [DataRow("http://music.yandex.ru/album/1/track/2")]
    [DataRow("https://music.yandex.ru.evil.example/album/1/track/2")]
    [DataRow("https://music.yandex.ru@evil.example/album/1/track/2")]
    [DataRow("https://music.yandex.ru:8080/album/1/track/2")]
    [DataRow("https://music.yandex.ru/track/2")]
    [DataRow("https://music.yandex.ru/album/1/track/0")]
    [DataRow("https://music.yandex.ru/album/1/track/2/extra")]
    [DataRow("https://music.yandex.ru/album/1/track/%32")]
    public void RejectsInvalidTrackLinks(string url) => Assert.IsNull(TrackId.Parse(url));

    [TestMethod]
    public void AcceptsCanonicalTrackAndRestrictsCleartextEndpoints()
    {
        Assert.AreEqual(First, TrackId.Parse("https://music.yandex.ru/album/10030/track/38634573?utm_source=share"));
        Assert.IsNotNull(Protocol.ParseEndpoint("ws://127.0.0.1:5050/ws", false));
        Assert.IsNotNull(Protocol.ParseEndpoint("ws://192.168.1.10:5050/ws", true));
        Assert.IsNull(Protocol.ParseEndpoint("ws://192.168.1.10:5050/ws", false));
        Assert.IsNull(Protocol.ParseEndpoint("ws://8.8.8.8:5050/ws", true));
        Assert.IsNull(Protocol.ParseEndpoint("ws://example.org:5050/ws", true));
        Assert.IsNotNull(Protocol.ParseEndpoint("wss://example.org/ws", false));
    }

    [TestMethod]
    public void EstimatesClockFromLowestRttAndShowsOnlyComparablePositions()
    {
        var clock = new SyncClock(new MonotonicClock());
        long t = clock.LocalNow;
        clock.Add(t, t + 1010, t + 1012, t + 22);
        clock.Add(t, t + 2000, t + 2002, t + 100);
        Assert.AreEqual(20L, clock.RttMs);
        Assert.IsLessThan(10L, Math.Abs(clock.Now - clock.LocalNow - 1000));
        var state = new RoomState("ROOM", 4, 1, First, Playback.Playing, 20_000, t, false);
        var leader = new PlayerReport(4, ReportKind.Applied, 20_000, 120_000, t, 25, Playback.Playing);
        var guest = new PlayerReport(4, ReportKind.Applied, 20_200, 120_000, t + 500, 30, Playback.Playing);
        var view = new RoomView(state, new(true, leader), new(true, guest));
        Assert.AreEqual(-300L, SyncClock.Difference(view, t + 2000));
        Assert.IsNull(SyncClock.Difference(view, t + 6000));
        Assert.IsNull(SyncClock.Difference(view with { Listener = new(true, guest with { Revision = 3 }) }, t + 2000));
        Assert.IsNull(SyncClock.Difference(view with { Listener = new(true, guest with { RttMs = null }) }, t + 2000));
    }

    [TestMethod]
    public async Task CorrectsLargeDriftAndHonorsThresholdAndCooldown()
    {
        const long time = 80_000;
        var mock = new FakePlayer(() => time);
        mock.Manual(First, 32_000, Playback.Playing);
        var state = new RoomState("ROOM", 2, 1, First, Playback.Playing, 30_000, time, false);
        var apply = new PlaybackApplier(mock, () => time, 1200);
        var reading = await mock.ObserveAsync(default);
        Assert.IsFalse(apply.NeedsCorrection(state, reading, time, time - 7999));
        Assert.IsFalse(apply.NeedsCorrection(state, reading with { PositionMs = 31_000 }, time, 0));
        Assert.IsTrue(apply.NeedsCorrection(state, reading, time, 0));
        var result = await apply.ApplyAsync(state, reading, () => true, default);
        Assert.AreEqual(30_000L, result.Reading.PositionMs);
        Assert.AreEqual(1, mock.Seeks);
        Assert.IsFalse(apply.NeedsCorrection(state, result.Reading, time, 0));
    }

    [TestMethod]
    public async Task TwoClientsApplySelectionPauseSeekResumeAndConfirm()
    {
        await using var server = await LocalServer.StartAsync();
        await using var leader = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => leader.View.Status == ConnectionStatus.Connected);
        await using var listener = new RoomConnection(server.Uri, leader.View.Room!.State.RoomId);
        var hostPlayer = new FakePlayer(() => leader.Clock.Now);
        var guestPlayer = new FakePlayer(() => listener.Clock.Now);
        await using var hostWorker = new RoomPlayer(hostPlayer, () => leader, 1200);
        await using var guestWorker = new RoomPlayer(guestPlayer, () => listener, 1200);
        await Check.EventuallyAsync(() => listener.View.Status == ConnectionStatus.Connected);
        Assert.IsNull(await leader.ActionAsync(ActionKind.Select, First));
        await BothAppliedAsync(1);
        Assert.AreEqual(First, guestWorker.View.Reading.Track);
        Assert.IsNotNull(leader.View.Room!.Listener.Report!.ApplyMs);
        Assert.IsNull(await leader.ActionAsync(ActionKind.Pause));
        await BothAppliedAsync(2);
        Assert.IsNull(await leader.ActionAsync(ActionKind.Seek, position: 30_000));
        await BothAppliedAsync(3);
        Assert.AreEqual(30_000L, guestWorker.View.Reading.PositionMs);
        Assert.AreEqual(Playback.Paused, guestWorker.View.Reading.Playback);
        Assert.IsNull(await leader.ActionAsync(ActionKind.Play));
        await BothAppliedAsync(4);
        Assert.AreEqual(Playback.Playing, guestWorker.View.Reading.Playback);

        async Task BothAppliedAsync(long revision) => await Check.EventuallyAsync(() =>
        {
            var room = leader.View.Room;
            return room?.Leader.Report is { Kind: ReportKind.Applied } lead && lead.Revision == revision &&
                room.Listener.Report is { Kind: ReportKind.Applied } guest && guest.Revision == revision;
        });
    }

    [TestMethod]
    public async Task RapidSelectionAndPauseSupersedeInFlightLoad()
    {
        await using var server = await LocalServer.StartAsync();
        await using var host = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => host.View.Status == ConnectionStatus.Connected);
        await using var guest = new RoomConnection(server.Uri, host.View.Room!.State.RoomId);
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mock = new FakePlayer(() => guest.Clock.Now) { HoldFirst = load.Task };
        await using var worker = new RoomPlayer(mock, () => guest, 1200);
        try
        {
            await Check.EventuallyAsync(() => guest.View.Status == ConnectionStatus.Connected);
            Assert.IsNull(await host.ActionAsync(ActionKind.Select, First));
            await Check.EventuallyAsync(() => mock.Loads == 1);
            Assert.IsNull(await host.ActionAsync(ActionKind.Select, Second));
            Assert.IsNull(await host.ActionAsync(ActionKind.Pause));
            await Check.EventuallyAsync(() => guest.View.Room?.State.Revision == 3);
            load.TrySetResult();
            await Check.EventuallyAsync(() => host.View.Room?.Listener.Report is { Kind: ReportKind.Applied, Revision: 3 });
            Assert.AreEqual(Second, worker.View.Reading.Track);
            Assert.AreEqual(Playback.Paused, worker.View.Reading.Playback);
            Assert.AreEqual(2, mock.Loads);
        }
        finally { load.TrySetResult(); }
    }

    [TestMethod]
    public async Task ReportsMissingPlayerAndUnavailableTrackThenRecovers()
    {
        await using var server = await LocalServer.StartAsync();
        await using var host = new RoomConnection(server.Uri, null);
        var mock = new FakePlayer(() => host.Clock.Now) { Running = false, Unavailable = First };
        await using var worker = new RoomPlayer(mock, () => host, 1200);
        await Check.EventuallyAsync(() => host.View.Status == ConnectionStatus.Connected);
        Assert.IsNull(await host.ActionAsync(ActionKind.Select, First));
        await Check.EventuallyAsync(() => host.View.Room?.Leader.Report is { Kind: ReportKind.Failed, Detail: "no-player" });
        Assert.AreEqual(0, mock.Loads);
        mock.Running = true;
        await Check.EventuallyAsync(() => host.View.Room?.Leader.Report is
            { Kind: ReportKind.Failed, Detail: "track-unavailable" });
        Assert.IsNull(await host.ActionAsync(ActionKind.Select, Second));
        await Check.EventuallyAsync(() => host.View.Room?.Leader.Report is { Kind: ReportKind.Applied, Revision: 2 });
        Assert.AreEqual(Second, worker.View.Reading.Track);
    }

    [TestMethod]
    public async Task LeaderUsesFreshObservationBeforePublishingUnknownTrack()
    {
        await using var server = await LocalServer.StartAsync();
        await using var host = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => host.View.Status == ConnectionStatus.Connected);

        int observations = 0;
        var mock = new FakePlayer(() => host.Clock.Now)
        {
            BeforeObserve = async (player, token) =>
            {
                // The first poll handles the empty room and the second starts the debounce.
                // Let the debounce expire during the next poll, then reveal the ID on the re-read.
                switch (++observations)
                {
                    case 3:
                        await Task.Delay(650, token);
                        break;
                    case 4:
                        player.Manual(First, 3000, Playback.Paused);
                        break;
                }
            }
        };
        mock.Manual(First, 0, Playback.Paused, exact: false);
        await using var worker = new RoomPlayer(mock, () => host, 1200);

        await Check.EventuallyAsync(() => host.View.Room?.State.Track == First);
        Assert.AreEqual(1L, host.View.Room!.State.Revision);
        Assert.AreEqual(3000L, host.View.Room.State.PositionMs);
    }

    [TestMethod]
    public async Task ExternalChangesRequireExactIdentityBeforeSendingATrack()
    {
        await using var server = await LocalServer.StartAsync();
        await using var host = new RoomConnection(server.Uri, null);
        var mock = new FakePlayer(() => host.Clock.Now);
        await using var worker = new RoomPlayer(mock, () => host, 1200);
        await Check.EventuallyAsync(() => host.View.Status == ConnectionStatus.Connected);
        await using var guest = new RoomConnection(server.Uri, host.View.Room!.State.RoomId);
        var follower = new FakePlayer(() => guest.Clock.Now);
        await using var guestWorker = new RoomPlayer(follower, () => guest, 1200);
        Assert.IsNull(await host.ActionAsync(ActionKind.Select, First));
        await Check.EventuallyAsync(() => host.View.Room?.Listener.Report is { Kind: ReportKind.Applied, Revision: 1 });

        mock.Manual(Second, 3000, Playback.Playing, exact: false);
        await Check.EventuallyAsync(() => host.View.Room?.State is { Revision: 2, Unknown: true, Track: null });
        await Check.EventuallyAsync(() => host.View.Room?.Listener.Report is { Kind: ReportKind.Unknown, Revision: 2 });
        Assert.AreEqual(1, follower.Loads);
        Assert.AreEqual(Playback.Paused, guestWorker.View.Reading.Playback);

        mock.Manual(Second, 8000, Playback.Playing, exact: true);
        await Check.EventuallyAsync(() => host.View.Room?.Listener.Report is { Kind: ReportKind.Applied, Revision: 3 });
        Assert.AreEqual(Second, guestWorker.View.Reading.Track);
        Assert.AreEqual(2, follower.Loads);
    }

    [TestMethod]
    public async Task ListenerFollowsNativePlaybackWithoutSendingCommandsToLeader()
    {
        await using var server = await LocalServer.StartAsync();
        await using var leader = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => leader.View.Status == ConnectionStatus.Connected);
        await using var listener = new RoomConnection(server.Uri, leader.View.Room!.State.RoomId);
        var leaderPlayer = new FakePlayer(() => leader.Clock.Now);
        var listenerPlayer = new FakePlayer(() => listener.Clock.Now);
        leaderPlayer.Manual(First, 15_000, Playback.Playing);
        await using var leaderWorker = new RoomPlayer(leaderPlayer, () => leader, 1200);
        await using var listenerWorker = new RoomPlayer(listenerPlayer, () => listener, 1200);

        await WaitForListenerAsync(1, Playback.Playing, First);
        leaderPlayer.ManualPlayback(20_000, Playback.Paused);
        await WaitForListenerAsync(2, Playback.Paused, First);
        leaderPlayer.ManualPlayback(51_000, Playback.Paused);
        await WaitForListenerAsync(3, Playback.Paused, First);
        Assert.AreEqual(51_000L, listenerWorker.View.Reading.PositionMs);
        leaderPlayer.ManualPlayback(51_000, Playback.Playing);
        await WaitForListenerAsync(4, Playback.Playing, First);
        leaderPlayer.Manual(Second, 0, Playback.Playing);
        await WaitForListenerAsync(5, Playback.Playing, Second);

        Assert.AreEqual(0, leaderPlayer.Loads);
        Assert.AreEqual(0, leaderPlayer.Seeks);
        Assert.AreEqual(0, leaderPlayer.PlaybackCommands);
        Assert.AreEqual(2, listenerPlayer.Loads);

        async Task WaitForListenerAsync(long revision, Playback playback, TrackId track)
        {
            await Check.EventuallyAsync(() =>
                leader.View.Room?.Listener.Report is { Kind: ReportKind.Applied } report &&
                report.Revision == revision && report.Playback == playback);
            Assert.AreEqual(track, listenerWorker.View.Reading.Track);
        }
    }

    [TestMethod]
    public async Task ListenerHoldsItsQueueUntilTheLeaderAutomaticallyAdvances()
    {
        await using var server = await LocalServer.StartAsync();
        await using var leader = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => leader.View.Status == ConnectionStatus.Connected);
        await using var listener = new RoomConnection(server.Uri, leader.View.Room!.State.RoomId);
        var localNext = new TrackId("3", "4");
        var leaderPlayer = new FakePlayer(() => leader.Clock.Now) { NextOnEnd = Second };
        var listenerPlayer = new FakePlayer(() => listener.Clock.Now) { NextOnEnd = localNext };
        leaderPlayer.Manual(First, 15_000, Playback.Playing);
        await using var leaderWorker = new RoomPlayer(leaderPlayer, () => leader, 1200);
        await using var listenerWorker = new RoomPlayer(listenerPlayer, () => listener, 1200);

        await Check.EventuallyAsync(() => leader.View.Room?.Listener.Report is { Kind: ReportKind.Applied, Revision: 1 });
        listenerPlayer.ManualPlayback(FakePlayer.DurationMs - 1100, Playback.Playing);
        await Check.EventuallyAsync(() => leader.View.Room?.Listener.Report is
            { Kind: ReportKind.Waiting, Detail: "waiting-next-track", Revision: 1 });
        await Task.Delay(1200);
        Assert.AreEqual(0, listenerPlayer.AutoAdvances);
        Assert.AreEqual(Playback.Paused, listenerWorker.View.Reading.Playback);
        Assert.AreEqual(1, listenerPlayer.Loads);

        // Simulate the local queue changing between an observation and the pause command.
        listenerPlayer.Manual(localNext, 0, Playback.Playing, exact: false);
        await Check.EventuallyAsync(() => listenerWorker.View.Reading is { Title: "Song 4", Playback: Playback.Paused });
        Assert.AreEqual(1, listenerPlayer.Loads);

        leaderPlayer.ManualPlayback(FakePlayer.DurationMs, Playback.Playing);
        await Check.EventuallyAsync(() => leader.View.Room?.Listener.Report is
            { Kind: ReportKind.Applied, Revision: 2, Playback: Playback.Playing });
        Assert.AreEqual(Second, listenerWorker.View.Reading.Track);
        Assert.AreEqual(0, leaderPlayer.PlaybackCommands);
        Assert.AreEqual(2, listenerPlayer.Loads);
    }

    [TestMethod]
    public async Task ListenerJoiningNearTheEndResumesAfterTheLeaderSeeksBack()
    {
        await using var server = await LocalServer.StartAsync();
        await using var leader = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => leader.View.Status == ConnectionStatus.Connected);
        Assert.IsNull(await leader.ActionAsync(ActionKind.Adopt, First, FakePlayer.DurationMs - 400, Playback.Playing));
        await using var listener = new RoomConnection(server.Uri, leader.View.Room!.State.RoomId);
        var listenerPlayer = new FakePlayer(() => listener.Clock.Now) { NextOnEnd = Second };
        await using var worker = new RoomPlayer(listenerPlayer, () => listener, 1200);

        await Check.EventuallyAsync(() => leader.View.Room?.Listener.Report is
            { Kind: ReportKind.Waiting, Detail: "waiting-next-track", Revision: 1 });
        Assert.AreEqual(Playback.Paused, worker.View.Reading.Playback);
        Assert.AreEqual(0, listenerPlayer.Seeks);

        Assert.IsNull(await leader.ActionAsync(ActionKind.Seek, position: 30_000));
        await Check.EventuallyAsync(() => leader.View.Room?.Listener.Report is
            { Kind: ReportKind.Applied, Revision: 2, Playback: Playback.Playing });
        Assert.AreEqual(First, worker.View.Reading.Track);
        Assert.AreEqual(1, listenerPlayer.Loads);
    }

    [TestMethod]
    public async Task MutualRoleSwapKeepsPlaybackAndTransfersNativeControl()
    {
        await using var server = await LocalServer.StartAsync();
        await using var host = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => host.View.Status == ConnectionStatus.Connected);
        string code = host.View.Room!.State.RoomId;
        await using var bridge = new DropBridge(server.Uri.Port);
        await using var guest = new RoomConnection(bridge.Uri, code);
        var hostPlayer = new FakePlayer(() => host.Clock.Now);
        var guestPlayer = new FakePlayer(() => guest.Clock.Now);
        hostPlayer.Manual(First, 15_000, Playback.Paused);
        await using var hostWorker = new RoomPlayer(hostPlayer, () => host, 1200);
        await using var guestWorker = new RoomPlayer(guestPlayer, () => guest, 1200);
        await Check.EventuallyAsync(() => host.View.Room?.Listener.Report is { Kind: ReportKind.Applied } &&
            guest.View is { Status: ConnectionStatus.Connected, Room.Swap: not null });

        var before = host.View.Room!.State;
        var swap = guest.View.Room!.Swap!.Id;
        Assert.IsNull(await guest.SwapRolesAsync(swap));
        await Check.EventuallyAsync(() => host.View.Room?.Swap?.RequestedBy == Role.Listener);
        Assert.IsNull(await guest.SwapRolesAsync(swap));
        Assert.AreEqual(Role.Leader, host.View.Role);
        Assert.AreEqual(Role.Listener, guest.View.Role);
        Assert.AreEqual(before, guest.View.Room!.State);

        Assert.IsNull(await host.SwapRolesAsync(swap));
        await Check.EventuallyAsync(() => host.View.Role == Role.Listener && guest.View.Role == Role.Leader &&
            host.View.Room?.Leader.Report is { Kind: ReportKind.Applied, Playback: Playback.Paused } &&
            host.View.Room?.Listener.Report is { Kind: ReportKind.Applied, Playback: Playback.Paused });
        Assert.AreEqual(before with { Revision = before.Revision + 1 }, host.View.Room!.State);
        Assert.AreEqual(1, guestPlayer.Loads);
        Assert.AreEqual(0, hostPlayer.Loads);
        Assert.AreEqual("leader-only", await host.ActionAsync(ActionKind.Play));

        bridge.Drop();
        await Check.EventuallyAsync(() => guest.View is
            { Status: ConnectionStatus.Connected, Generation: 2, Role: Role.Leader });
        guestPlayer.Manual(Second, 10_000, Playback.Playing);
        await Check.EventuallyAsync(() => hostWorker.View.Reading.Track == Second &&
            host.View.Room?.Listener.Report is { Kind: ReportKind.Applied, Playback: Playback.Playing });
        Assert.AreEqual(code, guest.View.Room!.State.RoomId);
        Assert.AreEqual(Second, host.View.Room!.State.Track);
        await guest.LeaveAsync();
        await Check.EventuallyAsync(() => host.View is { Status: ConnectionStatus.Ended, Error: "leader-left" });
    }

    [TestMethod]
    public async Task PromotedListenerCanPickNextTrackAfterSharedTrackEnds()
    {
        await using var server = await LocalServer.StartAsync();
        await using var host = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => host.View.Status == ConnectionStatus.Connected);
        Assert.IsNull(await host.ActionAsync(ActionKind.Adopt, First, FakePlayer.DurationMs, Playback.Playing));
        await using var guest = new RoomConnection(server.Uri, host.View.Room!.State.RoomId);
        var guestPlayer = new FakePlayer(() => guest.Clock.Now);
        await using var guestWorker = new RoomPlayer(guestPlayer, () => guest, 1200);
        await Check.EventuallyAsync(() => host.View.Room?.Listener.Report is
            { Kind: ReportKind.Waiting, Detail: "waiting-next-track" });

        var swap = host.View.Room!.Swap!.Id;
        Assert.IsNull(await host.SwapRolesAsync(swap));
        Assert.IsNull(await guest.SwapRolesAsync(swap));
        await Check.EventuallyAsync(() => guest.View.Role == Role.Leader && guestWorker.View.Detail == "track-ended");
        guestPlayer.Manual(Second, 0, Playback.Playing);
        await Check.EventuallyAsync(() => host.View.Room?.State.Track == Second);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RoleSwapDuringLoadOrSeekCannotPublishThePreviousRolesState(bool duringLoad)
    {
        await using var server = await LocalServer.StartAsync();
        await using var host = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => host.View.Status == ConnectionStatus.Connected);
        Assert.IsNull(await host.ActionAsync(ActionKind.Adopt, First, 30_000, Playback.Paused));
        await using var guest = new RoomConnection(server.Uri, host.View.Room!.State.RoomId);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mock = new FakePlayer(() => guest.Clock.Now)
        {
            HoldFirst = duringLoad ? release.Task : null,
            HoldSeek = duringLoad ? null : release.Task
        };
        await using var worker = new RoomPlayer(mock, () => guest, 1200);
        try
        {
            await Check.EventuallyAsync(() => host.View.Room?.Swap is not null &&
                (duringLoad ? mock.Loads == 1 : mock.Seeks == 1));
            var swap = host.View.Room!.Swap!.Id;
            Assert.IsNull(await host.SwapRolesAsync(swap));
            Assert.IsNull(await guest.SwapRolesAsync(swap));
            await Check.EventuallyAsync(() => guest.View.Role == Role.Leader && host.View.Role == Role.Listener);
            Assert.AreEqual(2L, guest.View.Room!.State.Revision);
            Assert.AreEqual(30_000L, guest.View.Room.State.PositionMs);
            Assert.AreEqual(Playback.Paused, guest.View.Room.State.Playback);
            Assert.IsNull(guest.View.Room.Leader.Report);
            Assert.IsNull(guest.View.Room.Listener.Report);
            Assert.AreEqual("leader-only", await host.ActionAsync(ActionKind.Select, Second));
            Assert.AreEqual("stale-revision", await guest.ActionAsync(ActionKind.Select, Second, expectedRevision: 1));
            Assert.IsNull(await guest.ActionAsync(ActionKind.Select, Second));
            release.TrySetResult();
            await Check.EventuallyAsync(() => guest.View.Room?.Leader.Report is { Kind: ReportKind.Applied, Revision: 3 } &&
                host.View.Room?.State.Revision == 3);
            Assert.AreEqual(Second, worker.View.Reading.Track);
            Assert.AreEqual(Playback.Playing, worker.View.Reading.Playback);
            Assert.AreEqual(3L, host.View.Room!.State.Revision);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task ReconnectingLeaderKeepsNativeChangesMadeWhileOffline()
    {
        await using var server = await LocalServer.StartAsync();
        await using var bridge = new DropBridge(server.Uri.Port);
        await using var leader = new RoomConnection(bridge.Uri, null);
        await Check.EventuallyAsync(() => leader.View.Status == ConnectionStatus.Connected);
        await using var listener = new RoomConnection(server.Uri, leader.View.Room!.State.RoomId);
        var leaderPlayer = new FakePlayer(() => leader.Clock.Now);
        leaderPlayer.Manual(First, 15_000, Playback.Playing);
        await using var worker = new RoomPlayer(leaderPlayer, () => leader, 1200);
        await Check.EventuallyAsync(() => listener.View.Room?.Leader.Report is { Kind: ReportKind.Applied, Revision: 1 });

        bridge.Drop();
        await Check.EventuallyAsync(() => leader.View.Status == ConnectionStatus.Reconnecting);
        leaderPlayer.ManualPlayback(60_000, Playback.Paused);
        await Check.EventuallyAsync(() => listener.View.Room?.State is { Revision: 2, PositionMs: 60_000, Playback: Playback.Paused });
        Assert.AreEqual(2L, leader.View.Generation);
        Assert.AreEqual(0, leaderPlayer.Seeks);
        Assert.AreEqual(0, leaderPlayer.PlaybackCommands);
    }
}

internal sealed class FakePlayer(Func<long> now) : IPlayer
{
    private readonly object gate = new();
    public const long DurationMs = 225_000;
    private TrackId? track;
    private Playback playback = Playback.Paused;
    private long position, anchored;
    private bool exact;
    private int loads, seeks, playbackCommands, autoAdvances;
    public int Loads => Volatile.Read(ref loads);
    public int Seeks => Volatile.Read(ref seeks);
    public int PlaybackCommands => Volatile.Read(ref playbackCommands);
    public int AutoAdvances => Volatile.Read(ref autoAdvances);
    public volatile bool Running = true;
    public TrackId? Unavailable { get; init; }
    public TrackId? NextOnEnd { get; init; }
    public Task? HoldFirst { get; init; }
    public Task? HoldSeek { get; init; }
    public Func<FakePlayer, CancellationToken, Task>? BeforeObserve { get; init; }

    public void Manual(TrackId selected, long ms, Playback state, bool exact = true)
    {
        lock (gate)
        {
            track = selected;
            position = ms;
            playback = state;
            anchored = now();
            this.exact = exact;
        }
    }

    public async Task<PlayerReading> ObserveAsync(CancellationToken token)
    {
        if (BeforeObserve is not null)
            await BeforeObserve(this, token);
        lock (gate)
        {
            if (Running && track is not null && NextOnEnd is { } next && track != next &&
                playback == Playback.Playing && PositionAt(now()) == DurationMs)
            {
                Manual(next, 0, Playback.Playing);
                Interlocked.Increment(ref autoAdvances);
            }
            return Read();
        }
    }

    public void ManualPlayback(long ms, Playback state)
    {
        lock (gate)
        {
            position = ms;
            playback = state;
            anchored = now();
        }
    }

    public async Task<PlayerReading> LoadAsync(TrackId selected, CancellationToken token)
    {
        if (Interlocked.Increment(ref loads) == 1 && HoldFirst is not null)
            await HoldFirst.WaitAsync(token);
        if (selected == Unavailable)
            throw new PlayerException("track-unavailable");
        lock (gate)
        {
            Manual(selected, 0, Playback.Playing);
            return Read();
        }
    }

    public Task<PlayerReading> SetPlaybackAsync(Playback playback, CancellationToken token)
    {
        lock (gate)
        {
            Interlocked.Increment(ref playbackCommands);
            position = PositionAt(now());
            anchored = now();
            this.playback = playback;
            return Task.FromResult(Read());
        }
    }

    public Task<PlayerReading> PauseForHandoffAsync(CancellationToken token) =>
        SetPlaybackAsync(Playback.Paused, token);

    public async Task<PlayerReading> SeekAsync(long positionMs, CancellationToken token)
    {
        if (Interlocked.Increment(ref seeks) == 1 && HoldSeek is not null)
            await HoldSeek.WaitAsync(token);
        lock (gate)
        {
            position = positionMs;
            anchored = now();
            return Read();
        }
    }

    private PlayerReading Read()
    {
        if (!Running || track is null)
            return new(Running);
        long sample = now();
        return new(true, "Song " + track.Id, "Artist", "Album", playback, PositionAt(sample), DurationMs,
            exact ? track : null, sample);
    }

    private long PositionAt(long sample) => Math.Min(DurationMs,
        position + (playback == Playback.Playing ? Math.Max(0, sample - anchored) : 0));
}
