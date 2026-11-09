using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using MusicSync.Core;
using MusicSync.Server;

await ServerHost.Build(args).RunAsync();

namespace MusicSync.Server
{
    public static class ServerHost
    {
        public static WebApplication Build(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
            string mode = builder.Configuration["Sync:Mode"] ?? "Loopback";
            int port = builder.Configuration.GetValue("Sync:Port", mode == "Tls" ? 8443 : 5050);
            int maxConnections = builder.Configuration.GetValue("Sync:MaxConnections", 32);
            int maxRooms = builder.Configuration.GetValue("Sync:MaxRooms", 16);
            int maxRoomsPerSource = builder.Configuration.GetValue("Sync:MaxRoomsPerSource", 4);
            int maxRoomCreationsPerSource = builder.Configuration.GetValue("Sync:MaxRoomCreationsPerSource", 8);
            int roomCreationWindowSeconds = builder.Configuration.GetValue("Sync:RoomCreationWindowSeconds", 60);
            int graceSeconds = builder.Configuration.GetValue("Sync:GraceSeconds", 90);
            if (port is < 0 or > 65535 || maxConnections is < 2 or > 256 ||
                maxRooms is < 1 or > 128 || maxRoomsPerSource is < 1 or > 128 ||
                maxRoomCreationsPerSource is < 1 or > 128 || roomCreationWindowSeconds is < 1 or > 3600 ||
                graceSeconds is < 5 or > 600)
                throw new InvalidOperationException("Invalid MusicSync limits or port.");
            string? trustedProxySetting = builder.Configuration["Sync:TrustedProxy"];
            IPAddress? trustedProxy = null;
            if (!string.IsNullOrEmpty(trustedProxySetting) && !IPAddress.TryParse(trustedProxySetting, out trustedProxy))
                throw new InvalidOperationException("Sync:TrustedProxy must be a proxy IP address.");

            // Keep framework-wide forwarding switches from changing the peer IP before the access check.
            builder.Services.PostConfigure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.None);
            var clock = new MonotonicClock();
            var rooms = new RoomRegistry(clock, maxRooms, maxRoomsPerSource, maxRoomCreationsPerSource,
                TimeSpan.FromSeconds(roomCreationWindowSeconds), TimeSpan.FromSeconds(graceSeconds));
            var slots = new SemaphoreSlim(maxConnections);

            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.AddServerHeader = false;
                kestrel.Limits.MaxConcurrentConnections = maxConnections + 16;
                kestrel.Limits.MaxRequestHeadersTotalSize = 8192;
                kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(8);
                switch (mode)
                {
                    case "Loopback":
                        kestrel.Listen(IPAddress.Loopback, port);
                        break;
                    case "Lan":
                        if (!IPAddress.TryParse(builder.Configuration["Sync:LanIp"], out var ip) ||
                            !Protocol.IsPrivateV4(ip) || port == 0)
                            throw new InvalidOperationException("LAN mode requires an explicit private IPv4 and fixed port.");
                        kestrel.Listen(ip, port);
                        kestrel.Listen(IPAddress.Loopback, port);
                        break;
                    case "Tls":
                        var path = builder.Configuration["Sync:TlsPfxPath"];
                        if (string.IsNullOrWhiteSpace(path))
                            throw new InvalidOperationException("TLS mode requires a PFX certificate.");
                        kestrel.ListenAnyIP(port, endpoint =>
                            endpoint.UseHttps(path, builder.Configuration["Sync:TlsPfxPassword"]));
                        break;
                    default:
                        throw new InvalidOperationException("Sync:Mode must be Loopback, Lan or Tls.");
                }
            });

            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                var ip = context.Connection.RemoteIpAddress;
                if (mode == "Lan" && (ip is null || !(IPAddress.IsLoopback(ip) || Protocol.IsPrivateV4(ip))) ||
                    mode == "Tls" && !context.Request.IsHttps)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                await next(context);
            });
            if (trustedProxy is not null)
            {
                var forwarding = new ForwardedHeadersOptions
                {
                    ForwardedHeaders = ForwardedHeaders.XForwardedFor,
                    ForwardLimit = 1
                };
                forwarding.KnownIPNetworks.Clear();
                forwarding.KnownProxies.Clear();
                forwarding.KnownProxies.Add(trustedProxy.IsIPv4MappedToIPv6 ? trustedProxy.MapToIPv4() : trustedProxy);
                app.UseWhen(context => context.Connection.RemoteIpAddress is not null,
                    branch => branch.UseForwardedHeaders(forwarding));
            }
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
            app.MapGet("/health", () => Results.Ok(new { status = "ok", protocol = Protocol.Version, mode }));
            app.Map("/ws", async context =>
            {
                if (context.Request.Headers.ContainsKey("Origin") || context.Request.QueryString.HasValue)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                if (!slots.Wait(0))
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }
                try
                {
                    string? subProtocol = context.WebSockets.WebSocketRequestedProtocols.Contains(Protocol.RoleSwapSubProtocol)
                        ? Protocol.RoleSwapSubProtocol : null;
                    var source = context.Connection.RemoteIpAddress ?? IPAddress.None;
                    if (source.IsIPv4MappedToIPv6)
                        source = source.MapToIPv4();
                    using var socket = await context.WebSockets.AcceptWebSocketAsync(subProtocol);
                    await ServeAsync(socket, source, rooms, clock, app.Logger, context.RequestAborted, app.Lifetime.ApplicationStopping);
                }
                finally { slots.Release(); }
            });
            var maintenance = new Timer(_ => rooms.Prune(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            app.Lifetime.ApplicationStopped.Register(maintenance.Dispose);
            return app;
        }

        private static async Task ServeAsync(WebSocket socket, IPAddress source, RoomRegistry rooms, MonotonicClock clock,
            ILogger logger, CancellationToken request, CancellationToken stopping)
        {
            using var peer = new RoomPeer(socket.SubProtocol == Protocol.RoleSwapSubProtocol);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(request, stopping, peer.Disconnect);
            var token = lifetime.Token;
            var writer = WriteAsync();
            try
            {
                var hello = await Wire.ReceiveAsync<ClientMessage>(socket, TimeSpan.FromSeconds(6), token);
                if (hello is null)
                    return;
                var rejection = rooms.Enter(peer, hello, source);
                if (rejection is not null)
                {
                    peer.Send(rejection);
                    peer.Complete();
                    await writer;
                    return;
                }

                long window = clock.Now;
                int count = 0;
                while (!token.IsCancellationRequested)
                {
                    var message = await Wire.ReceiveAsync<ClientMessage>(socket, Protocol.ReceiveTimeout, token);
                    if (message is null)
                        break;
                    long received = clock.Now;
                    if (received - window >= 1000)
                    {
                        count = 0;
                        window = received;
                    }
                    if (++count > 20 || !rooms.Handle(peer, message, received))
                    {
                        if (peer.IsCompleting)
                            await writer;
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or
                IOException or InvalidDataException or JsonException)
            {
                logger.LogDebug("WebSocket session ended ({ErrorType}).", ex.GetType().Name);
            }
            finally
            {
                rooms.Detach(peer);
                await lifetime.CancelAsync();
                await writer;
                if (socket.State == WebSocketState.CloseReceived)
                {
                    using var close = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", close.Token); }
                    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
                }
            }

            async Task WriteAsync()
            {
                try
                {
                    await foreach (var message in peer.Outgoing.ReadAllAsync(token))
                    {
                        using var sendLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
                        sendLimit.CancelAfter(TimeSpan.FromSeconds(4));
                        await Wire.SendAsync(socket, message.Type == "pong" ? message with { Sent = clock.Now } : message,
                            sendLimit.Token);
                    }
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        using var close = CancellationTokenSource.CreateLinkedTokenSource(token);
                        close.CancelAfter(TimeSpan.FromSeconds(1));
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", close.Token);
                    }
                    lifetime.CancelAfter(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
                {
                    await lifetime.CancelAsync();
                }
            }
        }
    }
}
