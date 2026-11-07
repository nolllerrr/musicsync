using System.Net.WebSockets;
using System.Text.Json;

namespace MusicSync.Core;

public enum ConnectionStatus { Connecting, Connected, Reconnecting, Ended }

public sealed record ConnectionView(ConnectionStatus Status, Role? Role = null, RoomView? Room = null,
    long Generation = 0, string? Error = null);

public sealed class RoomConnection : IAsyncDisposable
{
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

    private async Task<string?> RequestAsync(string type, long generation, RoomAction? action = null, Guid? swapId = null)
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
            await SendAsync(transport, new(Protocol.Version, type, Id: id, Action: action, SwapId: swapId), timeout.Token);
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
            socket.Options.AddSubProtocol(Protocol.RoleSwapSubProtocol);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            if (endpoint.Scheme == "ws")
                socket.Options.Proxy = null;
            Task? heartbeat = null;
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(endpoint, connectTimeout.Token);
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
                if (welcome is not { Type: "welcome", Room: not null, Role: not null })
                    throw new InvalidDataException("Expected a room snapshot.");
                joined = welcome.Room.State.RoomId;
                var initialRoom = welcome.Room;
                var role = welcome.Role;
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
                        initialRoom = first.Room;
                        role = first.Role ?? role;
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
                    view = new(ConnectionStatus.Connected, role, initialRoom, View.Generation + 1);
                }
                delay = 1;
                log?.Invoke($"Connected ({role}).");
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
                                view = current with { Role = message.Role ?? current.Role, Room = message.Room };
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
                    view = View with { Status = ConnectionStatus.Reconnecting, Error = "connection-lost" };
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

    private void End(string reason)
    {
        view = View with { Status = ConnectionStatus.Ended, Error = reason };
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
