using System.Net;
using System.Threading.Channels;
using MusicSync.Core;

namespace MusicSync.Server;

internal sealed class RoomPeer(bool supportsRoleSwap) : IDisposable
{
    private readonly CancellationTokenSource disconnect = new();
    private readonly Channel<ServerMessage> outgoing = Channel.CreateBounded<ServerMessage>(
        new BoundedChannelOptions(48) { SingleReader = true, AllowSynchronousContinuations = false });

    public ChannelReader<ServerMessage> Outgoing => outgoing.Reader;
    public CancellationToken Disconnect => disconnect.Token;
    public bool SupportsRoleSwap { get; } = supportsRoleSwap;
    public string? RoomId { get; set; }
    public Role Role { get; set; }
    public void Send(ServerMessage message)
    {
        if (!outgoing.Writer.TryWrite(message))
            Stop();
    }
    public void Stop() => disconnect.Cancel();
    public bool IsCompleting { get; private set; }
    public void Complete()
    {
        IsCompleting = true;
        outgoing.Writer.TryComplete();
    }
    public void Dispose() => disconnect.Dispose();
}

internal sealed class RoomRegistry(MonotonicClock clock, int maxRooms, int maxRoomsPerSource,
    int maxRoomCreationsPerSource, TimeSpan creationWindow, TimeSpan grace)
{
    private sealed class Seat(string token)
    {
        public string Token { get; } = token;
        public RoomPeer? Peer { get; set; }
        public PlayerReport? Report { get; set; }
        public long Expires { get; set; }
        public Participant Status => new(Peer is not null, Report);
    }

    private sealed class Room(string id, Seat leader, IPAddress creator, long now)
    {
        public IPAddress Creator { get; } = creator;
        public RoomState State { get; set; } = new(id, 0, 0, null, Playback.Paused, 0, now, false);
        public Seat Leader { get; set; } = leader;
        public Seat? Listener { get; set; }
        public RoleSwap? Swap { get; set; }
        public Queue<Guid> Accepted { get; } = new();
        public RoomView View => new(State, Leader.Status, Listener?.Status ?? new(false, null), Swap);
        public Seat? GetSeat(Role role) => role == Role.Leader ? Leader : Listener;

        public void ResetRoleSwap() =>
            Swap = Leader.Peer is { SupportsRoleSwap: true } && Listener?.Peer is { SupportsRoleSwap: true }
                ? new RoleSwap(Guid.NewGuid()) : null;

        public void Broadcast()
        {
            var message = new ServerMessage(Protocol.Version, "snapshot", Room: View);
            Leader.Peer?.Send(message with { Role = Role.Leader });
            Listener?.Peer?.Send(message with { Role = Role.Listener });
        }
    }

    private const int RoleSwapWindowMs = 30_000;
    private readonly object gate = new();
    private readonly Dictionary<string, Room> rooms = new();
    private readonly Dictionary<string, (Room Room, Role Role)> tokens = new();
    private readonly Queue<(IPAddress Source, long CreatedAt)> recentCreations = new();
    private readonly Dictionary<IPAddress, int> creationCounts = new();

    public ServerMessage? Enter(RoomPeer peer, ClientMessage hello, IPAddress source)
    {
        if (hello.Version != Protocol.Version)
            return Error("protocol-version");
        if (!Protocol.IsToken(hello.Token) || hello.Id is not null || hello.Action is not null ||
            hello.Report is not null || hello.Ping is not null || hello.SwapId is not null ||
            (hello.Type == "create" ? hello.RoomId is not null :
                hello.Type is not ("join" or "resume") || !Protocol.IsCode(hello.RoomId)))
            return Error("invalid-hello");

        lock (gate)
        {
            PruneLocked();
            Room room;
            Role role;
            if (tokens.TryGetValue(hello.Token!, out var existing))
            {
                (room, role) = existing;
                if (hello.Type == "create" ? role != Role.Leader :
                    room.State.RoomId != hello.RoomId || (hello.Type == "join" && role != Role.Listener))
                    return Error("invalid-session");
            }
            else if (hello.Type == "create")
            {
                if (rooms.Count >= maxRooms)
                    return Error("server-full");
                int creations = creationCounts.GetValueOrDefault(source);
                if (creations >= maxRoomCreationsPerSource ||
                    rooms.Values.Count(room => room.Creator.Equals(source)) >= maxRoomsPerSource)
                    return Error("room-creation-limited");
                string code;
                do { code = Protocol.NewCode(); } while (rooms.ContainsKey(code));
                role = Role.Leader;
                long now = clock.Now;
                room = new Room(code, new Seat(hello.Token!), source, now);
                rooms.Add(code, room);
                tokens.Add(hello.Token!, (room, role));
                recentCreations.Enqueue((source, now));
                creationCounts[source] = creations + 1;
            }
            else
            {
                if (hello.Type == "resume" || !rooms.TryGetValue(hello.RoomId!, out var joined))
                    return Error("room-unavailable");
                room = joined;
                if (room.Listener is not null)
                    return Error("room-full");
                role = Role.Listener;
                room.Listener = new Seat(hello.Token!);
                tokens.Add(hello.Token!, (room, role));
            }

            var seat = room.GetSeat(role)!;
            seat.Peer?.Stop();
            seat.Peer = peer;
            seat.Report = null;
            peer.RoomId = room.State.RoomId;
            peer.Role = role;
            room.ResetRoleSwap();
            peer.Send(new(Protocol.Version, "welcome", role, room.View));
            room.Broadcast();
            return null;
        }
    }

    public bool Handle(RoomPeer peer, ClientMessage message, long received)
    {
        if (message.Version != Protocol.Version || message.Token is not null || message.RoomId is not null ||
            message.SwapId is not null && message.Type != "swap-roles")
            return false;
        lock (gate)
        {
            if (!rooms.TryGetValue(peer.RoomId!, out var room) || room.GetSeat(peer.Role) is not { } seat ||
                seat.Peer != peer)
                return false;
            switch (message)
            {
                case { Type: "ping", Ping: > 0, Id: null, Action: null, Report: null }:
                    peer.Send(new(Protocol.Version, "pong", Ping: message.Ping, Received: received));
                    return true;
                case { Type: "action", Id: { } id, Action: { IsValid: true } action, Report: null, Ping: null }
                    when id != Guid.Empty:
                    Apply(room, peer, id, action);
                    return true;
                case { Type: "swap-roles", Id: { } requestId, SwapId: { } swapId, Action: null, Report: null, Ping: null }
                    when peer.SupportsRoleSwap && requestId != Guid.Empty && swapId != Guid.Empty:
                    string? error = SwapRoles(room, peer, swapId);
                    peer.Send(new(Protocol.Version, "result", Id: requestId, Error: error));
                    return true;
                case { Type: "report", Report: { IsValid: true } report, Id: null, Action: null, Ping: null }:
                    if (room.State.Revision == report.Revision &&
                        (report.SampleTime is null || report.SampleTime >= received - 60_000 &&
                            report.SampleTime <= received + 60_000))
                    {
                        seat.Report = report;
                        room.Broadcast();
                    }
                    return true;
                case { Type: "leave", Id: not null, Action: null, Report: null, Ping: null }:
                    peer.Send(new(Protocol.Version, "result", Id: message.Id));
                    if (peer.Role == Role.Leader)
                        Remove(room, "leader-left");
                    else
                    {
                        tokens.Remove(seat.Token);
                        room.Listener = null;
                        room.ResetRoleSwap();
                        room.Broadcast();
                    }
                    peer.Complete();
                    return false;
                default:
                    return false;
            }
        }
    }

    private string? SwapRoles(Room room, RoomPeer peer, Guid swapId)
    {
        if (room.Leader.Peer is not { Disconnect.IsCancellationRequested: false } leader ||
            room.Listener?.Peer is not { Disconnect.IsCancellationRequested: false } listener)
            return "role-swap-unavailable";

        long now = clock.Now;
        if (room.Swap?.ExpiresAt <= now)
        {
            room.ResetRoleSwap();
            room.Broadcast();
        }
        if (room.Swap is not { } swap || swap.Id != swapId)
            return "role-swap-stale";
        if (swap.RequestedBy == peer.Role)
            return null;
        if (swap.RequestedBy is null)
        {
            room.Swap = swap with { RequestedBy = peer.Role, ExpiresAt = now + RoleSwapWindowMs };
        }
        else
        {
            (room.Leader, room.Listener) = (room.Listener, room.Leader);
            leader.Role = Role.Listener;
            listener.Role = Role.Leader;
            tokens[room.Leader.Token] = (room, Role.Leader);
            tokens[room.Listener.Token] = (room, Role.Listener);
            // Invalidate in-flight playback commands and reports from the previous roles.
            room.State = room.State with { Revision = room.State.Revision + 1 };
            room.Leader.Report = room.Listener.Report = null;
            room.ResetRoleSwap();
        }
        room.Broadcast();
        return null;
    }

    private void Apply(Room room, RoomPeer peer, Guid id, RoomAction action)
    {
        var state = room.State;
        bool duplicate = room.Accepted.Contains(id);
        string? error = peer.Role != Role.Leader ? "leader-only" :
            duplicate ? null :
            action.BaseRevision != state.Revision ? "stale-revision" :
            state.Track is null && action.Kind is ActionKind.Play or ActionKind.Pause or ActionKind.Seek
                ? "no-track" : null;
        if (error is null && !duplicate)
        {
            long now = clock.Now;
            long revision = state.Revision + 1;
            long position = state.PositionAt(now);
            room.State = action.Kind switch
            {
                ActionKind.Select => new(state.RoomId, revision, revision, action.Track,
                    Playback.Playing, 0, now, false),
                ActionKind.Adopt => new(state.RoomId, revision, revision, action.Track,
                    action.Playback!.Value, action.PositionMs!.Value, now, false),
                ActionKind.Unknown => new(state.RoomId, revision, revision, null, Playback.Paused, 0, now, true),
                ActionKind.Seek => state with { Revision = revision, PositionMs = action.PositionMs!.Value, ServerTimestamp = now },
                _ => state with { Revision = revision, PositionMs = position, ServerTimestamp = now,
                    Playback = action.Kind == ActionKind.Play ? Playback.Playing : Playback.Paused }
            };
            room.Accepted.Enqueue(id);
            if (room.Accepted.Count > 128)
                room.Accepted.Dequeue();
            room.Leader.Report = null;
            if (room.Listener is not null)
                room.Listener.Report = null;
            room.Broadcast();
        }
        peer.Send(new(Protocol.Version, "result", Id: id, Error: error));
    }

    public void Detach(RoomPeer peer)
    {
        lock (gate)
        {
            if (peer.RoomId is null || !rooms.TryGetValue(peer.RoomId, out var room) ||
                room.GetSeat(peer.Role) is not { } seat || seat.Peer != peer)
                return;
            seat.Peer = null;
            seat.Report = null;
            seat.Expires = clock.Now + (long)grace.TotalMilliseconds;
            room.ResetRoleSwap();
            room.Broadcast();
        }
    }

    public void Prune()
    {
        lock (gate)
            PruneLocked();
    }

    private void PruneLocked()
    {
        long now = clock.Now;
        while (recentCreations.TryPeek(out var creation) && now - creation.CreatedAt >= creationWindow.TotalMilliseconds)
        {
            recentCreations.Dequeue();
            if (--creationCounts[creation.Source] == 0)
                creationCounts.Remove(creation.Source);
        }
        foreach (var room in rooms.Values.ToArray())
        {
            if (room.Leader.Peer is null && room.Leader.Expires <= now)
                Remove(room, "leader-timeout");
            else if (room.Listener is { Peer: null } listener && listener.Expires <= now)
            {
                tokens.Remove(listener.Token);
                room.Listener = null;
                room.Broadcast();
            }
            else if (room.Swap?.ExpiresAt <= now)
            {
                room.ResetRoleSwap();
                room.Broadcast();
            }
        }
    }

    private void Remove(Room room, string reason)
    {
        rooms.Remove(room.State.RoomId);
        tokens.Remove(room.Leader.Token);
        if (room.Listener is { } listener)
        {
            tokens.Remove(listener.Token);
            listener.Peer?.Send(new(Protocol.Version, "ended", Error: reason));
            listener.Peer?.Complete();
        }
    }

    private static ServerMessage Error(string error) => new(Protocol.Version, "error", Error: error);
}
