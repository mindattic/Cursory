using Cursory.Core.Models;
using Cursory.Core.Services;

namespace Cursory.Tests;

/// <summary>
/// Headless solvability harness. Drives two virtual cursors through the real <see cref="RoomState"/>
/// physics — grab the body's edges, follow a route of waypoints toward the goal — and asserts the
/// goal actually solves. This catches "mass too heavy to ever move", "gap too tight to pass", and
/// "goal unreachable" without a browser or two humans.
///
/// Coverage note: the block levels (translation/route puzzles) get a full auto-solve. The
/// rotation/thread shape levels (3, 9, 12, 14) can't be solved by a dumb two-cursor pull (a level
/// like Spinner deliberately jams a straight pull until you rotate the bar), so they only get a
/// weaker "the body is movable and makes real progress toward the goal" check — enough to catch a
/// too-heavy / stuck body, while the rotation itself is left to human play.
/// </summary>
[TestFixture]
public class SolvabilityTests
{
    // Routes for levels whose straight-line path is blocked — waypoints that detour around walls.
    // Levels not listed drive straight at the goal centre.
    private static readonly Dictionary<int, (double X, double Y)[]> Routes = new()
    {
        // L2: over the top of the central wall, then down to the pad.
        [2] = [(4400, 3900), (5800, 3900), (6700, 5000)],
        // L8: below the first jutting wall, above the second, then across to the pad.
        [8] = [(4800, 5650), (5800, 4350), (7200, 5000)],
    };

    [TestCase(1)] [TestCase(2)] [TestCase(4)] [TestCase(5)] [TestCase(6)]
    [TestCase(7)] [TestCase(8)] [TestCase(10)] [TestCase(11)] [TestCase(13)]
    public void Block_level_is_solvable_by_two_cursors(int level)
    {
        var room = BuildRoomAtLevel(level);
        var goals = room.Snapshot().Goals;
        Assert.That(goals, Is.Not.Empty, $"level {level} has no block goal");

        // One goal → both cursors on that block. Two goals → a cursor each.
        var assignments = goals.Count == 1
            ? new[] { (goals[0], "u1"), (goals[0], "u2") }
            : new[] { (goals[0], "u1"), (goals[1], "u2") };

        // Grab the goal-facing (leading) edge so a straight pull doesn't drag the rope back through
        // the body (which the segmented tether would wrap, rotating it). A human can wrangle that;
        // the harness just verifies the body is movable to the goal.
        foreach (var (goal, user) in assignments)
        {
            var b = FindBlock(room, goal.TargetBlockId)!;
            var dir = Math.Sign(goal.X - b.X);
            if (dir == 0) dir = 1;
            Assert.That(room.TryAttach(user, b.Id, b.X + dir * b.W / 2, b.Y), Is.True, $"{user} failed to grab {b.Id}");
            // Start the cursor on the block (a real player's would be) so the solid cursor isn't
            // marooned on the far side of a wall by its arbitrary spawn scatter.
            var cur = room.GetCursor(user)!;
            cur.X = b.X + dir * b.W / 2;
            cur.Y = b.Y;
        }

        var solved = DriveToGoals(room, level, assignments, maxTicks: 4000);
        var diag = string.Join("; ", room.Snapshot().Goals.Select(g =>
        {
            var bb = FindBlock(room, g.TargetBlockId);
            return $"{g.TargetBlockId} at ({bb?.X:F0},{bb?.Y:F0}) ang={bb?.Angle:F2} goal ({g.X:F0},{g.Y:F0}) solved={g.IsSolved}";
        }));
        Assert.That(solved, Is.True, $"level {level} did not solve: {diag}");
    }

    /// <summary>Rotation/thread shape levels: a dumb pull can't finesse them, so just prove the
    /// shape is movable — two cursors make real progress toward the goal (it isn't stuck/too heavy).</summary>
    [TestCase(3)] [TestCase(9)] [TestCase(12)]
    public void Shape_level_body_is_movable(int level)
    {
        var room = BuildRoomAtLevel(level);
        var sg = room.Snapshot().ShapeGoals;
        Assert.That(sg, Is.Not.Empty, $"level {level} has no shape goal");
        var goal = sg[0];
        var shape = FindShape(room, goal.TargetShapeId)!;

        var startDist = Dist(shape.X, shape.Y, goal.X, goal.Y);
        // Grab two ends of the shape's bounding span and pull both toward the goal.
        var (minX, maxX) = (shape.X - 250, shape.X + 250);
        Assert.That(room.TryAttachShape("u1", shape.Id, minX, shape.Y), Is.True);
        Assert.That(room.TryAttachShape("u2", shape.Id, maxX, shape.Y), Is.True);

        for (var i = 0; i < 1500; i++)
        {
            room.SetCursorPosition("u1", goal.X, goal.Y);
            room.SetCursorPosition("u2", goal.X, goal.Y);
            room.Step();
        }
        var s = FindShape(room, goal.TargetShapeId)!;
        var endDist = Dist(s.X, s.Y, goal.X, goal.Y);
        // ≥ 500 px of progress proves the body is movable (not too heavy / not stuck). A puzzle
        // like Spinner then jams the straight pull at the slot until a human rotates it — that's
        // expected, so we don't require it to fully reach the goal here.
        Assert.That(endDist, Is.LessThan(startDist - 500),
            $"level {level} shape barely moved: {startDist:F0} → {endDist:F0} px to goal");
    }

    // ── driver ────────────────────────────────────────────────────────────────

    private static bool DriveToGoals(
        RoomState room, int level, (GoalZone goal, string user)[] assignments, int maxTicks)
    {
        // Per assignment: a waypoint cursor. Shared-block assignments share the same route progress.
        var waypoints = Routes.TryGetValue(level, out var r) ? r : null;
        var idx = new Dictionary<string, int>();
        foreach (var (_, user) in assignments) idx[user] = 0;

        for (var tick = 0; tick < maxTicks; tick++)
        {
            foreach (var (goal, user) in assignments)
            {
                var b = FindBlock(room, goal.TargetBlockId);
                if (b == null) continue;
                var route = waypoints ?? [(goal.X, goal.Y)];
                var i = Math.Min(idx[user], route.Length - 1);
                var (wx, wy) = route[i];
                if (i < route.Length - 1 && Dist(b.X, b.Y, wx, wy) < 250) idx[user] = i + 1;
                room.SetCursorPosition(user, wx, wy);
            }
            room.Step();

            if (room.Snapshot().Goals.All(g => g.IsSolved)) return true;
        }
        return false;
    }

    private static RoomState BuildRoomAtLevel(int level)
    {
        var room = new RoomState();
        room.AddOrUpdatePlayer("u1", "c1", "U1", "#ff0000");
        if (level != 1) room.StartLevelVote("u1", level);   // solo room → quorum 1, resolves at once
        room.AddOrUpdatePlayer("u2", "c2", "U2", "#00ff00"); // second player joins after the switch
        Assert.That(room.CurrentLevel, Is.EqualTo(level));
        return room;
    }

    private static BlockState? FindBlock(RoomState room, string id) =>
        room.AllBlocks.FirstOrDefault(b => b.Id == id);
    private static ShapeActor? FindShape(RoomState room, string id) =>
        room.Snapshot().Shapes.FirstOrDefault(s => s.Id == id);
    private static double Dist(double ax, double ay, double bx, double by) =>
        Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
}
