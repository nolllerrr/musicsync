using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MusicSync.Core;
using MusicSync.Server;

namespace MusicSync.Tests;

[TestClass]
public sealed class GroupIntegrationTests
{
    private static readonly TrackId First = new("10030", "38634573");
    private static readonly TrackId Second = new("42185896", "151629851");

    [TestMethod]
    [DataRow("--Sync:MaxParticipantsPerRoom=0")]
    [DataRow("--Sync:MaxParticipantsPerRoom=11")]
    [DataRow("--Sync:MaxConnections=1")]
    [DataRow("--Sync:MaxConnections=1297")]
    [DataRow("--Sync:MaxRooms=129")]
    [DataRow("--Sync:GraceSeconds=4")]
    [DataRow("--Sync:MaxRoomsPerSource=0")]
    [DataRow("--Sync:MaxRoomCreationsPerSource=0")]
    [DataRow("--Sync:RoomCreationWindowSeconds=0")]
    [DataRow("--Sync:TrustedProxy=invalid")]
    public void InvalidServerLimitsAreRejectedAtStartup(string argument) =>
        Assert.ThrowsExactly<InvalidOperationException>(() => ServerHost.Build([argument]));

    [TestMethod]
    public async Task DefaultConnectionBudgetFitsSixteenFullRoomsAndSessionReplacement()
    {
        await using var server = await LocalServer.StartAsync("--Sync:MaxRoomsPerSource=16", "--Sync:MaxRoomCreationsPerSource=16");
        var peers = new List<Peer>();
        var rooms = new List<string>();
        try
        {
            for (int room = 0; room < 16; room++)
            {
                var leader = await Peer.OpenAsync(server.Uri, "create");
                peers.Add(leader);
                string code = (await leader.NextAsync("welcome")).Room!.State.RoomId;
                rooms.Add(code);
                for (int seat = 1; seat < 10; seat++)
                {
                    var listener = await Peer.OpenAsync(server.Uri, "join", code);
                    peers.Add(listener);
                    var welcome = await listener.NextAsync("welcome");
                    Assert.HasCount(seat + 1, welcome.Room!.Participants);
                }
            }
            Assert.HasCount(160, peers);
            using var resumed = await Peer.OpenAsync(server.Uri, "resume", rooms[0], token: peers[0].Token);
            Assert.AreEqual(Role.Leader, (await resumed.NextAsync("welcome")).Role);
            Assert.IsNull(await peers[^10].ActionAsync(new(ActionKind.Select, 0, First)));
            await peers[^1].SnapshotAsync(v => v.State.Track == First);
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
    }

    [TestMethod]
    public async Task TenRealConnectionsSynchronizeAndTransferToSelectedListener()
    {
        await using var server = await LocalServer.StartAsync();
        var connections = new List<RoomConnection>();
        var workers = new List<RoomPlayer>();
        try
        {
            var leader = new RoomConnection(server.Uri, null);
            connections.Add(leader);
            await Check.EventuallyAsync(() => leader.View.Status == ConnectionStatus.Connected);
            string code = leader.View.Room!.State.RoomId;
            for (int i = 1; i < 10; i++)
                connections.Add(new(server.Uri, code));
            await Check.EventuallyAsync(() => connections.All(c => c.View is
            { Status: ConnectionStatus.Connected, Participants.Count: 10, Capacity: 10 }));
            Assert.AreEqual(10, connections.Select(c => c.View.SelfParticipantId).Distinct().Count());
            await using var extra = new RoomConnection(server.Uri, code);
            await Check.EventuallyAsync(() => extra.View.Status == ConnectionStatus.Ended);
            Assert.AreEqual("room-full", extra.View.Error);

            Assert.IsNull(await leader.ActionAsync(ActionKind.Adopt, First, 10_000, Playback.Paused));
            await Check.EventuallyAsync(() => connections.All(c => c.View.Room?.State.Revision == 1));
            var players = connections.Select(c => new FakePlayer(() => c.Clock.Now)).ToArray();
            for (int i = 0; i < connections.Count; i++)
            {
                var client = connections[i];
                workers.Add(new(players[i], () => client, 1200));
            }
            await AppliedAsync(1, First, Playback.Paused);
            Assert.IsNull(await leader.ActionAsync(ActionKind.Play));
            await AppliedAsync(2, First, Playback.Playing);
            Assert.IsNull(await leader.ActionAsync(ActionKind.Pause));
            await AppliedAsync(3, First, Playback.Paused);
            Assert.IsNull(await leader.ActionAsync(ActionKind.Seek, position: 50_000));
            await AppliedAsync(4, First, Playback.Paused);
            Assert.IsTrue(workers.All(w => w.View.Reading.PositionMs == 50_000));
            Assert.IsNull(await leader.ActionAsync(ActionKind.Adopt, Second, 15_000, Playback.Paused));
            await AppliedAsync(5, Second, Playback.Paused);

            var target = connections[7];
            Guid targetId = target.View.SelfParticipantId!.Value;
            Assert.IsNull(await leader.TransferLeadershipAsync(targetId));
            await Check.EventuallyAsync(() => connections.All(c => c.View.Room?.Transfer is not null));
            var offer = target.View.Room!.Transfer!;
            Assert.AreEqual("leadership-transfer-target-only", await connections[1].AcceptLeadershipAsync(offer.Id));
            Assert.AreEqual(Role.Leader, leader.View.Role);
            Assert.IsNull(await target.AcceptLeadershipAsync(offer.Id));
            await Check.EventuallyAsync(() => connections.All(c => c.View.Room?.State.Revision == 6));
            Assert.IsTrue(connections.All(c => c.View.Participants.Single(p => p.Role == Role.Leader).ParticipantId == targetId));
            Assert.AreEqual("leader-only", await leader.ActionAsync(ActionKind.Play));
            Assert.AreEqual("stale-revision", await target.ActionAsync(ActionKind.Play, expectedRevision: 5));
            Assert.IsNull(await target.ActionAsync(ActionKind.Play));
            await AppliedAsync(7, Second, Playback.Playing);

            // Native changes by the former leader are corrected locally; the new leader controls everyone.
            players[0].ManualPlayback(90_000, Playback.Paused);
            players[7].ManualPlayback(40_000, Playback.Paused);
            await Check.EventuallyAsync(() => target.View.Room?.State is
            { Revision: 8, PositionMs: 40_000, Playback: Playback.Paused });
            await AppliedAsync(8, Second, Playback.Paused);
            Assert.IsTrue(workers.All(w => w.View.Reading.PositionMs == 40_000));
            await target.LeaveAsync();
            await Check.EventuallyAsync(() => connections.Where(c => c != target)
                .All(c => c.View.Status == ConnectionStatus.Ended));

            Task AppliedAsync(long revision, TrackId track, Playback playback) => Check.EventuallyAsync(() =>
                connections.All(c => c.View.Participants.Count == 10 && c.View.Participants.All(p =>
                    p.Report is { Kind: ReportKind.Applied or ReportKind.Corrected } report && report.Revision == revision)) &&
                workers.All(w => w.View.Reading.Track == track && w.View.Reading.Playback == playback));
        }
        finally
        {
            await Task.WhenAll(workers.Select(w => w.DisposeAsync().AsTask()));
            await Task.WhenAll(connections.Select(c => c.DisposeAsync().AsTask()));
        }
    }

    [TestMethod]
    public async Task ConcurrentOffersHaveOneWinnerAndOnlyTargetMayAcceptOrDecline()
    {
        await using var server = await LocalServer.StartAsync();
        await using var leader = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => leader.View.Status == ConnectionStatus.Connected);
        await using var one = new RoomConnection(server.Uri, leader.View.Room!.State.RoomId);
        await using var two = new RoomConnection(server.Uri, leader.View.Room.State.RoomId);
        await Check.EventuallyAsync(() => one.View.Status == ConnectionStatus.Connected && two.View.Status == ConnectionStatus.Connected);
        var results = await Task.WhenAll(leader.TransferLeadershipAsync(one.View.SelfParticipantId!.Value),
            leader.TransferLeadershipAsync(two.View.SelfParticipantId!.Value));
        Assert.AreEqual(1, results.Count(r => r is null));
        Assert.AreEqual(1, results.Count(r => r == "leadership-transfer-pending"));
        await Check.EventuallyAsync(() => one.View.Room?.Transfer is not null && two.View.Room?.Transfer is not null);
        var offer = leader.View.Room!.Transfer!;
        var target = offer.TargetParticipantId == one.View.SelfParticipantId ? one : two;
        var bystander = target == one ? two : one;
        Assert.AreEqual("leadership-transfer-forbidden", await bystander.CancelLeadershipAsync(offer.Id));
        Assert.IsNull(await target.CancelLeadershipAsync(offer.Id));
        await Check.EventuallyAsync(() => leader.View.Room?.Transfer is null && target.View.Room?.Transfer is null);
        Assert.AreEqual(0L, leader.View.Room!.State.Revision);
        Assert.AreEqual("leadership-transfer-stale", await target.AcceptLeadershipAsync(offer.Id));
        Assert.IsNull(await leader.TransferLeadershipAsync(target.View.SelfParticipantId!.Value));
        await Check.EventuallyAsync(() => target.View.Room?.Transfer is not null);
        var next = target.View.Room!.Transfer!;
        var race = await Task.WhenAll(leader.CancelLeadershipAsync(next.Id), target.AcceptLeadershipAsync(next.Id));
        Assert.AreEqual(1, race.Count(r => r is null));
        Assert.AreEqual(1, race.Count(r => r == "leadership-transfer-stale"));
        await Check.EventuallyAsync(() => leader.View.Room?.Transfer is null && target.View.Room?.Transfer is null);
        Assert.AreEqual(1, leader.View.Participants.Count(p => p.Role == Role.Leader));
        Assert.IsTrue(leader.View.Room!.State.Revision is 0 or 1);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ParticipantDisconnectInvalidatesConsentAndResumePreservesIdentity(bool dropLeader)
    {
        await using var server = await LocalServer.StartAsync();
        await using var bridge = new DropBridge(server.Uri.Port);
        await using var leader = new RoomConnection(dropLeader ? bridge.Uri : server.Uri, null);
        await Check.EventuallyAsync(() => leader.View.Status == ConnectionStatus.Connected);
        await using var target = new RoomConnection(dropLeader ? server.Uri : bridge.Uri, leader.View.Room!.State.RoomId);
        await Check.EventuallyAsync(() => target.View.Status == ConnectionStatus.Connected);
        var id = target.View.SelfParticipantId!.Value;
        Assert.IsNull(await leader.TransferLeadershipAsync(id));
        await Check.EventuallyAsync(() => target.View.Room?.Transfer is not null);
        var offer = target.View.Room!.Transfer!;
        bridge.Drop();
        var affected = dropLeader ? leader : target;
        var observer = dropLeader ? target : leader;
        await Check.EventuallyAsync(() => affected.View.Status == ConnectionStatus.Reconnecting);
        await Check.EventuallyAsync(() => observer.View.Room?.Transfer is null);
        await Check.EventuallyAsync(() => affected.View is { Status: ConnectionStatus.Connected, Generation: 2 });
        Assert.AreEqual(id, target.View.SelfParticipantId);
        Assert.AreEqual("leadership-transfer-stale", await target.AcceptLeadershipAsync(offer.Id));
        Assert.AreEqual(Role.Leader, leader.View.Role);
    }

    [TestMethod]
    public void ReportsCoalesceAndConsentExpiresWithoutPlaybackChanges()
    {
        var time = new PollTime();
        var clock = new MonotonicClock(time);
        var rooms = new RoomRegistry(clock, 1, 10, TimeSpan.FromSeconds(90), 4, 8, TimeSpan.FromSeconds(60));
        var peers = Enumerable.Range(0, 10).Select(_ => new RoomPeer(true, true)).ToArray();
        try
        {
            Assert.IsNull(rooms.Enter(peers[0], new(Protocol.Version, "create", Token: Protocol.NewToken()), IPAddress.Loopback));
            foreach (var peer in peers.Skip(1))
                Assert.IsNull(rooms.Enter(peer, new(Protocol.Version, "join", Token: Protocol.NewToken(), RoomId: peers[0].RoomId), IPAddress.Loopback));
            Drain();
            for (int i = 0; i < 20; i++)
                foreach (var peer in peers)
                    Assert.IsTrue(rooms.Handle(peer, new(Protocol.Version, "report", Report: new(0, ReportKind.Failed, Detail: $"error-{i}")), clock.Now));
            Assert.IsTrue(peers.All(p => !p.Outgoing.TryPeek(out _)));
            rooms.FlushReports();
            foreach (var peer in peers)
            {
                Assert.IsTrue(peer.Outgoing.TryRead(out var snapshot));
                Assert.IsTrue(snapshot!.Room!.Participants.All(p => p.Report?.Detail == "error-19"));
                Assert.IsFalse(peer.Outgoing.TryRead(out _));
                Assert.IsFalse(peer.Disconnect.IsCancellationRequested);
            }
            rooms.FlushReports();
            Assert.IsTrue(peers.All(p => !p.Outgoing.TryPeek(out _)));
            Assert.IsTrue(rooms.Handle(peers[0], new(Protocol.Version, "transfer-leadership", Id: Guid.NewGuid(),
                TargetParticipantId: peers[7].ParticipantId), clock.Now));
            var offer = ReadOffer();
            Drain();
            time.Advance(TimeSpan.FromMilliseconds(LeadershipTransfer.WindowMs + 1));
            Assert.IsTrue(rooms.Handle(peers[7], new(Protocol.Version, "accept-leadership", Id: Guid.NewGuid(), TransferId: offer.Id), clock.Now));
            Assert.IsTrue(peers[7].Outgoing.TryRead(out var rejected));
            Assert.AreEqual("leadership-transfer-expired", rejected!.Error);
            rooms.FlushReports();
            Assert.IsTrue(peers[0].Outgoing.TryRead(out var expired));
            Assert.IsNull(expired!.Room!.Transfer);
            Assert.AreEqual(0L, expired.Room.State.Revision);
            Assert.AreEqual(Role.Leader, peers[0].Role);

            LeadershipTransfer ReadOffer()
            {
                Assert.IsTrue(peers[0].Outgoing.TryRead(out var snapshot));
                return snapshot!.Room!.Transfer!;
            }
            void Drain() { foreach (var peer in peers) while (peer.Outgoing.TryRead(out _)) { } }
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
    }

    [TestMethod]
    public void MaximumLegalSnapshotFitsEightKiBIncludingTenReportsAndTransfer()
    {
        var report = new PlayerReport(long.MaxValue, ReportKind.Corrected, Protocol.MaxPositionMs,
            Protocol.MaxPositionMs, long.MaxValue, 30_000, Playback.Playing, 120_000, new string('z', 64));
        Assert.IsTrue(report.IsValid);
        var participants = Enumerable.Range(0, 10).Select(i => new Participant(Guid.NewGuid(),
            i == 0 ? Role.Leader : Role.Listener, false, report)).ToArray();
        var state = new RoomState(Protocol.NewCode(), long.MaxValue, long.MaxValue,
            new(new string('9', 19), new string('9', 19)), Playback.Playing,
            Protocol.MaxPositionMs, long.MaxValue, false);
        var room = new RoomView(state, participants, 10, Transfer: new(Guid.NewGuid(),
            participants[0].ParticipantId, participants[1].ParticipantId, long.MaxValue));
        var message = new ServerMessage(Protocol.Version, "welcome", Role.Listener, room,
            ParticipantId: participants[9].ParticipantId);
        int size = JsonSerializer.SerializeToUtf8Bytes(message, Protocol.Json).Length;
        Console.WriteLine($"Maximum group welcome: {size} bytes (limit {Protocol.MaxServerMessageBytes}).");
        Assert.IsGreaterThan(4096, size);
        Assert.IsLessThanOrEqualTo(8192, size);
        Assert.IsFalse((report with { Detail = new string('a', 65) }).IsValid);
        Assert.IsFalse((report with { Detail = "arbitrary text <tag>" }).IsValid);
    }
}
