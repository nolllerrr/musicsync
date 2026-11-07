namespace MusicSync.Core;

public static class ClientInstance
{
    // Retain the original tray client's mutex name to also exclude an unpatched 0.1.9 client.
    public const string MutexName = @"Local\MusicSync.Tray";
}
