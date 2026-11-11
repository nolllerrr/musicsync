using Microsoft.VisualStudio.TestTools.UnitTesting;
using MusicSync.Core;
using MusicSync.Server;

namespace MusicSync.Tests;

[TestClass]
public sealed class LeadershipTransferPolicyTests
{
    private static readonly Guid Leader = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Target = Guid.Parse("00000000-0000-0000-0000-000000000005");
    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-000000000009");
    private const long Now = 100_000;
    private static bool Connected(Guid id) => id == Leader || id == Target || id == Other;

    private static LeadershipTransfer Offer()
    {
        Assert.IsNull(LeadershipTransferPolicy.Create(null, Leader, Leader, Target, Connected, Now, out var offer));
        return offer!;
    }

    [TestMethod]
    public void CreatesAnOfferForExactlyThirtySecondsWithUniqueIds()
    {
        var first = Offer();
        var second = Offer();
        Assert.AreNotEqual(Guid.Empty, first.Id);
        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(Leader, first.InitiatorParticipantId);
        Assert.AreEqual(Target, first.TargetParticipantId);
        Assert.AreEqual(Now + 30_000, first.ExpiresAt);
    }

    [TestMethod]
    [DataRow("listener", "leader-only")]
    [DataRow("empty-actor", "leader-only")]
    [DataRow("offline-leader", "leadership-transfer-unavailable")]
    [DataRow("self", "leadership-transfer-invalid-target")]
    [DataRow("empty-target", "leadership-transfer-invalid-target")]
    [DataRow("offline-target", "leadership-transfer-unavailable")]
    [DataRow("other-room-target", "leadership-transfer-unavailable")]
    [DataRow("pending", "leadership-transfer-pending")]
    public void RejectsUnauthorizedOrUnavailableCreation(string scenario, string error)
    {
        var actor = scenario == "listener" ? Other : scenario == "empty-actor" ? Guid.Empty : Leader;
        var target = scenario == "self" ? Leader : scenario == "empty-target" ? Guid.Empty :
            scenario == "other-room-target" ? Guid.NewGuid() : Target;
        bool IsConnected(Guid id) => Connected(id) &&
            !(scenario == "offline-leader" && id == Leader || scenario == "offline-target" && id == Target);
        var pending = scenario == "pending" ? Offer() : null;
        Assert.AreEqual(error, LeadershipTransferPolicy.Create(pending, actor, Leader, target, IsConnected, Now, out var offer));
        Assert.IsNull(offer);
    }

    [TestMethod]
    [DataRow(-1L, null)]
    [DataRow(0L, "leadership-transfer-expired")]
    [DataRow(1L, "leadership-transfer-expired")]
    public void AcceptanceUsesServerTimeAndAnExclusiveDeadline(long offset, string? error)
    {
        var offer = Offer();
        Assert.AreEqual(error, LeadershipTransferPolicy.Accept(offer, offer.Id, Target, Leader, Connected, offer.ExpiresAt + offset));
    }

    [TestMethod]
    public void OnlyTheSelectedListenerCanAcceptTheCurrentId()
    {
        var offer = Offer();
        foreach (var actor in new[] { Leader, Other, Guid.Empty, Guid.NewGuid() })
            Assert.AreEqual("leadership-transfer-target-only",
                LeadershipTransferPolicy.Accept(offer, offer.Id, actor, Leader, Connected, Now));
        foreach (var id in new[] { Guid.Empty, Guid.NewGuid() })
            Assert.AreEqual("leadership-transfer-stale",
                LeadershipTransferPolicy.Accept(offer, id, Target, Leader, Connected, Now));
        Assert.IsNull(LeadershipTransferPolicy.Accept(offer, offer.Id, Target, Leader, Connected, Now));
        // The registry must clear the offer in the same critical section as the role change.
        Assert.AreEqual("leadership-transfer-stale",
            LeadershipTransferPolicy.Accept(null, offer.Id, Target, Target, Connected, Now));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void EitherEndpointDisconnectInvalidatesConsentEvenAfterReconnect(bool disconnectLeader)
    {
        var offer = Offer();
        var disconnected = disconnectLeader ? Leader : Target;
        bool IsConnected(Guid id) => Connected(id) && id != disconnected;
        Assert.AreEqual("leadership-transfer-unavailable",
            LeadershipTransferPolicy.Accept(offer, offer.Id, Target, Leader, IsConnected, Now));
        var cleared = LeadershipTransferPolicy.KeepPending(offer, Leader, IsConnected, Now);
        Assert.IsNull(cleared);
        Assert.AreEqual("leadership-transfer-stale",
            LeadershipTransferPolicy.Accept(cleared, offer.Id, Target, Leader, Connected, Now));
    }

    [TestMethod]
    public void UnrelatedDisconnectDoesNotInvalidateAnOfferButLeaderChangeDoes()
    {
        var offer = Offer();
        Assert.AreEqual(offer, LeadershipTransferPolicy.KeepPending(offer, Leader, id => id != Other && Connected(id), Now));
        Assert.IsNull(LeadershipTransferPolicy.KeepPending(offer, Other, Connected, Now));
        Assert.AreEqual("leadership-transfer-unavailable",
            LeadershipTransferPolicy.Accept(offer, offer.Id, Target, Other, Connected, Now));
    }

    [TestMethod]
    public void OnlyInitiatorOrTargetCanCancelAndExpiredOffersCannotBeConfirmed()
    {
        var offer = Offer();
        Assert.IsNull(LeadershipTransferPolicy.Cancel(offer, offer.Id, Leader, Leader, Connected, Now));
        Assert.IsNull(LeadershipTransferPolicy.Cancel(offer, offer.Id, Target, Leader, Connected, Now));
        Assert.AreEqual("leadership-transfer-forbidden",
            LeadershipTransferPolicy.Cancel(offer, offer.Id, Other, Leader, Connected, Now));
        Assert.AreEqual("leadership-transfer-stale",
            LeadershipTransferPolicy.Cancel(offer, Guid.NewGuid(), Target, Leader, Connected, Now));
        Assert.IsNull(LeadershipTransferPolicy.KeepPending(offer, Leader, Connected, offer.ExpiresAt));
        Assert.IsNull(LeadershipTransferPolicy.Create(offer, Leader, Leader, Other, Connected, offer.ExpiresAt, out var next));
        Assert.AreNotEqual(offer.Id, next!.Id);
        Assert.AreEqual(Other, next.TargetParticipantId);
        Assert.AreEqual("leadership-transfer-stale",
            LeadershipTransferPolicy.Accept(next, offer.Id, Target, Leader, Connected, offer.ExpiresAt));
    }

    [TestMethod]
    [DataRow(Playback.Playing, 47_500L)]
    [DataRow(Playback.Paused, 45_000L)]
    public void PlaybackEpochPreservesTrackSelectionAndProjectedPosition(Playback playback, long expectedPosition)
    {
        var before = new RoomState("ROOM", 8, 3, new("1", "2"), playback, 45_000, Now, false);
        var after = LeadershipTransferPolicy.AdvancePlaybackEpoch(before, Now + 2500);
        Assert.AreEqual(before with { Revision = 9, PositionMs = expectedPosition, ServerTimestamp = Now + 2500 }, after);
        Assert.AreEqual(before.PositionAt(Now + 5000), after.PositionAt(Now + 5000));
    }

    [TestMethod]
    public void PlaybackEpochAlsoPreservesUnknownTracksAndBoundsTheProjectedPosition()
    {
        var unknown = new RoomState("ROOM", 8, 3, null, Playback.Paused, 0, Now, true);
        Assert.AreEqual(unknown with { Revision = 9, ServerTimestamp = Now + 2500 },
            LeadershipTransferPolicy.AdvancePlaybackEpoch(unknown, Now + 2500));
        var playing = unknown with { Track = new("1", "2"), Unknown = false, Playback = Playback.Playing,
            PositionMs = Protocol.MaxPositionMs - 1 };
        Assert.AreEqual(Protocol.MaxPositionMs, LeadershipTransferPolicy.AdvancePlaybackEpoch(playing, Now + 2500).PositionMs);
    }
}
