using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using MusicSync.Core;
using MusicSync.Yandex;

namespace MusicSync.Agent;

internal sealed record AgentInput(string Type, int Id, string? Command = null, string? Code = null,
    Guid? SwapId = null, JsonElement? Result = null, string? Error = null);

internal sealed record AgentResult(int Id, string? Error = null)
{
    public string Type => "result";
}

internal sealed record AgentEvaluation(int Id, string Expression)
{
    public string Type => "evaluate";
}

internal sealed class AgentPipe(StreamReader input, TextWriter output) : ISonataTransport
{
    private const int MaxLineLength = 64 * 1024;
    private readonly CancellationTokenSource disconnected = new();
    private readonly Channel<AgentInput> commands = Channel.CreateBounded<AgentInput>(16);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<AgentInput>> evaluations = new();
    private readonly object outputGate = new();
    private int nextEvaluation;

    public CancellationToken Disconnected => disconnected.Token;
    public ChannelReader<AgentInput> Commands => commands.Reader;

    public async Task ReadAsync()
    {
        try
        {
            while (await input.ReadLineAsync(Disconnected) is { } line)
            {
                if (line.Length > MaxLineLength)
                    continue;
                AgentInput? message;
                try { message = JsonSerializer.Deserialize<AgentInput>(line, Protocol.Json); }
                catch (JsonException) { continue; }
                if (message is not { Id: > 0 })
                    continue;

                if (message.Type == "evalResult" && evaluations.TryGetValue(message.Id, out var pending))
                    pending.TrySetResult(message);
                else if (message.Type == "command" && !commands.Writer.TryWrite(message))
                    Send(new AgentResult(message.Id, "agent-busy"));
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) when (Disconnected.IsCancellationRequested) { }
        finally
        {
            disconnected.Cancel();
            commands.Writer.TryComplete();
        }
    }

    public void Send<T>(T message)
    {
        lock (outputGate)
        {
            if (Disconnected.IsCancellationRequested)
                return;
            try
            {
                output.WriteLine(JsonSerializer.Serialize(message, Protocol.Json));
                output.Flush();
            }
            catch (IOException) { disconnected.Cancel(); }
        }
    }

    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int id = Interlocked.Increment(ref nextEvaluation);
        var pending = new TaskCompletionSource<AgentInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        evaluations[id] = pending;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, Disconnected);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            Send(new AgentEvaluation(id, expression));
            var reply = await pending.Task.WaitAsync(timeout.Token);
            if (reply.Error is "no-player" or "no-session")
                throw new PlayerException(reply.Error);
            if (reply.Error is not null || reply.Result is not { ValueKind: JsonValueKind.Object } result)
                throw new PlayerException("sonata-command-failed");
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new PlayerException(Disconnected.IsCancellationRequested ? "no-player" : "sonata-command-failed");
        }
        finally { evaluations.TryRemove(id, out _); }
    }
}
