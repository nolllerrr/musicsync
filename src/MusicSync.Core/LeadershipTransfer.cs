namespace MusicSync.Core;

/// <summary>A server-issued offer addressed to one participant, using the server's monotonic clock.</summary>
public sealed record LeadershipTransfer(Guid Id, Guid InitiatorParticipantId, Guid TargetParticipantId, long ExpiresAt)
{
    public const long WindowMs = 30_000;
}
