using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

internal sealed class YandexMusicProcess(string executable) : IDisposable
{
    private const uint WmClose = 0x0010;
    private readonly List<(Process Process, string[] Arguments)> pendingRestarts = [];

    internal bool RestartPending => pendingRestarts.Any(instance => instance.Process.HasExited);

    internal void Close()
    {
        if (!IsRunning(executable))
        {
            return;
        }

        using var patcher = Process.GetCurrentProcess();
        var instances = FindInstances(patcher.SessionId);
        Console.WriteLine("Closing Yandex Music...");
        foreach (var (pid, arguments) in instances)
        {
            RequestClose(pid, arguments);
        }

        var wait = Stopwatch.StartNew();
        while (IsRunning(executable))
        {
            if (wait.Elapsed >= TimeSpan.FromSeconds(12))
            {
                throw new InvalidOperationException("Yandex Music did not exit after the close request. Exit it from the tray and retry.");
            }
            Thread.Sleep(150);
        }
    }

    internal void Restart()
    {
        foreach (var (process, arguments) in pendingRestarts)
        {
            if (!process.HasExited)
            {
                continue;
            }

            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = true
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            using var restarted = Process.Start(start);
            Console.WriteLine("Yandex Music restarted.");
        }
    }

    public void Dispose()
    {
        foreach (var instance in pendingRestarts)
        {
            instance.Process.Dispose();
        }
    }

    internal static bool IsRunning(string executable)
    {
        var running = false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                running |= Matches(process, executable);
            }
        }
        return running;
    }

    private List<(int Pid, string[] Arguments)> FindInstances(int sessionId)
    {
        var instances = new List<(int, string[])>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, SessionId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'Яндекс Музыка.exe'");
        using var processes = searcher.Get();
        foreach (ManagementObject process in processes)
        {
            using (process)
            {
                if (!string.Equals(process["ExecutablePath"] as string, executable, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if ((uint)process["SessionId"] != sessionId)
                {
                    throw new InvalidOperationException("Yandex Music is running in another Windows session. Close it there and retry.");
                }
                if (process["CommandLine"] is not string { Length: > 0 } commandLine)
                {
                    throw new InvalidOperationException("Could not read the Yandex Music launch arguments.");
                }
                var arguments = ReadArguments(commandLine);
                if (!arguments.Any(argument => argument.StartsWith("--type=", StringComparison.Ordinal)))
                {
                    instances.Add(((int)(uint)process["ProcessId"], arguments));
                }
            }
        }
        return instances;
    }

    private void RequestClose(int pid, string[] arguments)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }

        var requested = false;
        try
        {
            if (!Matches(process, executable))
            {
                return;
            }
            requested = process.CloseMainWindow() || CloseHiddenWindow(pid);
            if (requested)
            {
                pendingRestarts.Add((process, arguments));
            }
        }
        catch (InvalidOperationException) when (process.HasExited) { }
        finally
        {
            if (!requested)
            {
                process.Dispose();
            }
        }
    }

    private static bool Matches(Process process, string executable)
    {
        try { return string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase); }
        catch (Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool CloseHiddenWindow(int pid)
    {
        var requested = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == pid)
            {
                var name = new StringBuilder(64);
                GetClassName(window, name, name.Capacity);
                if (name.ToString() == "Chrome_WidgetWin_1")
                {
                    requested |= PostMessage(window, WmClose, 0, 0);
                }
            }
            return true;
        }, 0);
        return requested;
    }

    private static string[] ReadArguments(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            var arguments = new string[count - 1];
            for (int i = 1; i < count; i++)
            {
                arguments[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            }
            return arguments;
        }
        finally { LocalFree(argv); }
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
