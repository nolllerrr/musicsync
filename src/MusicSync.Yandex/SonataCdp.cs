using System.Globalization;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using MusicSync.Core;

namespace MusicSync.Yandex;

internal sealed class SonataCdp(int port)
{
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly HttpClient LocalHttp = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false
    });

    private readonly string authority = "127.0.0.1:" + (port is >= 1 and <= 65535
        ? port.ToString(CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(port)));

    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[MaxResponseBytes + 1];
        using var socket = new ClientWebSocket();
        bool connected = false;
        try
        {
            var target = await DiscoverAsync(buffer, timeout.Token).ConfigureAwait(false);
            await socket.ConnectAsync(target, LocalHttp, timeout.Token).ConfigureAwait(false);
            connected = true;
            byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new { expression, returnByValue = true, awaitPromise = true, userGesture = false }
            });
            await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            return await ReadResultAsync(socket, buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new PlayerException(connected ? "sonata-command-failed" : "no-player");
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or IOException)
        {
            token.ThrowIfCancellationRequested();
            throw new PlayerException("no-player");
        }
        catch (JsonException)
        {
            throw new PlayerException("no-session");
        }
    }

    private async Task<Uri> DiscoverAsync(byte[] buffer, CancellationToken token)
    {
        using var response = await LocalHttp.GetAsync($"http://{authority}/json/list",
            HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new PlayerException("no-session");
        using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await body.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0)
                break;
            length += read;
        }
        if (length > MaxResponseBytes)
            throw new PlayerException("no-session");

        using var discovery = JsonDocument.Parse(buffer.AsMemory(0, length));
        if (discovery.RootElement.ValueKind != JsonValueKind.Array)
            throw new PlayerException("no-session");

        Uri? endpoint = null;
        foreach (var target in discovery.RootElement.EnumerateArray())
        {
            if (Text(target, "type") != "page" ||
                !Uri.TryCreate(Text(target, "url"), UriKind.Absolute, out var page) ||
                page.Scheme != "music-application" || page.Host != "desktop" ||
                !page.IsDefaultPort || page.UserInfo.Length != 0)
                continue;

            var id = Text(target, "id");
            if (endpoint is not null || id is not { Length: > 0 and <= 128 } ||
                !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                throw new PlayerException("no-session");
            var expected = $"ws://{authority}/devtools/page/{id}";
            if (Text(target, "webSocketDebuggerUrl") != expected)
                throw new PlayerException("no-session");
            endpoint = new Uri(expected);
        }
        return endpoint ?? throw new PlayerException("no-player");
    }

    private static async Task<JsonElement> ReadResultAsync(ClientWebSocket socket, byte[] buffer, CancellationToken token)
    {
        int remaining = MaxResponseBytes;
        while (remaining > 0)
        {
            int length = 0;
            ValueWebSocketReceiveResult frame;
            do
            {
                if (remaining == 0)
                    throw new PlayerException("no-session");
                frame = await socket.ReceiveAsync(buffer.AsMemory(length, remaining), token).ConfigureAwait(false);
                if (frame.MessageType == WebSocketMessageType.Close)
                    throw new PlayerException("no-player");
                if (frame.MessageType != WebSocketMessageType.Text)
                    throw new PlayerException("no-session");
                length += frame.Count;
                remaining -= frame.Count;
            } while (!frame.EndOfMessage);

            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            var reply = document.RootElement;
            if (reply.ValueKind != JsonValueKind.Object)
                throw new PlayerException("no-session");
            if (Text(reply, "method") == "Inspector.detached")
                throw new PlayerException("no-player");
            if (!reply.TryGetProperty("id", out var id) && Text(reply, "method") is not null)
                continue;
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int requestId) || requestId != 1)
                throw new PlayerException("no-session");
            if (reply.TryGetProperty("error", out _))
                throw new PlayerException("sonata-command-failed");
            if (!reply.TryGetProperty("result", out var evaluation) || evaluation.ValueKind != JsonValueKind.Object)
                throw new PlayerException("no-session");
            if (evaluation.TryGetProperty("exceptionDetails", out _))
                throw new PlayerException("sonata-command-failed");
            if (!evaluation.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("value", out var value))
                throw new PlayerException("no-session");
            return value.Clone();
        }
        throw new PlayerException("no-session");
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
