using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MusicSync.Core;
using MusicSync.Server;

[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.MethodLevel)]

namespace MusicSync.Tests;

[TestClass]
public sealed class ServerTests
{
    private static readonly TrackId Track = new("10030", "38634573");

    [TestMethod]
    public async Task TenParticipantsHaveUniquePublicIdsAndAllReceiveSharedState()
    {
        await using var server = await LocalServer.StartAsync();
        using var group = await Group.OpenAsync(server.Uri, 10);
        var full = await group.Leader.SnapshotAsync(view => view.Participants.Count == 10);
        Assert.AreEqual(10, full.Capacity);
        Assert.AreEqual(1, full.Participants.Count(p => p.Role == Role.Leader));
        Assert.AreEqual(9, full.Participants.Count(p => p.Role == Role.Listener));
        Assert.IsTrue(full.Participants.All(p => p.Connected && p.Report is null));
        Assert.AreEqual(10, full.Participants.Select(p => p.ParticipantId).Distinct().Count());
        foreach (var member in group.Members)
        {
            var own = member.Welcome.Room!.Participants.Single(p => p.ParticipantId == member.Welcome.ParticipantId);
            Assert.AreNotEqual(Guid.Empty, own.ParticipantId);
            Assert.AreEqual(member.Welcome.Role, own.Role);
        }
        using var eleventh = await Peer.OpenAsync(server.Uri, "join", group.RoomId);
        Assert.AreEqual("room-full", (await eleventh.NextAsync("error")).Error);
        Assert.AreEqual("leader-only", await group.Members[9].Peer.ActionAsync(new(ActionKind.Select, 0, Track)));
        Assert.IsNull(await group.Leader.ActionAsync(new(ActionKind.Select, 0, Track)));
        var snapshots = await Task.WhenAll(group.Members.Select(m => m.Peer.SnapshotAsync(v => v.State.Revision == 1)));
        Assert.IsTrue(snapshots.All(v => v.State == snapshots[0].State && v.Participants.Count == 10));
    }

    [TestMethod]
    public async Task ExplicitListenerLeaveFreesOnlyItsSeatAndBroadcastsMembership()
    {
        await using var server = await LocalServer.StartAsync("--Sync:MaxParticipantsPerRoom=3");
        using var group = await Group.OpenAsync(server.Uri, 3);
        var leaving = group.Members[1];
        await leaving.Peer.LeaveAsync();
        foreach (var member in new[] { group.Members[0], group.Members[2] })
        {
            var left = await member.Peer.SnapshotAsync(v => v.Participants.Count == 2 &&
                !v.Participants.Any(p => p.ParticipantId == leaving.Welcome.ParticipantId));
            Assert.IsFalse(left.Participants.Any(p => p.ParticipantId == leaving.Welcome.ParticipantId));
            Assert.IsTrue(left.Participants.All(p => p.Connected));
        }
        using var expired = await Peer.OpenAsync(server.Uri, "resume", group.RoomId, token: leaving.Peer.Token);
        Assert.AreEqual("room-unavailable", (await expired.NextAsync("error")).Error);
        using var replacement = await Peer.OpenAsync(server.Uri, "join", group.RoomId);
        var welcome = await replacement.NextAsync("welcome");
        Assert.HasCount(3, welcome.Room!.Participants);
        Assert.AreNotEqual(leaving.Welcome.ParticipantId, welcome.ParticipantId);
        await group.Members[2].Peer.SnapshotAsync(v => v.Participants.Any(p => p.ParticipantId == welcome.ParticipantId));
    }

    [TestMethod]
    public async Task DuplicateTokenReplacesConnectionWithoutAllocatingASecondSeat()
    {
        await using var server = await LocalServer.StartAsync("--Sync:MaxParticipantsPerRoom=3");
        using var group = await Group.OpenAsync(server.Uri, 3);
        var original = group.Members[1];
        using var replacement = await Peer.OpenAsync(server.Uri, "join", group.RoomId, token: original.Peer.Token);
        var welcome = await replacement.NextAsync("welcome");
        Assert.AreEqual(original.Welcome.ParticipantId, welcome.ParticipantId);
        Assert.HasCount(3, welcome.Room!.Participants);
        original.Peer.Abort();
        var report = new PlayerReport(0, ReportKind.Applying);
        await replacement.SendAsync(new(Protocol.Version, "report", Report: report));
        var active = await group.Leader.SnapshotAsync(v => v.Participants.Any(p =>
            p.ParticipantId == welcome.ParticipantId && p.Report == report));
        Assert.IsTrue(active.Participants.All(p => p.Connected));
        using var extra = await Peer.OpenAsync(server.Uri, "join", group.RoomId);
        Assert.AreEqual("room-full", (await extra.NextAsync("error")).Error);

        using var otherLeader = await Peer.OpenAsync(server.Uri, "create");
        string otherRoom = (await otherLeader.NextAsync("welcome")).Room!.State.RoomId;
        using var crossRoom = await Peer.OpenAsync(server.Uri, "join", otherRoom, token: original.Peer.Token);
        Assert.AreEqual("invalid-session", (await crossRoom.NextAsync("error")).Error);
        using var secondRole = await Peer.OpenAsync(server.Uri, "create", token: original.Peer.Token);
        Assert.AreEqual("invalid-session", (await secondRole.NextAsync("error")).Error);
    }

    [TestMethod]
    public async Task DisconnectedListenerKeepsReservedSeatAndResumesWithSameId()
    {
        await using var server = await LocalServer.StartAsync("--Sync:MaxParticipantsPerRoom=3");
        using var group = await Group.OpenAsync(server.Uri, 3);
        var dropped = group.Members[1];
        dropped.Peer.Abort();
        foreach (var member in new[] { group.Members[0], group.Members[2] })
        {
            var view = await member.Peer.SnapshotAsync(v => v.Participants.Any(p =>
                p.ParticipantId == dropped.Welcome.ParticipantId && !p.Connected));
            Assert.HasCount(3, view.Participants);
            Assert.AreEqual(2, view.Participants.Count(p => p.Connected));
        }
        using var denied = await Peer.OpenAsync(server.Uri, "join", group.RoomId);
        Assert.AreEqual("room-full", (await denied.NextAsync("error")).Error);
        Assert.IsNull(await group.Leader.ActionAsync(new(ActionKind.Select, 0, Track)));
        using var resumed = await Peer.OpenAsync(server.Uri, "resume", group.RoomId, token: dropped.Peer.Token);
        var welcome = await resumed.NextAsync("welcome");
        Assert.AreEqual(dropped.Welcome.ParticipantId, welcome.ParticipantId);
        Assert.AreEqual(1L, welcome.Room!.State.Revision);
        Assert.IsTrue(welcome.Room.Participants.All(p => p.Connected));
        await group.Members[2].Peer.SnapshotAsync(v => v.Participants.All(p => p.Connected));
    }

    [TestMethod]
    public async Task ReportsRemainIndependentAndLargeSnapshotsFitTheServerFrameLimit()
    {
        await using var server = await LocalServer.StartAsync();
        using var group = await Group.OpenAsync(server.Uri, 10);
        for (int i = 0; i < group.Members.Count; i++)
            await group.Members[i].Peer.SendAsync(new(Protocol.Version, "report",
                Report: new(0, ReportKind.Failed, Detail: new string((char)('А' + i), 350))));
        var reports = await group.Leader.SnapshotAsync(v => v.Participants.All(p => p.Report is not null));
        for (int i = 0; i < group.Members.Count; i++)
            Assert.AreEqual(new string((char)('А' + i), 350), reports.Participants.Single(p =>
                p.ParticipantId == group.Members[i].Welcome.ParticipantId).Report!.Detail);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new ServerMessage(Protocol.Version, "snapshot", Room: reports), Protocol.Json);
        Assert.IsGreaterThan(Protocol.MaxMessageBytes, json.Length);
        Assert.IsLessThanOrEqualTo(Protocol.MaxServerMessageBytes, json.Length);
        using var document = JsonDocument.Parse(json);
        var room = document.RootElement.GetProperty("room");
        Assert.IsFalse(room.TryGetProperty("leader", out _));
        Assert.IsFalse(room.TryGetProperty("listener", out _));
        string serialized = System.Text.Encoding.UTF8.GetString(json);
        foreach (var member in group.Members)
            Assert.IsFalse(serialized.Contains(member.Peer.Token, StringComparison.Ordinal));

        var last = group.Members[9];
        await last.Peer.SnapshotAsync(v => v.Participants.All(p => p.Report is not null));
        await last.Peer.SendAsync(new(Protocol.Version, "report", Report: new(0, ReportKind.Applying)));
        var updated = await group.Leader.SnapshotAsync(v => v.Participants.Any(p =>
            p.ParticipantId == last.Welcome.ParticipantId && p.Report?.Kind == ReportKind.Applying));
        Assert.AreEqual(9, updated.Participants.Count(p => p.Report?.Kind == ReportKind.Failed));
        var dropped = group.Members[8];
        dropped.Peer.Abort();
        var disconnected = await group.Leader.SnapshotAsync(v => v.Participants.Any(p =>
            p.ParticipantId == dropped.Welcome.ParticipantId && !p.Connected));
        Assert.AreEqual(8, disconnected.Participants.Count(p => p.Report?.Kind == ReportKind.Failed));
        Assert.AreEqual(ReportKind.Applying, disconnected.Participants.Single(p =>
            p.ParticipantId == last.Welcome.ParticipantId).Report!.Kind);
        Assert.IsNull(disconnected.Participants.Single(p => p.ParticipantId == dropped.Welcome.ParticipantId).Report);
        Assert.IsNull(await group.Leader.ActionAsync(new(ActionKind.Select, 0, Track)));
        var reset = await last.Peer.SnapshotAsync(v => v.State.Revision == 1);
        Assert.IsTrue(reset.Participants.All(p => p.Report is null));
        await last.Peer.SendAsync(new(Protocol.Version, "report", Report: new(0, ReportKind.Applying)));
        await last.Peer.SendAsync(new(Protocol.Version, "report", Report: new(1, ReportKind.Waiting)));
        var latest = await group.Leader.SnapshotAsync(v => v.Participants.Any(p => p.Report?.Kind == ReportKind.Waiting));
        Assert.AreEqual(1, latest.Participants.Count(p => p.Report is not null));
    }

    [TestMethod]
    public async Task LeaderLeaveEndsEveryListenerAndInvalidatesAllSessions()
    {
        await using var server = await LocalServer.StartAsync();
        using var group = await Group.OpenAsync(server.Uri, 10);
        await group.Leader.LeaveAsync();
        foreach (var member in group.Members.Skip(1))
            Assert.AreEqual("leader-left", (await member.Peer.NextAsync("ended")).Error);
        foreach (var member in group.Members)
        {
            using var resume = await Peer.OpenAsync(server.Uri, "resume", group.RoomId, token: member.Peer.Token);
            Assert.AreEqual("room-unavailable", (await resume.NextAsync("error")).Error);
        }
    }

    [TestMethod]
    public async Task LeaderCanResumeDuringGraceWithoutLosingRoomOrIdentity()
    {
        await using var server = await LocalServer.StartAsync();
        using var group = await Group.OpenAsync(server.Uri, 3);
        Assert.IsNull(await group.Leader.ActionAsync(new(ActionKind.Select, 0, Track)));
        group.Leader.Abort();
        foreach (var member in group.Members.Skip(1))
        {
            var waiting = await member.Peer.SnapshotAsync(v => !v.Leader.Connected);
            Assert.AreEqual(1L, waiting.State.Revision);
            Assert.AreEqual(1, waiting.Participants.Count(p => p.Role == Role.Leader));
            Assert.AreEqual("leader-only", await member.Peer.ActionAsync(new(ActionKind.Pause, 1)));
        }
        using var resume = await Peer.OpenAsync(server.Uri, "resume", group.RoomId, token: group.Leader.Token);
        var welcome = await resume.NextAsync("welcome");
        Assert.AreEqual(group.Members[0].Welcome.ParticipantId, welcome.ParticipantId);
        Assert.AreEqual(Role.Leader, welcome.Role);
        Assert.AreEqual(Track, welcome.Room!.State.Track);
        Assert.IsNull(await resume.ActionAsync(new(ActionKind.Pause, 1)));
        foreach (var member in group.Members.Skip(1))
            await member.Peer.SnapshotAsync(v => v.State.Revision == 2 && v.Leader.Connected);
    }

    [TestMethod]
    public async Task ListenerGraceExpiryReleasesReservationWithoutEndingTheGroup()
    {
        await using var server = await LocalServer.StartAsync("--Sync:GraceSeconds=5", "--Sync:MaxParticipantsPerRoom=3");
        using var group = await Group.OpenAsync(server.Uri, 3);
        var dropped = group.Members[1];
        dropped.Peer.Abort();
        await group.Leader.SnapshotAsync(v => v.Participants.Any(p => !p.Connected));
        using var denied = await Peer.OpenAsync(server.Uri, "join", group.RoomId);
        Assert.AreEqual("room-full", (await denied.NextAsync("error")).Error);
        var expired = await group.Leader.SnapshotAsync(v => v.Participants.Count == 2, seconds: 13);
        Assert.IsTrue(expired.Participants.All(p => p.Connected));
        await group.Members[2].Peer.SnapshotAsync(v => v.Participants.Count == 2);
        using var old = await Peer.OpenAsync(server.Uri, "resume", group.RoomId, token: dropped.Peer.Token);
        Assert.AreEqual("room-unavailable", (await old.NextAsync("error")).Error);
        using var replacement = await Peer.OpenAsync(server.Uri, "join", group.RoomId);
        Assert.HasCount(3, (await replacement.NextAsync("welcome")).Room!.Participants);
    }

    [TestMethod]
    public async Task LeaderGraceExpiryEndsAllRemainingListeners()
    {
        await using var server = await LocalServer.StartAsync("--Sync:GraceSeconds=5");
        using var group = await Group.OpenAsync(server.Uri, 4);
        group.Leader.Abort();
        var ended = await Task.WhenAll(group.Members.Skip(1).Select(m => m.Peer.NextAsync("ended", seconds: 13)));
        Assert.IsTrue(ended.All(message => message.Error == "leader-timeout"));
        using var resume = await Peer.OpenAsync(server.Uri, "resume", group.RoomId, token: group.Members[2].Peer.Token);
        Assert.AreEqual("room-unavailable", (await resume.NextAsync("error")).Error);
    }

    [TestMethod]
    public async Task ConcurrentJoinsAllocateTheLastSeatExactlyOnce()
    {
        await using var server = await LocalServer.StartAsync("--Sync:MaxParticipantsPerRoom=3");
        using var group = await Group.OpenAsync(server.Uri, 2);
        var contenders = await Task.WhenAll(Peer.OpenAsync(server.Uri, "join", group.RoomId),
            Peer.OpenAsync(server.Uri, "join", group.RoomId));
        using var first = contenders[0];
        using var second = contenders[1];
        var results = await Task.WhenAll(first.NextAsync(null), second.NextAsync(null));
        Assert.AreEqual(1, results.Count(m => m.Type == "welcome"));
        Assert.AreEqual(1, results.Count(m => m.Error == "room-full"));
        var view = await group.Leader.SnapshotAsync(v => v.Participants.Count == 3);
        Assert.AreEqual(3, view.Participants.Select(p => p.ParticipantId).Distinct().Count());
    }

    [TestMethod]
    public async Task GroupMembershipInvalidatesPairSwapAndRejectsAmbiguousRoleChanges()
    {
        await using var server = await LocalServer.StartAsync();
        using var group = await Group.OpenAsync(server.Uri, 2, roleSwap: true);
        var pair = group.Members[1].Welcome.Room!;
        Guid initial = pair.Swap!.Id;
        Assert.IsNull(await group.Leader.SwapRolesAsync(initial));
        using var third = await Peer.OpenAsync(server.Uri, "join", group.RoomId, roleSwap: true);
        Assert.IsNull((await third.NextAsync("welcome")).Room!.Swap);
        await group.Members[1].Peer.SnapshotAsync(v => v.Participants.Count == 3 && v.Swap is null);
        Assert.AreEqual("role-swap-group-unsupported", await group.Members[1].Peer.SwapRolesAsync(initial));
        await third.LeaveAsync();
        var reset = await group.Leader.SnapshotAsync(v => v.Participants.Count == 2 && v.Swap is not null && v.Swap.Id != initial);
        Assert.AreNotEqual(initial, reset.Swap!.Id);
        Assert.AreEqual("role-swap-stale", await group.Leader.SwapRolesAsync(initial));
        Assert.IsNull(await group.Leader.SwapRolesAsync(reset.Swap.Id));
        Assert.IsNull(await group.Members[1].Peer.SwapRolesAsync(reset.Swap.Id));
        var swapped = await group.Leader.SnapshotAsync(v => v.State.Revision == 1);
        Assert.AreEqual(pair.Leader.ParticipantId, swapped.Listener.ParticipantId);
        Assert.AreEqual(pair.Listener.ParticipantId, swapped.Leader.ParticipantId);
    }

    [TestMethod]
    public async Task SourceCreationLimitsDoNotBlockJoinsOrSessionRecovery()
    {
        await using var server = await LocalServer.StartAsync("--Sync:MaxRoomsPerSource=1");
        using var group = await Group.OpenAsync(server.Uri, 3);
        using var denied = await Peer.OpenAsync(server.Uri, "create");
        Assert.AreEqual("room-creation-limited", (await denied.NextAsync("error")).Error);
        using var spoofed = await Peer.OpenAsync(server.Uri, "create", forwardedFor: "198.51.100.9");
        Assert.AreEqual("room-creation-limited", (await spoofed.NextAsync("error")).Error);
        using var recovered = await Peer.OpenAsync(server.Uri, "create", token: group.Leader.Token);
        Assert.AreEqual(group.Members[0].Welcome.ParticipantId, (await recovered.NextAsync("welcome")).ParticipantId);
        await recovered.LeaveAsync();
        using var allowed = await Peer.OpenAsync(server.Uri, "create");
        await allowed.NextAsync("welcome");
    }

    [TestMethod]
    public async Task CreationRateLimitSurvivesExplicitRoomDeletion()
    {
        await using var server = await LocalServer.StartAsync("--Sync:MaxRoomCreationsPerSource=1");
        using var leader = await Peer.OpenAsync(server.Uri, "create");
        await leader.NextAsync("welcome");
        await leader.LeaveAsync();
        using var denied = await Peer.OpenAsync(server.Uri, "create");
        Assert.AreEqual("room-creation-limited", (await denied.NextAsync("error")).Error);
    }

    [TestMethod]
    public async Task CapacityCanBeOneAndUnsafeCapacitySettingsFailAtStartup()
    {
        foreach (int capacity in new[] { -1, 0, 11, int.MaxValue })
            Assert.ThrowsExactly<InvalidOperationException>(() => ServerHost.Build([$"--Sync:MaxParticipantsPerRoom={capacity}"]));
        await using var server = await LocalServer.StartAsync("--Sync:MaxParticipantsPerRoom=1");
        using var group = await Group.OpenAsync(server.Uri, 1);
        Assert.AreEqual(1, group.Members[0].Welcome.Room!.Capacity);
        using var denied = await Peer.OpenAsync(server.Uri, "join", group.RoomId);
        Assert.AreEqual("room-full", (await denied.NextAsync("error")).Error);
    }

    [TestMethod]
    public void SlowPeerQueueOverflowDoesNotBlockOtherParticipants()
    {
        var clock = new MonotonicClock();
        var rooms = new RoomRegistry(clock, 1, 3, TimeSpan.FromSeconds(90), 4, 8, TimeSpan.FromSeconds(60));
        using var leader = new RoomPeer(false);
        using var slow = new RoomPeer(false);
        using var fast = new RoomPeer(false);
        Assert.IsNull(rooms.Enter(leader, new(Protocol.Version, "create", Token: Protocol.NewToken()), IPAddress.Loopback));
        Assert.IsNull(rooms.Enter(slow, new(Protocol.Version, "join", Token: Protocol.NewToken(), RoomId: leader.RoomId), IPAddress.Loopback));
        Assert.IsNull(rooms.Enter(fast, new(Protocol.Version, "join", Token: Protocol.NewToken(), RoomId: leader.RoomId), IPAddress.Loopback));
        Drain(leader);
        Drain(fast);
        for (int i = 0; i < 60; i++)
        {
            Assert.IsTrue(rooms.Handle(leader, new(Protocol.Version, "report",
                Report: new(0, ReportKind.Applying, Detail: i.ToString())), clock.Now));
            Drain(leader);
            Assert.IsTrue(fast.Outgoing.TryRead(out var snapshot));
            Assert.AreEqual(i.ToString(), snapshot.Room!.Leader.Report!.Detail);
        }
        Assert.IsTrue(slow.Disconnect.IsCancellationRequested);
        Assert.IsFalse(leader.Disconnect.IsCancellationRequested);
        Assert.IsFalse(fast.Disconnect.IsCancellationRequested);
        rooms.Detach(slow);
        Assert.IsTrue(fast.Outgoing.TryRead(out var disconnected));
        Assert.AreEqual(2, disconnected.Room!.Participants.Count(p => p.Connected));

        static void Drain(RoomPeer peer)
        {
            while (peer.Outgoing.TryRead(out _)) { }
        }
    }

    [TestMethod]
    public void ReplacedPeerCannotMutateOrDetachItsRecoveredSeat()
    {
        var clock = new MonotonicClock();
        var rooms = new RoomRegistry(clock, 1, 3, TimeSpan.FromSeconds(90), 4, 8, TimeSpan.FromSeconds(60));
        using var old = new RoomPeer(false);
        using var resumed = new RoomPeer(false);
        string token = Protocol.NewToken();
        Assert.IsNull(rooms.Enter(old, new(Protocol.Version, "create", Token: token), IPAddress.Loopback));
        Assert.IsNull(rooms.Enter(resumed, new(Protocol.Version, "resume", Token: token, RoomId: old.RoomId), IPAddress.Loopback));
        Assert.AreEqual(old.ParticipantId, resumed.ParticipantId);
        Assert.IsTrue(old.Disconnect.IsCancellationRequested);
        Assert.IsFalse(rooms.Handle(old, new(Protocol.Version, "action", Id: Guid.NewGuid(),
            Action: new(ActionKind.Select, 0, Track)), clock.Now));
        rooms.Detach(old);
        Assert.IsTrue(rooms.Handle(resumed, new(Protocol.Version, "action", Id: Guid.NewGuid(),
            Action: new(ActionKind.Select, 0, Track)), clock.Now));
        ServerMessage? latest = null;
        while (resumed.Outgoing.TryRead(out var message))
            if (message.Type == "snapshot") latest = message;
        Assert.AreEqual(1L, latest!.Room!.State.Revision);
        Assert.IsTrue(latest.Room.Leader.Connected);
    }

    [TestMethod]
    public async Task GlobalConnectionLimitStillAppliesToGroupMembers()
    {
        await using var server = await LocalServer.StartAsync("--Sync:MaxConnections=2");
        using var group = await Group.OpenAsync(server.Uri, 2);
        using var extra = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsExactlyAsync<WebSocketException>(() => extra.ConnectAsync(server.Uri, timeout.Token));
        Assert.IsNull(await group.Leader.ActionAsync(new(ActionKind.Select, 0, Track)));
        await group.Members[1].Peer.SnapshotAsync(v => v.State.Revision == 1);
    }

    private sealed class Group : IDisposable
    {
        public List<(Peer Peer, ServerMessage Welcome)> Members { get; } = [];
        public Peer Leader => Members[0].Peer;
        public string RoomId => Members[0].Welcome.Room!.State.RoomId;

        public static async Task<Group> OpenAsync(Uri uri, int count, bool roleSwap = false)
        {
            var group = new Group();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var peer = await Peer.OpenAsync(uri, i == 0 ? "create" : "join",
                        i == 0 ? null : group.RoomId, roleSwap: roleSwap);
                    try { group.Members.Add((peer, await peer.NextAsync("welcome"))); }
                    catch { peer.Dispose(); throw; }
                }
                return group;
            }
            catch { group.Dispose(); throw; }
        }

        public void Dispose()
        {
            foreach (var member in Members)
                member.Peer.Dispose();
        }
    }

    [TestMethod]
    public async Task CreatesRoomJoinsOneListenerAndRestrictsActions()
    {
        await using var server = await LocalServer.StartAsync("--Sync:MaxParticipantsPerRoom=2");
        using var leader = await Peer.OpenAsync(server.Uri, "create");
        var created = await leader.NextAsync("welcome");
        Assert.AreEqual(Role.Leader, created.Role);
        var room = created.Room!.State.RoomId;
        Assert.IsTrue(Protocol.IsCode(room));
        Assert.AreEqual(0L, created.Room.State.Revision);
        using var listener = await Peer.OpenAsync(server.Uri, "join", room);
        var joined = await listener.NextAsync("welcome");
        Assert.AreEqual(Role.Listener, joined.Role);
        Assert.IsTrue(joined.Room!.Leader.Connected);
        Assert.IsTrue((await leader.SnapshotAsync(v => v.Listener.Connected)).Listener.Connected);

        using var third = await Peer.OpenAsync(server.Uri, "join", room);
        Assert.AreEqual("room-full", (await third.NextAsync("error")).Error);
        Assert.AreEqual("leader-only", await listener.ActionAsync(new(ActionKind.Select, 0, Track)));
        Assert.IsNull(await leader.ActionAsync(new(ActionKind.Select, 0, Track)));
        var playback = await listener.SnapshotAsync(v => v.State.Revision == 1);
        Assert.AreEqual(Track, playback.State.Track);
        Assert.AreEqual(Playback.Playing, playback.State.Playback);
    }

    [TestMethod]
    public async Task ReconnectInvalidatesConsentAndConcurrentConfirmationsSwapOnlyOnce()
    {
        await using var server = await LocalServer.StartAsync();
        using var leader = await Peer.OpenAsync(server.Uri, "create", roleSwap: true);
        string room = (await leader.NextAsync("welcome")).Room!.State.RoomId;
        using var listener = await Peer.OpenAsync(server.Uri, "join", room, roleSwap: true);
        var initial = (await listener.NextAsync("welcome")).Room!.Swap!.Id;
        Assert.IsNull(await leader.SwapRolesAsync(initial));
        var pending = await listener.SnapshotAsync(view => view.Swap?.RequestedBy == Role.Leader);
        Assert.IsNotNull(pending.Swap!.ExpiresAt);

        listener.Abort();
        await leader.SnapshotAsync(view => !view.Listener.Connected && view.Swap is null);
        using var resumed = await Peer.OpenAsync(server.Uri, "resume", room, token: listener.Token, roleSwap: true);
        var reset = (await resumed.NextAsync("welcome")).Room!.Swap!;
        Assert.AreNotEqual(initial, reset.Id);
        Assert.IsNull(reset.RequestedBy);
        Assert.AreEqual("role-swap-stale", await resumed.SwapRolesAsync(initial));

        var results = await Task.WhenAll(leader.SwapRolesAsync(reset.Id), resumed.SwapRolesAsync(reset.Id));
        Assert.IsTrue(results.All(error => error is null));
        Assert.AreEqual("role-swap-stale", await leader.SwapRolesAsync(reset.Id));
        Assert.AreEqual("leader-only", await leader.ActionAsync(new(ActionKind.Select, 1, Track)));
        Assert.AreEqual("stale-revision", await resumed.ActionAsync(new(ActionKind.Select, 0, Track)));
        Assert.IsNull(await resumed.ActionAsync(new(ActionKind.Select, 1, Track)));
        Assert.AreEqual(2L, (await leader.SnapshotAsync(view => view.State.Track == Track)).State.Revision);
    }

    [TestMethod]
    public async Task PeerWithoutRoleSwapCapabilityCanJoinAndFollowPlayback()
    {
        await using var server = await LocalServer.StartAsync();
        await using var leader = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => leader.View.Status == ConnectionStatus.Connected);
        using var listener = await Peer.OpenAsync(server.Uri, "join", leader.View.Room!.State.RoomId);
        Assert.IsNull((await listener.NextAsync("welcome")).Room!.Swap);
        await Check.EventuallyAsync(() => leader.View.Room?.Listener.Connected == true);
        Assert.IsNull(leader.View.Room!.Swap);
        Assert.IsNull(await leader.ActionAsync(ActionKind.Select, Track));
        Assert.AreEqual(Track, (await listener.SnapshotAsync(view => view.State.Revision == 1)).State.Track);
    }

    [TestMethod]
    public async Task DeduplicatesEventsAndRejectsStaleRevisions()
    {
        await using var server = await LocalServer.StartAsync();
        using var leader = await Peer.OpenAsync(server.Uri, "create");
        string code = (await leader.NextAsync("welcome")).Room!.State.RoomId;
        using var listener = await Peer.OpenAsync(server.Uri, "join", code);
        await listener.NextAsync("welcome");
        var id = Guid.NewGuid();
        var select = new RoomAction(ActionKind.Select, 0, Track);
        Assert.IsNull(await leader.ActionAsync(select, id));
        Assert.IsNull(await leader.ActionAsync(select, id));
        Assert.AreEqual("leader-only", await listener.ActionAsync(select, id));
        Assert.AreEqual("stale-revision", await leader.ActionAsync(new(ActionKind.Pause, 0)));
        Assert.IsNull(await leader.ActionAsync(new(ActionKind.Seek, 1, PositionMs: 22_000)));
        Assert.IsNull(await leader.ActionAsync(new(ActionKind.Pause, 2)));
        var paused = await listener.SnapshotAsync(v => v.State.Revision == 3);
        Assert.AreEqual(1L, paused.State.Selection);
        Assert.AreEqual(Playback.Paused, paused.State.Playback);
        Assert.IsGreaterThanOrEqualTo(22_000L, paused.State.PositionMs);
    }

    [TestMethod]
    public async Task ReusesMembershipAfterLostHandshakeAndExplicitLeaveEndsRoom()
    {
        await using var server = await LocalServer.StartAsync();
        using var first = await Peer.OpenAsync(server.Uri, "create");
        string room = (await first.NextAsync("welcome")).Room!.State.RoomId;
        using var leader = await Peer.OpenAsync(server.Uri, "create", token: first.Token);
        Assert.AreEqual(room, (await leader.NextAsync("welcome")).Room!.State.RoomId);
        using var listener = await Peer.OpenAsync(server.Uri, "join", room);
        await listener.NextAsync("welcome");
        using var impostor = await Peer.OpenAsync(server.Uri, "resume", room);
        Assert.AreEqual("room-unavailable", (await impostor.NextAsync("error")).Error);
        Guid id = Guid.NewGuid();
        await leader.SendAsync(new(Protocol.Version, "leave", Id: id));
        Assert.AreEqual(id, (await leader.NextAsync("result")).Id);
        Assert.AreEqual("leader-left", (await listener.NextAsync("ended")).Error);
    }

    [TestMethod]
    public async Task ResumeReceivesOnlyLatestStateAfterSocketAbort()
    {
        await using var server = await LocalServer.StartAsync();
        using var leader = await Peer.OpenAsync(server.Uri, "create");
        string room = (await leader.NextAsync("welcome")).Room!.State.RoomId;
        using var first = await Peer.OpenAsync(server.Uri, "join", room);
        await first.NextAsync("welcome");
        await leader.SnapshotAsync(v => v.Listener.Connected);
        first.Abort();
        await leader.SnapshotAsync(v => !v.Listener.Connected);
        Assert.IsNull(await leader.ActionAsync(new(ActionKind.Select, 0, Track)));
        Assert.IsNull(await leader.ActionAsync(new(ActionKind.Pause, 1)));
        using var resumed = await Peer.OpenAsync(server.Uri, "resume", room, token: first.Token);
        var latest = (await resumed.NextAsync("welcome")).Room!.State;
        Assert.AreEqual(2L, latest.Revision);
        Assert.AreEqual(Track, latest.Track);
        Assert.AreEqual(Playback.Paused, latest.Playback);
        Assert.AreEqual(2L, (await resumed.NextAsync("snapshot")).Room!.State.Revision);
    }

    [TestMethod]
    public async Task GraceExpiryRemovesRoomAndNotifiesListener()
    {
        await using var server = await LocalServer.StartAsync("--Sync:GraceSeconds=5");
        using var leader = await Peer.OpenAsync(server.Uri, "create");
        string room = (await leader.NextAsync("welcome")).Room!.State.RoomId;
        await using var listener = new RoomConnection(server.Uri, room);
        await Check.EventuallyAsync(() => listener.View.Status == ConnectionStatus.Connected);
        leader.Abort();
        await Check.EventuallyAsync(() => listener.View.Room?.Leader.Connected == false);
        await Check.EventuallyAsync(() => listener.View.Status == ConnectionStatus.Ended, seconds: 13);
        Assert.AreEqual("leader-timeout", listener.View.Error);
        using var expired = await Peer.OpenAsync(server.Uri, "resume", room, token: leader.Token);
        Assert.AreEqual("room-unavailable", (await expired.NextAsync("error")).Error);
    }

    [TestMethod]
    public async Task RejectsIncompatibleProtocolAndMalformedFrames()
    {
        await using var server = await LocalServer.StartAsync();
        foreach (int version in new[] { 1, 999 })
        {
            using var old = await Peer.OpenAsync(server.Uri, "create", version: version);
            var rejection = await old.NextAsync("error");
            Assert.AreEqual("protocol-version", rejection.Error);
            Assert.AreEqual(Protocol.Version, rejection.Version);
            Assert.IsNull(rejection.Room);
            Assert.IsNull(rejection.ParticipantId);
        }
        await RejectedAsync("""{"version":2,"version":2,"type":"create"}""");
        await RejectedAsync("""{"version":2,"type":"create","unexpected":1}""");
        await RejectedAsync(new string(' ', Protocol.MaxMessageBytes + 1));
        using var health = new HttpClient();
        var response = await health.GetAsync(new UriBuilder(server.Uri) { Scheme = "http", Path = "/health" }.Uri);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        async Task RejectedAsync(string text)
        {
            using var socket = new ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await socket.ConnectAsync(server.Uri, timeout.Token);
            await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, timeout.Token);
            try
            {
                var close = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[4096]), timeout.Token);
                Assert.AreEqual(WebSocketMessageType.Close, close.MessageType);
            }
            catch (WebSocketException) { }
        }
    }

    [TestMethod]
    public async Task TlsModeRejectsPlainWsAndClientRejectsUntrustedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid("1.3.6.1.5.5.7.3.1")
        }, critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        string file = Path.Combine(Path.GetTempPath(), $"MusicSync-test-{Guid.NewGuid():N}.pfx");
        string password = Protocol.NewToken();
        File.WriteAllBytes(file, certificate.Export(X509ContentType.Pkcs12, password));
        try
        {
            await using var server = await LocalServer.StartAsync("--Sync:Mode=Tls",
                $"--Sync:TlsPfxPath={file}", $"--Sync:TlsPfxPassword={password}");
            Assert.AreEqual("wss", server.Uri.Scheme);
            await using var client = new RoomConnection(server.Uri, null);
            await Check.EventuallyAsync(() => client.View.Status == ConnectionStatus.Reconnecting);
            Assert.IsNull(client.View.Room);
            using var plain = new ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Assert.ThrowsExactlyAsync<WebSocketException>(() =>
                plain.ConnectAsync(new UriBuilder(server.Uri) { Scheme = "ws" }.Uri, timeout.Token));

            // Trust the generated root only inside this test handler; hostname verification still runs.
            using var handler = new SocketsHttpHandler();
            handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust };
            handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(certificate);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            var health = new UriBuilder(server.Uri) { Scheme = "https", Path = "/health" }.Uri;
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync(health, timeout.Token)).StatusCode);
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task StaleReportsAreDroppedAndRoomLimitIsEnforced()
    {
        await using var server = await LocalServer.StartAsync("--Sync:MaxRooms=1");
        using var leader = await Peer.OpenAsync(server.Uri, "create");
        await leader.NextAsync("welcome");
        using var other = await Peer.OpenAsync(server.Uri, "create");
        Assert.AreEqual("server-full", (await other.NextAsync("error")).Error);
        Assert.IsNull(await leader.ActionAsync(new(ActionKind.Select, 0, Track)));
        await leader.SendAsync(new(Protocol.Version, "report", Report: new(0, ReportKind.Failed, Detail: "stale")));
        await leader.SendAsync(new(Protocol.Version, "report", Report: new(1, ReportKind.Applying)));
        var snapshot = await leader.NextAsync("snapshot", message => message.Room!.Leader.Report?.Kind == ReportKind.Applying);
        Assert.AreEqual(1L, snapshot.Room!.Leader.Report!.Revision);
        Assert.AreEqual(ReportKind.Applying, snapshot.Room.Leader.Report.Kind);
    }

    [TestMethod]
    public async Task RealClientReconnectsAndGetsLatestSnapshotAfterNetworkDrop()
    {
        await using var server = await LocalServer.StartAsync();
        using var leader = await Peer.OpenAsync(server.Uri, "create");
        string code = (await leader.NextAsync("welcome")).Room!.State.RoomId;
        await using var bridge = new DropBridge(server.Uri.Port);
        await using var listener = new RoomConnection(bridge.Uri, code);
        await Check.EventuallyAsync(() => listener.View.Status == ConnectionStatus.Connected);
        Assert.IsNotNull(listener.Clock.RttMs);
        Guid? participantId = listener.View.ParticipantId;
        Assert.IsNotNull(participantId);
        bridge.Drop();
        await Check.EventuallyAsync(() => listener.View.Status == ConnectionStatus.Reconnecting);
        Assert.IsNull(await leader.ActionAsync(new(ActionKind.Select, 0, Track)));
        Assert.IsNull(await leader.ActionAsync(new(ActionKind.Seek, 1, PositionMs: 48_000)));
        await Check.EventuallyAsync(() => listener.View is { Generation: 2, Room.State.Revision: 2 });
        Assert.AreEqual(48_000L, listener.View.Room!.State.PositionMs);
        Assert.AreEqual(Track, listener.View.Room.State.Track);
        Assert.AreEqual(participantId, listener.View.ParticipantId);
    }
}

internal sealed class LocalServer(WebApplication app, Uri uri) : IAsyncDisposable
{
    public Uri Uri { get; } = uri;

    public static async Task<LocalServer> StartAsync(params string[] args)
    {
        var app = ServerHost.Build(["--Sync:Port=0", "--Logging:LogLevel:Default=Warning", .. args]);
        await app.StartAsync();
        var address = new Uri(app.Urls.Single());
        return new(app, new UriBuilder(address.Scheme == "https" ? "wss" : "ws", "127.0.0.1", address.Port, "/ws").Uri);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await app.StopAsync(timeout.Token);
        await app.DisposeAsync();
    }
}

internal sealed class Peer : IDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim sendGate = new(1);
    private readonly Channel<ServerMessage> messages = Channel.CreateUnbounded<ServerMessage>();
    private readonly Dictionary<string, Queue<ServerMessage>> deferred = new();
    private Peer(string token) => Token = token;
    public string Token { get; }

    public static async Task<Peer> OpenAsync(Uri uri, string kind, string? room = null, string? token = null,
        int version = Protocol.Version, bool roleSwap = false, string? forwardedFor = null)
    {
        var peer = new Peer(token ?? Protocol.NewToken());
        if (roleSwap)
            peer.socket.Options.AddSubProtocol(Protocol.RoleSwapSubProtocol);
        if (forwardedFor is not null)
            peer.socket.Options.SetRequestHeader("X-Forwarded-For", forwardedFor);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await peer.socket.ConnectAsync(uri, timeout.Token);
        await peer.SendAsync(new(version, kind, Token: peer.Token, RoomId: room));
        _ = peer.ReadAsync();
        _ = peer.HeartbeatAsync();
        return peer;
    }

    public async Task<string?> ActionAsync(RoomAction action, Guid? id = null)
    {
        id ??= Guid.NewGuid();
        await SendAsync(new(Protocol.Version, "action", Id: id, Action: action));
        return (await NextAsync("result", message => message.Id == id)).Error;
    }

    public async Task<string?> SwapRolesAsync(Guid swapId)
    {
        var id = Guid.NewGuid();
        await SendAsync(new(Protocol.Version, "swap-roles", Id: id, SwapId: swapId));
        return (await NextAsync("result", message => message.Id == id)).Error;
    }

    public async ValueTask SendAsync(ClientMessage message)
    {
        await sendGate.WaitAsync(stop.Token);
        try { await Wire.SendAsync(socket, message, stop.Token); }
        finally { sendGate.Release(); }
    }

    public async Task LeaveAsync()
    {
        var id = Guid.NewGuid();
        await SendAsync(new(Protocol.Version, "leave", Id: id));
        Assert.IsNull((await NextAsync("result", m => m.Id == id)).Error);
    }

    public async Task<RoomView> SnapshotAsync(Func<RoomView, bool> filter, int seconds = 5) =>
        (await NextAsync("snapshot", message => filter(message.Room!), seconds)).Room!;

    public async Task<ServerMessage> NextAsync(string? type, Func<ServerMessage, bool>? filter = null, int seconds = 5)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        if (type is not null && deferred.TryGetValue(type, out var queued))
            while (queued.TryDequeue(out var pending))
                if (filter?.Invoke(pending) ?? true)
                    return pending;
        while (await messages.Reader.WaitToReadAsync(timeout.Token))
            while (messages.Reader.TryRead(out var message))
            {
                if ((type is null || message.Type == type) && (filter?.Invoke(message) ?? true))
                    return message;
                if (type is not null && message.Type != type && message.Type != "pong")
                {
                    if (!deferred.TryGetValue(message.Type, out var backlog))
                        deferred[message.Type] = backlog = new();
                    backlog.Enqueue(message);
                }
            }
        throw new InvalidOperationException($"Socket closed before {type}.");
    }

    public void Abort() => socket.Abort();

    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(Protocol.Heartbeat);
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
                await SendAsync(new(Protocol.Version, "ping", Ping: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await Wire.ReceiveAsync<ServerMessage>(socket, Protocol.ReceiveTimeout, stop.Token) is { } message)
                await messages.Writer.WriteAsync(message, stop.Token);
            messages.Writer.TryComplete();
        }
        catch (Exception ex) { messages.Writer.TryComplete(ex); }
    }

    public void Dispose()
    {
        stop.Cancel();
        socket.Dispose();
    }
}

internal sealed class DropBridge : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private volatile TcpClient? incoming;

    public DropBridge(int targetPort)
    {
        listener.Start();
        Uri = new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/ws");
        loop = ForwardAsync(targetPort);
    }

    public Uri Uri { get; }
    public void Drop() => incoming!.Dispose();

    private async Task ForwardAsync(int port)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var source = await listener.AcceptTcpClientAsync(stop.Token);
                using var target = new TcpClient();
                incoming = source;
                await target.ConnectAsync(IPAddress.Loopback, port, stop.Token);
                var from = source.GetStream();
                var to = target.GetStream();
                await Task.WhenAny(from.CopyToAsync(to, stop.Token), to.CopyToAsync(from, stop.Token));
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        await loop;
        stop.Dispose();
    }
}

internal static class Check
{
    public static async Task EventuallyAsync(Func<bool> predicate, int seconds = 7)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            while (!predicate())
                await Task.Delay(30, timeout.Token);
        }
        catch (OperationCanceledException) { Assert.Fail("The expected state was not reached."); }
    }
}
