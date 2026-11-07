using System.Text;
using MusicSync.Core;

namespace MusicSync.Agent;

internal static class Program
{
    public static int Main(string[] args)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), encoding);
        using var output = new StreamWriter(Console.OpenStandardOutput(), encoding) { NewLine = "\n" };
        using var diagnostics = new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true };
        var pipe = new AgentPipe(input, output);
        var endpoint = args.Length == 1 ? Protocol.ParseEndpoint(args[0], allowLan: false) : null;
        if (endpoint is null)
        {
            pipe.Send(new AgentState("ended", Error: "invalid-endpoint"));
            return 1;
        }

        using var instance = new Mutex(true, ClientInstance.MutexName, out bool first);
        if (!first)
        {
            pipe.Send(new AgentState("ended", Error: "instance-in-use"));
            return 1;
        }

        // Windows mutex ownership is thread-affine. Keep this thread for the life of the session.
        try
        {
            var log = TextWriter.Synchronized(diagnostics);
            new AgentSession(pipe, endpoint, Log).RunAsync().GetAwaiter().GetResult();
            return 0;

            void Log(string message)
            {
                try { log.WriteLine(message); }
                catch (IOException) { }
            }
        }
        finally { instance.ReleaseMutex(); }
    }
}
