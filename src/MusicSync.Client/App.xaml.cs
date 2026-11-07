using System.Globalization;
using System.Windows;

namespace MusicSync.Client;

public partial class App : Application
{
    private Mutex? instance;
    private MainWindow? window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        const string option = "--sonata-port";
        const string prefix = option + "=";
        int? sonataPort = null;
        foreach (string argument in e.Args)
        {
            if (!argument.Equals(option, StringComparison.OrdinalIgnoreCase) &&
                !argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (sonataPort.HasValue || argument.Length <= prefix.Length ||
                !int.TryParse(argument.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
                port is < 1 or > 65535)
            {
                MessageBox.Show("Укажите порт Sonata один раз в формате --sonata-port=9222. Допустимый диапазон от 1 до 65535.",
                    "MusicSync", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            sonataPort = port;
        }

        instance = new Mutex(true, @"Local\MusicSync.Tray", out bool first);
        if (!first)
        {
            instance.Dispose();
            instance = null;
            Shutdown();
            return;
        }

        MainWindow = window = new MainWindow(sonataPort);
        window.ShowStatusWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        window?.DisposeTray();
        instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
