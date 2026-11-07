if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
{
    Console.Error.WriteLine("Windows x64 is required.");
    return 1;
}

if (args is not ["install" or "uninstall", ..] || args.Length > 2)
{
    Console.Error.WriteLine("Usage: MusicSync.Patch.exe install|uninstall [Yandex Music directory]");
    return 2;
}

try
{
    var directory = args.Length == 2 ? args[1] : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "YandexMusic");
    new PatchInstallation(Path.GetFullPath(directory), AppContext.BaseDirectory).Run(args[0] == "install");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
