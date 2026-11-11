namespace MusicSync.Core;

// PR 1 integration point. The v1 server has no public identities: do not invent
// IDs from roles, array positions or private resume tokens. See docs/GROUP_ROOMS_SYNC.md.
internal static class RoomMembership
{
    public static ConnectionView Update(ConnectionView view, ServerMessage message) => view with
    {
        Participants = Array.Empty<PublicParticipant>(),
        SelfParticipantId = null,
        Capacity = 2
    };

    public static PlayerReport? LeaderReport(ConnectionView view) =>
        view.Participants.FirstOrDefault(p => p.Role == Role.Leader)?.Report ?? view.Room?.Leader.Report;

    public static bool? PeerConnected(ConnectionView view) => view.Participants.Count > 0
        ? view.Participants.Any(p => p.ParticipantId != view.SelfParticipantId && p.Connected)
        : (view.Role == Role.Leader ? view.Room?.Listener : view.Room?.Leader)?.Connected;
}
