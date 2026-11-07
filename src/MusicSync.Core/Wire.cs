using System.Net.WebSockets;
using System.Text.Json;

namespace MusicSync.Core;

public static class Wire
{
    public static async Task<T?> ReceiveAsync<T>(WebSocket socket, TimeSpan timeout, CancellationToken cancellation)
        where T : class
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        var buffer = new byte[Protocol.MaxMessageBytes];
        int length = 0;
        ValueWebSocketReceiveResult frame;
        do
        {
            if (length == buffer.Length)
                throw new InvalidDataException("Message too large.");
            frame = await socket.ReceiveAsync(buffer.AsMemory(length), limit.Token);
            if (frame.MessageType == WebSocketMessageType.Close)
                return null;
            if (frame.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Text JSON is required.");
            length += frame.Count;
        } while (!frame.EndOfMessage);

        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), Protocol.Json)
            ?? throw new InvalidDataException("A JSON object is required.");
    }

    public static ValueTask SendAsync<T>(WebSocket socket, T message, CancellationToken cancellation) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, Protocol.Json).AsMemory(),
            WebSocketMessageType.Text, true, cancellation);
}
