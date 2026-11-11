using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MusicSync.Core;
using MusicSync.Server;

namespace MusicSync.Tests;

[TestClass]
public sealed class LeadershipTransferClientTests
{
    [TestMethod]
    public void ContractRoundTripsWithoutAddingNullFieldsToLegacyMessages()
    {
        var offer = new LeadershipTransfer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 123_456);
        var state = new RoomState(Protocol.NewCode(), 1, 1, null, Playback.Paused, 0, 100_000, false);
        var room = new RoomView(state, new(true, null), new(true, null), Transfer: offer);
        string json = JsonSerializer.Serialize(room, Protocol.Json);
        Assert.AreEqual(room, JsonSerializer.Deserialize<RoomView>(json, Protocol.Json));
        var command = new ClientMessage(Protocol.Version, "transfer-leadership", Id: Guid.NewGuid(),
            TargetParticipantId: offer.TargetParticipantId);
        Assert.AreEqual(command, JsonSerializer.Deserialize<ClientMessage>(JsonSerializer.Serialize(command, Protocol.Json), Protocol.Json));
        string legacy = JsonSerializer.Serialize(new ClientMessage(Protocol.Version, "ping", Ping: 1), Protocol.Json);
        Assert.DoesNotContain("targetParticipantId", legacy);
        Assert.DoesNotContain("transferId", legacy);
        Assert.DoesNotContain("transfer", JsonSerializer.Serialize(room with { Transfer = null }, Protocol.Json));
    }

    [TestMethod]
    public async Task LegacyServerKeepsRoleSwapAndRejectsNewMethodsWithoutSendingThem()
    {
        await using var server = await LocalServer.StartAsync();
        await using var leader = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => leader.View.Status == ConnectionStatus.Connected);
        await using var listener = new RoomConnection(server.Uri, leader.View.Room!.State.RoomId);
        await Check.EventuallyAsync(() => listener.View.Status == ConnectionStatus.Connected && leader.View.Room?.Swap is not null);
        Assert.IsFalse(leader.View.SupportsLeadershipTransfer);
        Assert.AreEqual("leadership-transfer-unavailable", await leader.TransferLeadershipAsync(Guid.NewGuid()));
        Assert.AreEqual("leadership-transfer-unavailable", await listener.AcceptLeadershipAsync(Guid.NewGuid()));
        Assert.AreEqual("leadership-transfer-unavailable", await leader.CancelLeadershipAsync(Guid.NewGuid()));
        var id = leader.View.Room!.Swap!.Id;
        Assert.IsNull(await leader.SwapRolesAsync(id));
        Assert.IsNull(await listener.SwapRolesAsync(id));
        await Check.EventuallyAsync(() => listener.View.Role == Role.Leader && leader.View.Role == Role.Listener);
    }

    [TestMethod]
    [DataRow("transfer-leadership", Role.Leader)]
    [DataRow("accept-leadership", Role.Listener)]
    [DataRow("cancel-leadership", Role.Leader)]
    [DataRow("cancel-leadership", Role.Listener)]
    public async Task NegotiatedCommandsCarryOnlyTheirOwnIdentifiersAndPropagateServerErrors(string type, Role role)
    {
        var commands = Channel.CreateUnbounded<ClientMessage>();
        var offer = new LeadershipTransfer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), long.MaxValue);
        await using var server = await StartContractEndpointAsync(role, offer, commands.Writer);
        await using var client = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => client.View.Status == ConnectionStatus.Connected);
        Assert.IsTrue(client.View.SupportsLeadershipTransfer);
        string? result = type switch
        {
            "transfer-leadership" => await client.TransferLeadershipAsync(offer.TargetParticipantId),
            "accept-leadership" => await client.AcceptLeadershipAsync(offer.Id),
            _ => await client.CancelLeadershipAsync(offer.Id)
        };
        Assert.AreEqual("test-server-rejection", result);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var sent = await commands.Reader.ReadAsync(timeout.Token);
        Assert.AreEqual(type, sent.Type);
        Assert.IsNotNull(sent.Id);
        Assert.AreNotEqual(Guid.Empty, sent.Id);
        Assert.AreEqual(type == "transfer-leadership" ? offer.TargetParticipantId : (Guid?)null, sent.TargetParticipantId);
        Assert.AreEqual(type == "transfer-leadership" ? null : (Guid?)offer.Id, sent.TransferId);
        Assert.IsNull(sent.Token);
        Assert.IsNull(sent.RoomId);
        Assert.IsNull(sent.SwapId);
        Assert.IsNull(sent.Action);
        Assert.IsNull(sent.Report);
        Assert.IsNull(sent.Ping);
    }

    [TestMethod]
    public async Task ClientRejectsEmptyIdsWrongRoleStaleAndExpiredOffersLocally()
    {
        var commands = Channel.CreateUnbounded<ClientMessage>();
        var expired = new LeadershipTransfer(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        await using var server = await StartContractEndpointAsync(Role.Listener, expired, commands.Writer);
        await using var client = new RoomConnection(server.Uri, null);
        await Check.EventuallyAsync(() => client.View.Status == ConnectionStatus.Connected);
        Assert.AreEqual("invalid-command", await client.TransferLeadershipAsync(Guid.Empty));
        Assert.AreEqual("invalid-command", await client.AcceptLeadershipAsync(Guid.Empty));
        Assert.AreEqual("invalid-command", await client.CancelLeadershipAsync(Guid.Empty));
        Assert.AreEqual("leader-only", await client.TransferLeadershipAsync(Guid.NewGuid()));
        Assert.AreEqual("leadership-transfer-stale", await client.AcceptLeadershipAsync(Guid.NewGuid()));
        Assert.AreEqual("leadership-transfer-stale", await client.CancelLeadershipAsync(Guid.NewGuid()));
        Assert.AreEqual("leadership-transfer-expired", await client.AcceptLeadershipAsync(expired.Id));
        Assert.AreEqual("leadership-transfer-expired", await client.CancelLeadershipAsync(expired.Id));
        Assert.IsFalse(commands.Reader.TryRead(out _));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LegacyServerDoesNotSilentlyIgnoreLeadershipFields(bool inHello)
    {
        await using var server = await LocalServer.StartAsync();
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await socket.ConnectAsync(server.Uri, timeout.Token);
        await Wire.SendAsync(socket, new ClientMessage(Protocol.Version, "create", Token: Protocol.NewToken(),
            TargetParticipantId: inHello ? Guid.NewGuid() : null), timeout.Token);
        var welcome = await Wire.ReceiveAsync<ServerMessage>(socket, TimeSpan.FromSeconds(3), timeout.Token);
        if (inHello)
        {
            Assert.AreEqual("invalid-hello", welcome!.Error);
            return;
        }
        Assert.AreEqual("welcome", welcome!.Type);
        await Wire.ReceiveAsync<ServerMessage>(socket, TimeSpan.FromSeconds(3), timeout.Token); // initial snapshot
        await Wire.SendAsync(socket, new ClientMessage(Protocol.Version, "ping", Ping: 1, TransferId: Guid.NewGuid()), timeout.Token);
        try
        {
            Assert.IsNull(await Wire.ReceiveAsync<ServerMessage>(socket, TimeSpan.FromSeconds(3), timeout.Token));
        }
        catch (WebSocketException) { }
    }

    // A scripted wire-contract peer, not a group-room implementation. Actual group integration awaits PR 1.
    private static async Task<LocalServer> StartContractEndpointAsync(Role role, LeadershipTransfer offer,
        ChannelWriter<ClientMessage> commands)
    {
        var app = ServerHost.Build(["--Sync:Port=0", "--Logging:LogLevel:Default=Warning"]);
        app.Map("/leadership-contract", async (HttpContext context) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(Protocol.LeadershipTransferSubProtocol);
            var token = context.RequestAborted;
            var clock = new MonotonicClock();
            try
            {
                await Wire.ReceiveAsync<ClientMessage>(socket, TimeSpan.FromSeconds(3), token); // hello
                var ping = await Wire.ReceiveAsync<ClientMessage>(socket, TimeSpan.FromSeconds(3), token);
                var room = new RoomView(new(Protocol.NewCode(), 0, 0, null, Playback.Paused, 0, clock.Now, false),
                    new(true, null), new(true, null), Transfer: offer);
                await Wire.SendAsync(socket, new ServerMessage(Protocol.Version, "welcome", role, room), token);
                await Wire.SendAsync(socket, new ServerMessage(Protocol.Version, "pong", Ping: ping!.Ping,
                    Received: clock.Now, Sent: clock.Now), token);
                while (await Wire.ReceiveAsync<ClientMessage>(socket, Protocol.ReceiveTimeout, token) is { } message)
                {
                    if (message.Type == "ping")
                        await Wire.SendAsync(socket, new ServerMessage(Protocol.Version, "pong", Ping: message.Ping,
                            Received: clock.Now, Sent: clock.Now), token);
                    else
                    {
                        commands.TryWrite(message);
                        await Wire.SendAsync(socket, new ServerMessage(Protocol.Version, "result", Id: message.Id,
                            Error: "test-server-rejection"), token);
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
        });
        await app.StartAsync();
        return new(app, new UriBuilder(app.Urls.Single()) { Scheme = "ws", Host = "127.0.0.1", Path = "/leadership-contract" }.Uri);
    }
}
