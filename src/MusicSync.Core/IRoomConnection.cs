namespace MusicSync.Core;

public interface IRoomConnection
{
    ConnectionView View { get; }
    SyncClock Clock { get; }
    event Action? Changed;
    Task<string?> ActionAsync(ActionKind kind, TrackId? track = null, long? position = null,
        Playback? playback = null, long? expectedRevision = null);
    Task ReportAsync(PlayerReport report, long generation);
}
