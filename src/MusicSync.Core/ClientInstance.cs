namespace MusicSync.Core;

public static class ClientInstance
{
    // Keep the mutex name stable across versions so only one session can control playback.
    public const string MutexName = @"Local\MusicSync.Tray";
}
