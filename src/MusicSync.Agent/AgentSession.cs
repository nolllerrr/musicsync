using System.Text.Json.Serialization;
using MusicSync.Core;
using MusicSync.Yandex;

namespace MusicSync.Agent;

internal sealed record AgentSwap(Guid Id, Role? RequestedBy, long? RemainingMs);

internal sealed record AgentTransfer(Guid Id, Guid InitiatorParticipantId, Guid TargetParticipantId, long RemainingMs);

internal sealed record AgentState(string Status, Role? Role = null, string? Code = null,
    bool? PeerConnected = null, string? Error = null, AgentSwap? Swap = null, string? PlaybackError = null)
{
    public string Type => "state";
    public IReadOnlyList<Participant> Participants { get; init; } = Array.Empty<Participant>();
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Guid? SelfParticipantId { get; init; }
    public int Capacity { get; init; }
    public bool SupportsLeadershipTransfer { get; init; }
    public AgentTransfer? Transfer { get; init; }

    internal static AgentState Project(ConnectionView view, long serverNow, PlayerView? player)
    {
        if (view.Status == ConnectionStatus.Ended)
            return new("ended", Error: view.Error);

        var swap = view.Room?.Swap;
        var activeSwap = view.Status == ConnectionStatus.Connected && swap is not null
            ? new AgentSwap(swap.Id, swap.RequestedBy,
                swap.ExpiresAt is long deadline ? Math.Clamp(deadline - serverNow, 0, 30_000) : null)
            : null;
        string status = view.Status switch
        {
            ConnectionStatus.Connected => "connected",
            ConnectionStatus.Reconnecting => "reconnecting",
            _ => "connecting"
        };
        string? playbackError = view.Status == ConnectionStatus.Connected &&
            player is { Kind: ReportKind.Failed } failed ? failed.Detail ?? "player-error" : null;
        return new(status, view.Role, view.Room?.State.RoomId, view.PeerConnected, view.Error, activeSwap, playbackError)
        {
            Participants = view.Participants,
            SelfParticipantId = view.SelfParticipantId,
            Capacity = view.Capacity,
            SupportsLeadershipTransfer = view.SupportsLeadershipTransfer,
            Transfer = view.Status == ConnectionStatus.Connected && view.Room?.Transfer is { } transfer
                ? new(transfer.Id, transfer.InitiatorParticipantId, transfer.TargetParticipantId,
                    Math.Clamp(transfer.ExpiresAt - serverNow, 0, LeadershipTransfer.WindowMs)) : null
        };
    }
}

internal sealed class AgentSession(AgentPipe pipe, Uri endpoint, Action<string> log)
{
    private const long CorrectionThresholdMs = 1200;
    private volatile RoomConnection? connection;
    private volatile RoomPlayer? player;

    public async Task RunAsync()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(pipe.Disconnected);
        _ = Task.Run(pipe.ReadAsync);
        var publish = PublishAsync(lifetime.Token);
        AgentInput? exitCommand = null;
        try
        {
            await foreach (var command in pipe.Commands.ReadAllAsync(lifetime.Token))
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (command.Command is "shutdown" or "leave")
                {
                    exitCommand = command;
                    break;
                }

                var error = command.Command switch
                {
                    "create" => await ConnectAsync(null),
                    "join" => await JoinAsync(command.Code),
                    "swapRoles" => await SwapAsync(command.SwapId),
                    "transferLeadership" => await LeadershipAsync(command.Command, command.TargetParticipantId),
                    "acceptLeadership" or "cancelLeadership" => await LeadershipAsync(command.Command, command.TransferId),
                    "retryPlayback" => RetryPlayback(),
                    _ => "invalid-command"
                };
                pipe.Send(new AgentResult(command.Id, error));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync();
            await publish;
            await LeaveAsync();
        }

        if (exitCommand is not null)
        {
            if (exitCommand.Command == "leave")
                pipe.Send(new AgentState("idle"));
            pipe.Send(new AgentResult(exitCommand.Id));
        }
    }

    private async Task<string?> JoinAsync(string? code)
    {
        string? invitation = code?.Trim().ToUpperInvariant();
        return Protocol.IsCode(invitation) ? await ConnectAsync(invitation) : "invalid-code";
    }

    private async Task<string?> ConnectAsync(string? invitation)
    {
        if (connection?.View.Status is ConnectionStatus.Connecting or ConnectionStatus.Connected or ConnectionStatus.Reconnecting)
            return "already-in-room";
        await LeaveAsync();
        var room = new RoomConnection(endpoint, invitation, log);
        connection = room;
        var adapter = new SonataPlayer(pipe, () => room.Clock.Now);
        player = new RoomPlayer(adapter, () => connection, CorrectionThresholdMs, log);
        return null;
    }

    private async Task<string?> SwapAsync(Guid? requestedId)
    {
        var room = connection;
        var view = room?.View;
        if (room is null || view?.Status != ConnectionStatus.Connected)
            return "offline";
        if (view.Room?.Swap is not { } swap)
            return "role-swap-unavailable";
        if (swap.RequestedBy == view.Role || swap.ExpiresAt <= room.Clock.Now ||
            (swap.RequestedBy is not null || requestedId.HasValue) && requestedId != swap.Id)
            return "role-swap-stale";
        return await room.SwapRolesAsync(swap.Id);
    }

    private Task<string?> LeadershipAsync(string command, Guid? id)
    {
        var room = connection;
        if (room is null || room.View.Status != ConnectionStatus.Connected)
            return Task.FromResult<string?>("offline");
        if (id is null || id == Guid.Empty)
            return Task.FromResult<string?>("invalid-command");
        return command switch
        {
            "transferLeadership" => room.TransferLeadershipAsync(id.Value),
            "acceptLeadership" => room.AcceptLeadershipAsync(id.Value),
            _ => room.CancelLeadershipAsync(id.Value)
        };
    }

    private string? RetryPlayback()
    {
        if (connection?.View.Status != ConnectionStatus.Connected || player is not { } activePlayer)
            return "offline";
        activePlayer.Retry();
        return null;
    }

    private async Task LeaveAsync()
    {
        var room = connection;
        var activePlayer = player;
        connection = null;
        player = null;
        if (room is not null)
        {
            var stopPlayer = activePlayer is null ? Task.CompletedTask : activePlayer.DisposeAsync().AsTask();
            await Task.WhenAll(stopPlayer, room.LeaveAsync());
        }
    }

    private async Task PublishAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        AgentState? previous = null;
        try
        {
            do
            {
                var state = GetState();
                if (state is not null && state != previous)
                {
                    pipe.Send(state);
                    previous = state;
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private AgentState? GetState()
    {
        var room = connection;
        if (room is null)
            return null;
        var view = room.View;
        return AgentState.Project(view, room.Clock.Now, player?.View);
    }
}
