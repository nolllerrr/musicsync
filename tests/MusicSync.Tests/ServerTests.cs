using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    public async Task CreatesRoomJoinsOneListenerAndRestrictsActions()
    {
        await using var server = await LocalServer.StartAsync();
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
    public async Task LegacyPeerCanStillJoinAndFollowPlayback()
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
        using var old = await Peer.OpenAsync(server.Uri, "create", version: 999);
        Assert.AreEqual("protocol-version", (await old.NextAsync("error")).Error);
        await RejectedAsync("""{"version":1,"version":1,"type":"create"}""");
        await RejectedAsync("""{"version":1,"type":"create","unexpected":1}""");
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
        var snapshot = await leader.NextAsync("snapshot");
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
        bridge.Drop();
        await Check.EventuallyAsync(() => listener.View.Status == ConnectionStatus.Reconnecting);
        Assert.IsNull(await leader.ActionAsync(new(ActionKind.Select, 0, Track)));
        Assert.IsNull(await leader.ActionAsync(new(ActionKind.Seek, 1, PositionMs: 48_000)));
        await Check.EventuallyAsync(() => listener.View is { Generation: 2, Room.State.Revision: 2 });
        Assert.AreEqual(48_000L, listener.View.Room!.State.PositionMs);
        Assert.AreEqual(Track, listener.View.Room.State.Track);
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
    private readonly Channel<ServerMessage> messages = Channel.CreateUnbounded<ServerMessage>();
    private Peer(string token) => Token = token;
    public string Token { get; }

    public static async Task<Peer> OpenAsync(Uri uri, string kind, string? room = null, string? token = null,
        int version = Protocol.Version, bool roleSwap = false)
    {
        var peer = new Peer(token ?? Protocol.NewToken());
        if (roleSwap)
            peer.socket.Options.AddSubProtocol(Protocol.RoleSwapSubProtocol);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await peer.socket.ConnectAsync(uri, timeout.Token);
        await peer.SendAsync(new(version, kind, Token: peer.Token, RoomId: room));
        _ = peer.ReadAsync();
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

    public ValueTask SendAsync(ClientMessage message) => Wire.SendAsync(socket, message, stop.Token);

    public async Task<RoomView> SnapshotAsync(Func<RoomView, bool> filter) =>
        (await NextAsync("snapshot", message => filter(message.Room!))).Room!;

    public async Task<ServerMessage> NextAsync(string type, Func<ServerMessage, bool>? filter = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await messages.Reader.WaitToReadAsync(timeout.Token))
            while (messages.Reader.TryRead(out var message))
                if (message.Type == type && (filter?.Invoke(message) ?? true))
                    return message;
        throw new InvalidOperationException($"Socket closed before {type}.");
    }

    public void Abort() => socket.Abort();

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
