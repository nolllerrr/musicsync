namespace MusicSync.Core;

internal static class RoomMembership
{
    public static ConnectionView Update(ConnectionView view, ServerMessage message) => view with
    {
        Participants = message.Room!.Participants,
        ParticipantId = message.ParticipantId ?? view.ParticipantId,
        SelfParticipantId = message.ParticipantId ?? view.SelfParticipantId,
        Capacity = message.Room.Capacity
    };

    public static PlayerReport? LeaderReport(ConnectionView view) =>
        view.Participants.FirstOrDefault(p => p.Role == Role.Leader)?.Report ?? view.Room?.Leader.Report;

    public static bool? PeerConnected(ConnectionView view) => view.Participants.Count > 0
        ? view.Participants.Any(p => p.ParticipantId != view.SelfParticipantId && p.Connected)
        : (view.Role == Role.Leader ? view.Room?.Listener : view.Room?.Leader)?.Connected;
}
