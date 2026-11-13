using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace MusicSync.Core;

public enum ConnectionStatus { Connecting, Connected, Reconnecting, Ended }

public sealed record ConnectionView(ConnectionStatus Status, Role? Role = null, RoomView? Room = null,
    long Generation = 0, string? Error = null, Guid? ParticipantId = null, bool SupportsLeadershipTransfer = false)
{
    public IReadOnlyList<Participant> Participants { get; init; } = Array.Empty<Participant>();
    public Guid? SelfParticipantId { get; init; }
    public int Capacity { get; init; }
    public PlayerReport? LeaderReport => RoomMembership.LeaderReport(this);
    // Summary compatibility; the roster uses Participants.
    public bool? PeerConnected => RoomMembership.PeerConnected(this);
}

public sealed class RoomConnection : IRoomConnection, IAsyncDisposable
{
    private static readonly HttpMessageInvoker Ipv4WebSocketInvoker = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.Zero,
        UseCookies = false,
        ConnectCallback = ConnectTcpAsync
    });

    private sealed class Transport : IDisposable
    {
        public ClientWebSocket Socket { get; } = new();
        public SemaphoreSlim SendGate { get; } = new(1);
        public (Guid Id, TaskCompletionSource<ServerMessage> Result)? Pending { get; set; }
        public void Dispose() => Socket.Dispose();
    }

    private readonly Uri endpoint;
    private readonly string? invitation;
    private readonly string token = Protocol.NewToken();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim actionGate = new(1);
    private readonly object gate = new();
    private readonly Action<string>? log;
    private readonly Task loop;
    private string? joined;
    private Transport? active;
    private volatile ConnectionView view = new(ConnectionStatus.Connecting);
    private int disposed;
    public SyncClock Clock { get; } = new(new MonotonicClock());
    public ConnectionView View => view;
    public event Action? Changed;

    private void SetView(ConnectionView next)
    {
        view = next;
        Changed?.Invoke();
    }

    public RoomConnection(Uri endpoint, string? invitation, Action<string>? log = null)
    {
        this.endpoint = endpoint;
        this.invitation = invitation;
        this.log = log;
        loop = Task.Run(RunAsync);
    }

    public async Task<string?> ActionAsync(ActionKind kind, TrackId? track = null, long? position = null,
        Playback? playback = null, long? expectedRevision = null)
    {
        await actionGate.WaitAsync();
        try
        {
            var current = View;
            if (current.Status != ConnectionStatus.Connected || current.Room is null)
                return "offline";
            if (current.Role != Role.Leader)
                return "leader-only";
            long revision = current.Room.State.Revision;
            if (expectedRevision is not null && expectedRevision != revision)
                return "stale-revision";
            var action = new RoomAction(kind, revision, track, position, playback);
            if (!action.IsValid)
                return "invalid-command";
            return await RequestAsync("action", current.Generation, action);
        }
        finally { actionGate.Release(); }
    }

    public async Task ReportAsync(PlayerReport report, long generation)
    {
        if (!report.IsValid)
            return;
        Transport? transport;
        lock (gate)
            transport = View.Generation == generation ? active : null;
        if (transport is null)
            return;
        try { await SendAsync(transport, new(Protocol.Version, "report", Report: report), stop.Token); }
        catch (Exception ex) when (IsNetworkError(ex)) { }
    }

    public async Task<string?> SwapRolesAsync(Guid swapId)
    {
        await actionGate.WaitAsync();
        try
        {
            var current = View;
            if (current.Status != ConnectionStatus.Connected)
                return "offline";
            if (current.Room?.Swap is not { } swap || swap.Id != swapId)
                return "role-swap-stale";
            return await RequestAsync("swap-roles", current.Generation, swapId: swapId);
        }
        finally { actionGate.Release(); }
    }

    public async Task LeaveAsync()
    {
        await actionGate.WaitAsync();
        try
        {
            var current = View;
            if (current.Status == ConnectionStatus.Connected)
                await RequestAsync("leave", current.Generation);
            await DisposeAsync();
        }
        finally { actionGate.Release(); }
    }

    public Task<string?> TransferLeadershipAsync(Guid targetParticipantId) =>
        LeadershipRequestAsync("transfer-leadership", targetParticipantId);

    public Task<string?> AcceptLeadershipAsync(Guid transferId) =>
        LeadershipRequestAsync("accept-leadership", transferId);

    // Both the initiator (cancel) and the selected listener (decline) use this command.
    public Task<string?> CancelLeadershipAsync(Guid transferId) =>
        LeadershipRequestAsync("cancel-leadership", transferId);

    private async Task<string?> LeadershipRequestAsync(string type, Guid value)
    {
        await actionGate.WaitAsync();
        try
        {
            var current = View;
            if (current.Status != ConnectionStatus.Connected || current.Room is null)
                return "offline";
            if (!current.SupportsLeadershipTransfer)
                return "leadership-transfer-unavailable";
            if (value == Guid.Empty)
                return "invalid-command";
            if (type == "transfer-leadership")
            {
                if (current.Role != Role.Leader)
                    return "leader-only";
                return await RequestAsync(type, current.Generation, targetParticipantId: value);
            }
            if (current.Room.Transfer is not { } transfer || transfer.Id != value)
                return "leadership-transfer-stale";
            if (transfer.ExpiresAt <= Clock.Now)
                return "leadership-transfer-expired";
            return await RequestAsync(type, current.Generation, transferId: value);
        }
        finally { actionGate.Release(); }
    }

    private async Task<string?> RequestAsync(string type, long generation, RoomAction? action = null, Guid? swapId = null,
        Guid? targetParticipantId = null, Guid? transferId = null)
    {
        var id = Guid.NewGuid();
        var response = new TaskCompletionSource<ServerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Transport? transport;
        lock (gate)
        {
            transport = View.Generation == generation ? active : null;
            if (transport is null)
                return "offline";
            transport.Pending = (id, response);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await SendAsync(transport, new(Protocol.Version, type, Id: id, Action: action, SwapId: swapId,
                TargetParticipantId: targetParticipantId, TransferId: transferId), timeout.Token);
            return (await response.Task.WaitAsync(timeout.Token)).Error;
        }
        catch (Exception ex) when (IsNetworkError(ex)) { return "delivery-unknown"; }
        finally
        {
            lock (gate)
                transport.Pending = null;
        }
    }

    private async Task RunAsync()
    {
        int delay = 1;
        var cancellation = stop.Token;
        while (!cancellation.IsCancellationRequested)
        {
            using var transport = new Transport();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var socket = transport.Socket;
            socket.Options.AddSubProtocol(Protocol.LeadershipTransferSubProtocol);
            socket.Options.AddSubProtocol(Protocol.RoleSwapSubProtocol);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            if (endpoint.Scheme == "ws")
                socket.Options.Proxy = null;
            Task? heartbeat = null;
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var invoker = endpoint.Scheme == "wss" && endpoint.HostNameType == UriHostNameType.IPv4
                    ? Ipv4WebSocketInvoker : null;
                await socket.ConnectAsync(endpoint, invoker, connectTimeout.Token);
                await SendAsync(transport, new(Protocol.Version,
                    joined is not null ? "resume" : invitation is not null ? "join" : "create",
                    Token: token, RoomId: joined ?? invitation), connectTimeout.Token);
                // One first ping gives the player a time estimate before applying the snapshot.
                long ping = Clock.LocalNow;
                await SendAsync(transport, new(Protocol.Version, "ping", Ping: ping), connectTimeout.Token);
                var welcome = await ReceiveAsync(socket, connectTimeout.Token);
                if (welcome is { Type: "error" })
                {
                    End(welcome.Error ?? "protocol-error");
                    return;
                }
                if (welcome is not { Type: "welcome", Room: not null, Role: not null, ParticipantId: not null } ||
                    !welcome.Room.Participants.Any(p => p.ParticipantId == welcome.ParticipantId && p.Role == welcome.Role))
                    throw new InvalidDataException("Expected a room snapshot.");
                joined = welcome.Room.State.RoomId;
                var initialView = RoomMembership.Update(
                    new(ConnectionStatus.Connected, welcome.Role, welcome.Room, View.Generation + 1), welcome);
                while (true)
                {
                    var first = await ReceiveAsync(socket, connectTimeout.Token);
                    if (first is { Type: "ended" })
                    {
                        End(first.Error ?? "room-ended");
                        return;
                    }
                    if (first is { Type: "snapshot", Room: not null })
                    {
                        initialView = RoomMembership.Update(initialView with
                        {
                            Room = first.Room,
                            Role = first.Role ?? initialView.Role
                        }, first);
                    }
                    else if (first is { Type: "pong", Ping: not null, Received: not null, Sent: not null } && first.Ping == ping)
                    {
                        ObservePong(first);
                        break;
                    }
                    else
                        throw new InvalidDataException("Expected a clock sample.");
                }

                lock (gate)
                {
                    active = transport;
                    SetView(initialView with
                    {
                        SupportsLeadershipTransfer =
                        socket.SubProtocol == Protocol.LeadershipTransferSubProtocol
                    });
                }
                delay = 1;
                log?.Invoke($"Connected ({initialView.Role}).");
                heartbeat = HeartbeatAsync(transport, attempt);
                while (!attempt.IsCancellationRequested)
                {
                    var message = await ReceiveAsync(socket, attempt.Token);
                    switch (message)
                    {
                        case { Type: "snapshot", Room: not null }:
                            var current = View;
                            if (message.Room.State.RoomId == joined &&
                                message.Room.State.Revision >= current.Room!.State.Revision)
                                SetView(RoomMembership.Update(current with
                                {
                                    Role = message.Role ?? current.Role,
                                    Room = message.Room
                                }, message));
                            break;
                        case { Type: "pong", Ping: not null, Received: not null, Sent: not null }:
                            ObservePong(message);
                            break;
                        case { Type: "result", Id: not null }:
                            lock (gate)
                            {
                                if (transport.Pending is { } pending && pending.Id == message.Id)
                                    pending.Result.TrySetResult(message);
                            }
                            break;
                        case { Type: "ended" or "error" }:
                            End(message.Error ?? "room-ended");
                            return;
                        default:
                            throw new InvalidDataException("Unexpected server message.");
                    }
                }
                attempt.Token.ThrowIfCancellationRequested();
            }
            catch (ProtocolVersionException)
            {
                End("protocol-version");
                return;
            }
            catch (Exception ex) when (IsNetworkError(ex))
            {
                if (!cancellation.IsCancellationRequested)
                {
                    SetView(View with { Status = ConnectionStatus.Reconnecting, Error = "connection-lost" });
                    log?.Invoke($"Connection interrupted ({ex.GetType().Name}).");
                }
            }
            finally
            {
                lock (gate)
                {
                    active = null;
                    transport.Pending?.Result.TrySetCanceled();
                }
                await attempt.CancelAsync();
                if (heartbeat is not null)
                    await heartbeat;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(delay), cancellation); }
            catch (OperationCanceledException) { return; }
            delay = Math.Min(delay * 2, 8);
        }
    }

    private void ObservePong(ServerMessage message) =>
        Clock.Add(message.Ping!.Value, message.Received!.Value, message.Sent!.Value, Clock.LocalNow);

    private static async ValueTask<Stream> ConnectTcpAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var remote = context.DnsEndPoint;
        // Some Windows split tunnels reject dual-mode sockets connecting to an IPv4 address.
        var socket = Uri.CheckHostName(remote.Host) == UriHostNameType.IPv4
            ? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            await socket.ConnectAsync(remote, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void End(string reason)
    {
        SetView(View with { Status = ConnectionStatus.Ended, Error = reason });
        log?.Invoke($"Room ended ({reason}).");
    }

    private static async Task<ServerMessage> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        var message = await Wire.ReceiveAsync<ServerMessage>(socket, Protocol.ReceiveTimeout, token)
            ?? throw new IOException("The server closed the WebSocket.");
        if (message.Version != Protocol.Version)
            throw new ProtocolVersionException();
        return message;
    }

    private async Task HeartbeatAsync(Transport transport, CancellationTokenSource lifetime)
    {
        using var ticks = new PeriodicTimer(Protocol.Heartbeat);
        try
        {
            while (await ticks.WaitForNextTickAsync(lifetime.Token))
                await SendAsync(transport, new(Protocol.Version, "ping", Ping: Clock.LocalNow), lifetime.Token);
        }
        catch (Exception ex) when (IsNetworkError(ex)) { await lifetime.CancelAsync(); }
    }

    private static async Task SendAsync(Transport transport, ClientMessage message, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        await transport.SendGate.WaitAsync(timeout.Token);
        try { await Wire.SendAsync(transport.Socket, message, timeout.Token); }
        finally { transport.SendGate.Release(); }
    }

    private static bool IsNetworkError(Exception ex) => ex is WebSocketException or OperationCanceledException or
        IOException or InvalidDataException or HttpRequestException or JsonException or ObjectDisposedException;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            await stop.CancelAsync();
        await loop;
    }

    private sealed class ProtocolVersionException : Exception;
}
