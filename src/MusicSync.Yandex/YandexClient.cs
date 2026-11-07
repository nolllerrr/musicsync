using System.ComponentModel;
using System.Diagnostics;

internal readonly record struct YandexClient(bool Running, nint Window)
{
    private const string ProcessName = "Яндекс Музыка";
    private static readonly string InstalledPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "YandexMusic", ProcessName + ".exe");

    public static YandexClient Find()
    {
        bool running = false;
        var windows = new HashSet<nint>();
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                try
                {
                    if (!string.Equals(process.MainModule?.FileName, InstalledPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    running = true;
                    if (process.MainWindowHandle != 0)
                        windows.Add(process.MainWindowHandle);
                }
                catch (InvalidOperationException) { /* The process exited during enumeration. */ }
                catch (Win32Exception) { /* Its executable path could not be verified. */ }
            }
        }
        return new YandexClient(running, windows.Count == 1 ? windows.Single() : 0);
    }

    public void OpenTrackPage(Uri uri)
    {
        if (Window == 0)
            throw new InvalidOperationException("Open the Yandex Music window before using play-url.");

        // The official app forwards this argument to its running instance.
        var start = new ProcessStartInfo(InstalledPath) { UseShellExecute = false };
        start.ArgumentList.Add(uri.AbsoluteUri);
        try
        {
            using var handoff = Process.Start(start);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("Could not send the track URL to the installed Yandex Music executable.", ex);
        }
    }
}
