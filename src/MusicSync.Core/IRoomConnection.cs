using System.Text.Json.Serialization;

namespace MusicSync.Core;

// A local view of public membership, independent of the server's wire DTOs.
public sealed record PublicParticipant(Guid ParticipantId, Role Role, bool Connected,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PlayerReport? Report);

public interface IRoomConnection
{
    ConnectionView View { get; }
    SyncClock Clock { get; }
    event Action? Changed;
    Task<string?> ActionAsync(ActionKind kind, TrackId? track = null, long? position = null,
        Playback? playback = null, long? expectedRevision = null);
    Task ReportAsync(PlayerReport report, long generation);
}
