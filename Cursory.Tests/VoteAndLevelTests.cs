using Cursory.Core.Models;
using Cursory.Core.Services;

namespace Cursory.Tests;

[TestFixture]
public class VoteAndLevelTests
{
    /// <summary>
    /// Single player → quorum = ceil(2/3 × 1) = 1 → initiator's auto-YES passes immediately.
    /// </summary>
    [Test]
    public void Solo_room_reset_vote_passes_immediately()
    {
        var room = new RoomState();
        room.AddOrUpdatePlayer("u1", "conn-1", "U1", "#fff");
        Assert.That(room.StartResetVote("u1"), Is.True);
        Assert.That(room.CurrentVote, Is.Null, "Single-voter quorum should resolve in the same call.");
    }

    /// <summary>
    /// Two-voter room → quorum = ceil(2/3 × 2) = 2 → initiator alone is not enough.
    /// </summary>
    [Test]
    public void Two_voter_reset_needs_both_to_pass()
    {
        var room = new RoomState();
        room.AddOrUpdatePlayer("u1", "c1", "U1", "#fff");
        room.AddOrUpdatePlayer("u2", "c2", "U2", "#000");
        Assert.That(room.StartResetVote("u1"), Is.True);
        var v = room.CurrentVote;
        Assert.That(v, Is.Not.Null);
        Assert.That(v!.Quorum, Is.EqualTo(2));
        Assert.That(v.YesUserIds, Has.Member("u1"));
        // Second YES from u2 reaches quorum and resolves.
        room.CastVote("u2", true);
        Assert.That(room.CurrentVote, Is.Null);
    }

    /// <summary>
    /// A second voter saying NO in a two-player room makes quorum unreachable → vote cancels early.
    /// </summary>
    [Test]
    public void Vote_rejected_when_quorum_unreachable()
    {
        var room = new RoomState();
        room.AddOrUpdatePlayer("u1", "c1", "U1", "#fff");
        room.AddOrUpdatePlayer("u2", "c2", "U2", "#000");
        room.StartResetVote("u1");
        room.CastVote("u2", false);
        Assert.That(room.CurrentVote, Is.Null, "1 YES + 1 NO of 2 voters: quorum 2 is no longer reachable.");
    }

    /// <summary>
    /// A successful level-switch vote moves CurrentLevel and queues geometry rebroadcast + level announcement.
    /// </summary>
    [Test]
    public void Level_switch_vote_moves_current_level()
    {
        var room = new RoomState();
        room.AddOrUpdatePlayer("u1", "c1", "U1", "#fff");
        Assert.That(room.CurrentLevel, Is.EqualTo(1));
        Assert.That(room.StartLevelVote("u1", 2), Is.True);
        Assert.That(room.CurrentLevel, Is.EqualTo(2));
        Assert.That(room.ConsumeGeometryRebroadcast(), Is.True);
        Assert.That(room.ConsumeLevelAnnouncement(), Is.EqualTo(2));
    }

    /// <summary>
    /// A vote whose target is the currently-loaded level is rejected — no-op votes don't
    /// get to stall a real reset attempt.
    /// </summary>
    [Test]
    public void Level_vote_for_current_level_is_rejected()
    {
        var room = new RoomState();
        room.AddOrUpdatePlayer("u1", "c1", "U1", "#fff");
        Assert.That(room.StartLevelVote("u1", 1), Is.False);
    }

    /// <summary>
    /// Out-of-range level targets are rejected.
    /// </summary>
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(99)]
    public void Out_of_range_level_vote_is_rejected(int target)
    {
        var room = new RoomState();
        room.AddOrUpdatePlayer("u1", "c1", "U1", "#fff");
        Assert.That(room.StartLevelVote("u1", target), Is.False);
    }
}
