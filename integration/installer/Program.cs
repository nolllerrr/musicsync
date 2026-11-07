using System.IO;
using System.Text;
using System.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Electron starts the installed copy with the existing Agent command line.
        if (string.Equals(Path.GetFileName(Environment.ProcessPath), PatchInstallation.AgentName,
                StringComparison.OrdinalIgnoreCase))
        {
            return MusicSync.Agent.Program.Main(args);
        }

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "YandexMusic");

        try
        {
            if (args is ["install" or "uninstall", ..] && args.Length <= 2)
            {
                Console.OutputEncoding = Encoding.UTF8;
                var installation = new PatchInstallation(args.Length == 2 ? Path.GetFullPath(args[1]) : directory);
                var install = args[0] == "install";
                Console.WriteLine(installation.Run(install, endpoint: null));
                return 0;
            }

            if (args is ["--directory", var target])
            {
                directory = Path.GetFullPath(target);
            }
            else if (args.Length != 0)
            {
                Console.Error.WriteLine("Usage: MusicSync.Patch.exe [--directory <path> | install|uninstall [path]]");
                return 2;
            }

            return new Application().Run(new PatchWindow(new PatchInstallation(directory)));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            if (args.Length == 0 || args is ["--directory", _])
            {
                MessageBox.Show(error.Message, "MusicSync", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return 1;
        }
    }
}
