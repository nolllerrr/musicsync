using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;

internal sealed class YandexMusicRestart(string executable, string agent)
{
    private string[]? _restartArguments;

    internal static bool IsRunning(string root)
    {
        foreach (var path in new[] { Path.Combine(root, "Яндекс Музыка.exe"),
                     Path.Combine(root, "resources", "musicsync-together", PatchInstallation.AgentName) })
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited && string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { return true; }
                }
            }
        }
        return false;
    }

    internal bool ShouldRestart => _restartArguments is not null;

    internal void Close()
    {
        var players = Find(executable);
        try
        {
            if (players.Count != 0)
            {
                var browsers = FindBrowsers(players);
                foreach (var (process, arguments) in browsers)
                {
                    if (process.HasExited)
                        continue;

                    _restartArguments ??= arguments;
                    try { process.CloseMainWindow(); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }

                var grace = Stopwatch.StartNew();
                foreach (var (process, _) in browsers)
                {
                    if (!process.WaitForExit(Math.Max(0, 3_000 - (int)grace.ElapsedMilliseconds)))
                        Terminate(process);
                }
            }
        }
        finally
        {
            foreach (var process in players)
                process.Dispose();
        }

        // The browser can leave Chromium subprocesses or a room agent behind as it exits.
        foreach (var path in new[] { executable, agent })
        {
            var remaining = Find(path);
            try
            {
                foreach (var process in remaining)
                {
                    if (path == executable || !process.WaitForExit(2_000))
                        Terminate(process);
                }
            }
            finally
            {
                foreach (var process in remaining)
                    process.Dispose();
            }
        }
    }

    internal string RestartNotice()
    {
        if (_restartArguments is null)
            return "";

        try
        {
            var running = Find(executable);
            foreach (var process in running)
                process.Dispose();
            if (running.Count == 0)
            {
                var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)! };
                foreach (var argument in _restartArguments)
                    start.ArgumentList.Add(argument);

                using var process = Process.Start(start);
            }

            return " Яндекс Музыка снова запущена.";
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return $" Не удалось снова открыть Яндекс Музыку. {error.Message}";
        }
    }

    private static List<Process> Find(string path)
    {
        using var current = Process.GetCurrentProcess();
        var matches = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            try
            {
                if (process.SessionId == current.SessionId &&
                    string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(process);
                    continue;
                }
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            process.Dispose();
        }
        return matches;
    }

    private static List<(Process Process, string[] Arguments)> FindBrowsers(List<Process> players)
    {
        var filter = string.Join(" OR ", players.Select(process => $"ProcessId = {process.Id}"));
        using var query = new ManagementObjectSearcher($"SELECT ProcessId, CommandLine FROM Win32_Process WHERE {filter}");
        using var results = query.Get();
        var browsers = new List<(Process, string[])>();
        foreach (var item in results)
        {
            using var row = item;
            var process = players.Single(player => player.Id == (uint)row["ProcessId"]);
            if (process.HasExited)
                continue;
            if (row["CommandLine"] is not string commandLine || string.IsNullOrWhiteSpace(commandLine))
                throw new InvalidOperationException("Не удалось прочитать параметры запущенной Яндекс Музыки.");

            var arguments = GetArguments(commandLine);
            if (!arguments.Any(argument => argument == "--type" || argument.StartsWith("--type=", StringComparison.Ordinal)))
                browsers.Add((process, arguments));
        }
        return browsers;
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception error) when ((error is InvalidOperationException or Win32Exception) && process.HasExited) { }
        if (!process.WaitForExit(5_000))
            throw new IOException("Не удалось завершить процессы Яндекс Музыки.");
    }

    private static string[] GetArguments(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine.TrimStart(), out var count);
        if (argv == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var arguments = new string[count - 1];
            for (var index = 1; index < count; index++)
                arguments[index - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!;
            return arguments;
        }
        finally { LocalFree(argv); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
