using MusicSync.Core;

namespace MusicSync.Server;

/// <summary>
/// Checks used atomically by RoomRegistry. Call under the registry lock, resolving actorId from
/// the current server session and leaderId/isConnected from that same room. These checks do not
/// consume an offer or change roles; validation and commit must share that lock.
/// </summary>
internal static class LeadershipTransferPolicy
{
    public static string? Create(LeadershipTransfer? pending, Guid actorId, Guid leaderId, Guid targetId,
        Func<Guid, bool> isConnected, long now, out LeadershipTransfer? transfer)
    {
        transfer = null;
        if (actorId == Guid.Empty || actorId != leaderId)
            return "leader-only";
        if (!isConnected(actorId))
            return "leadership-transfer-unavailable";
        if (targetId == Guid.Empty || targetId == actorId)
            return "leadership-transfer-invalid-target";
        if (!isConnected(targetId))
            return "leadership-transfer-unavailable";
        if (KeepPending(pending, leaderId, isConnected, now) is not null)
            return "leadership-transfer-pending";
        transfer = new(Guid.NewGuid(), actorId, targetId, checked(now + LeadershipTransfer.WindowMs));
        return null;
    }

    public static string? Accept(LeadershipTransfer? pending, Guid transferId, Guid actorId, Guid leaderId,
        Func<Guid, bool> isConnected, long now) =>
        Validate(pending, transferId, leaderId, isConnected, now) ??
        (actorId != pending!.TargetParticipantId ? "leadership-transfer-target-only" : null);

    public static string? Cancel(LeadershipTransfer? pending, Guid transferId, Guid actorId, Guid leaderId,
        Func<Guid, bool> isConnected, long now) =>
        Validate(pending, transferId, leaderId, isConnected, now) ??
        (actorId != pending!.TargetParticipantId && actorId != pending.InitiatorParticipantId
            ? "leadership-transfer-forbidden" : null);

    public static LeadershipTransfer? KeepPending(LeadershipTransfer? pending, Guid leaderId,
        Func<Guid, bool> isConnected, long now) =>
        pending is not null && Validate(pending, pending.Id, leaderId, isConnected, now) is null ? pending : null;

    public static RoomState AdvancePlaybackEpoch(RoomState state, long now) => state with
    {
        Revision = checked(state.Revision + 1),
        PositionMs = state.PositionAt(now),
        ServerTimestamp = now
    };

    private static string? Validate(LeadershipTransfer? pending, Guid transferId, Guid leaderId,
        Func<Guid, bool> isConnected, long now)
    {
        if (transferId == Guid.Empty || pending is null || pending.Id != transferId)
            return "leadership-transfer-stale";
        if (pending.ExpiresAt <= now)
            return "leadership-transfer-expired";
        if (pending.InitiatorParticipantId == Guid.Empty || pending.TargetParticipantId == Guid.Empty ||
            pending.InitiatorParticipantId == pending.TargetParticipantId || pending.InitiatorParticipantId != leaderId ||
            !isConnected(pending.InitiatorParticipantId) || !isConnected(pending.TargetParticipantId))
            return "leadership-transfer-unavailable";
        return null;
    }
}
