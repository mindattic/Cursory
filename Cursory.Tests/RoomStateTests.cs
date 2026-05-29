using Cursory.Core.Models;
using Cursory.Core.Services;

namespace Cursory.Tests;

/// <summary>
/// Behavioural tests for the Aether-backed <see cref="RoomState"/>. These assert the
/// *mechanics* the puzzles depend on — grab snaps to a corner, friction gates solo drags,
/// cooperation breaks friction, offset pulls rotate the body — not exact engine constants
/// (which get tuned by feel). Objects sit around (CX, CY), far from the seeded puzzles.
/// </summary>
[TestFixture]
public class RoomStateTests
{
    private const double CX = 1500;
    private const double CY = 1500;

    private static BlockState Block(string id, double mass, double friction, double size = 200) => new()
    {
        Id = id, X = CX, Y = CY, W = size, H = size, Mass = mass, StaticFriction = friction,
    };

    /// <summary>A grab snaps to the nearest point on the body's edge, not the interior click —
    /// the click's nearest axis is pushed out to the rim. You grab the edge of an object.</summary>
    [Test]
    public void Grab_anchors_at_nearest_edge()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        room.AddBlock(Block("b", mass: 4, friction: 0.4, size: 260));   // edges at ±130
        // Click off-centre, nearest the right edge (dx 110 → 20 from the right edge).
        room.TryAttach("u1", "b", CX + 110, CY - 100);

        var c = room.GetCursor("u1")!;
        Assert.That(c.AttachedBlockId, Is.EqualTo("b"));
        Assert.That(c.AnchorLocalX, Is.EqualTo(130).Within(1.0), "X snaps out to the nearest (right) edge");
        Assert.That(c.AnchorLocalY, Is.EqualTo(-100).Within(1.0), "Y stays at the click");
    }

    /// <summary>A grab beyond the block's edge clamps to the body rather than anchoring in space.</summary>
    [Test]
    public void Grab_clamps_anchor_to_body()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        room.AddBlock(Block("b", mass: 4, friction: 0.4, size: 260));   // half-extent 130
        room.TryAttach("u1", "b", CX + 500, CY + 500);                  // way outside

        var c = room.GetCursor("u1")!;
        Assert.That(c.AnchorLocalX, Is.EqualTo(130).Within(1.0), "anchor X clamps to +half-width");
        Assert.That(c.AnchorLocalY, Is.EqualTo(130).Within(1.0), "anchor Y clamps to +half-height");
    }

    /// <summary>A grab reports its pull in mass-units, capped at a single grab's ceiling.</summary>
    [Test]
    public void Grab_reports_pull_in_mass_units()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        var b = Block("b", mass: 1, friction: 0.2);
        room.AddBlock(b);
        room.TryAttach("u1", "b", CX, CY);
        room.GetCursor("u1")!.X = CX + 800;            // pull well past the saturation reach
        for (var i = 0; i < 10; i++) room.Step();

        var pull = room.GetCursor("u1")!.PullMass;
        Assert.That(pull, Is.GreaterThan(0));
        Assert.That(pull, Is.LessThanOrEqualTo(RoomState.SingleGrabMaxMass + 1e-6));
    }

    /// <summary>A light, low-friction block can be dragged by a single cursor (the Level 1 feel).</summary>
    [Test]
    public void Single_cursor_moves_light_block()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        var b = Block("b", mass: 1, friction: 0.2);   // mass 1 < single-grab ceiling (1.5)
        room.AddBlock(b);
        room.TryAttach("u1", "b", CX, CY);
        room.GetCursor("u1")!.X = CX + 800;   // pull hard to the right

        for (var i = 0; i < 120; i++) room.Step();
        Assert.That(b.X, Is.GreaterThan(CX + 50), $"light block should follow the cursor, X={b.X}");
    }

    /// <summary>
    /// A heavy, high-friction block can't be moved by one cursor — a single grab's force ceiling
    /// is below the body's friction, so it stays put no matter how far the cursor pulls.
    /// </summary>
    [Test]
    public void Single_cursor_cannot_move_heavy_block()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        var b = Block("b", mass: 10, friction: 2.0, size: 260);
        room.AddBlock(b);
        room.TryAttach("u1", "b", CX, CY);
        room.GetCursor("u1")!.X = CX + 800;

        for (var i = 0; i < 120; i++) room.Step();
        Assert.That(b.X, Is.EqualTo(CX).Within(15), $"one cursor should not budge the heavy block, X={b.X}");
    }

    /// <summary>Two cursors grabbing the same heavy block and pulling together break its friction.</summary>
    [Test]
    public void Two_cursors_together_move_heavy_block()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        room.AddTestCursor("u2", CX, CY);
        var b = Block("b", mass: 2, friction: 2.0, size: 260);   // mass 2: one grab (1.5) can't, two (≤3) can
        room.AddBlock(b);
        // Both grab the same corner, both pull right — forces stack.
        room.TryAttach("u1", "b", CX + 130, CY - 130);
        room.TryAttach("u2", "b", CX + 130, CY - 130);
        room.GetCursor("u1")!.X = CX + 800;
        room.GetCursor("u2")!.X = CX + 800;

        for (var i = 0; i < 180; i++) room.Step();
        Assert.That(b.X, Is.GreaterThan(CX + 30), $"two cursors should drag the heavy block, X={b.X}");
    }

    /// <summary>Two cursors pulling in opposite directions cancel — the block holds position.</summary>
    [Test]
    public void Opposing_cursors_cancel()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        room.AddTestCursor("u2", CX, CY);
        var b = Block("b", mass: 6, friction: 1.0, size: 200);
        room.AddBlock(b);
        room.TryAttach("u1", "b", CX, CY);
        room.TryAttach("u2", "b", CX, CY);
        room.GetCursor("u1")!.X = CX + 600;
        room.GetCursor("u2")!.X = CX - 600;

        for (var i = 0; i < 120; i++) room.Step();
        Assert.That(b.X, Is.EqualTo(CX).Within(20), $"opposing pulls should cancel, X={b.X}");
    }

    /// <summary>
    /// Two cursors on opposite corners pulling in opposite directions form a couple that rotates
    /// the body — the torque mechanic that lets a team pivot a shape through a gap.
    /// </summary>
    [Test]
    public void Offset_opposing_cursors_rotate_block()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        room.AddTestCursor("u2", CX, CY);
        var b = Block("b", mass: 1, friction: 0.6, size: 300);   // corners at ±150
        room.AddBlock(b);
        room.TryAttach("u1", "b", CX - 150, CY - 150);   // top-left
        room.TryAttach("u2", "b", CX + 150, CY + 150);   // bottom-right
        // u1 pulls up, u2 pulls down → couple, ~zero net force, strong torque.
        room.GetCursor("u1")!.Y = CY - 600;
        room.GetCursor("u2")!.Y = CY + 600;

        for (var i = 0; i < 180; i++) room.Step();
        Assert.That(Math.Abs(b.Angle), Is.GreaterThan(0.1), $"expected rotation, Angle={b.Angle}");
    }

    /// <summary>Releasing a grab stops it driving the body.</summary>
    [Test]
    public void Detach_removes_cursor_force()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        var b = Block("b", mass: 1, friction: 0.2);   // light enough for one cursor to get it moving
        room.AddBlock(b);
        room.TryAttach("u1", "b", CX, CY);
        room.GetCursor("u1")!.X = CX + 800;
        for (var i = 0; i < 30; i++) room.Step();

        room.Detach("u1");
        Assert.That(room.GetCursor("u1")!.AttachedBlockId, Is.Null);

        // Yank the cursor far in a brand-new direction (perpendicular to the original drag). A
        // still-attached spring would haul the block after it in Y; a released one won't — the
        // block only coasts out its existing X-momentum, so its Y barely changes.
        var afterY = b.Y;
        room.GetCursor("u1")!.X = CX;
        room.GetCursor("u1")!.Y = CY + 3000;
        for (var i = 0; i < 120; i++) room.Step();
        Assert.That(Math.Abs(b.Y - afterY), Is.LessThan(60),
            $"released block followed the cursor in Y: delta={b.Y - afterY}");
    }

    /// <summary>A compound shape is a real engine body: the grab snaps to a piece edge, and a
    /// light shape follows a single cursor.</summary>
    [Test]
    public void Shape_grab_anchors_on_edge_and_light_shape_moves()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        var s = new ShapeActor
        {
            Id = "s", X = CX, Y = CY, Mass = 1, Color = "#7FBF5A",
            Pieces = [new ShapePiece { LocalX = 0, LocalY = 0, HalfW = 150, HalfH = 50 }],
        };
        room.AddShape(s);
        // Click just inside the top of the bar → anchor snaps up to the top edge (localY = -50).
        room.TryAttachShape("u1", "s", CX + 10, CY - 40);

        var c = room.GetCursor("u1")!;
        Assert.That(c.AttachedShapeId, Is.EqualTo("s"));
        Assert.That(c.AnchorLocalY, Is.EqualTo(-50).Within(1.0), "anchor snaps to the top edge");

        room.GetCursor("u1")!.X = CX + 600;
        for (var i = 0; i < 120; i++) room.Step();
        Assert.That(s.X, Is.GreaterThan(CX + 30), $"light shape should follow the cursor, X={s.X}");
    }

    /// <summary>The leash holds a tethered cursor within the max tether length of its anchor.</summary>
    [Test]
    public void Tethered_cursor_is_leashed_to_max_length()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        var b = Block("b", mass: 5, friction: 1.0, size: 200);   // heavy: won't move, so the anchor stays put
        room.AddBlock(b);
        room.TryAttach("u1", "b", CX + 100, CY);                 // grab the right edge
        room.GetCursor("u1")!.X = CX + 5000;                     // yank far away
        room.Step();

        var c = room.GetCursor("u1")!;
        var dist = Math.Sqrt((c.X - c.AnchorWorldX) * (c.X - c.AnchorWorldX) + (c.Y - c.AnchorWorldY) * (c.Y - c.AnchorWorldY));
        Assert.That(dist, Is.LessThanOrEqualTo(301), $"cursor should be leashed to ~300 px, was {dist}");
    }

    /// <summary>With collision on (the default), a cursor landing inside a wall is nudged out to a
    /// surface — but it isn't swept-blocked, so it can still cross (a point past the wall lands there).</summary>
    [Test]
    public void Cursor_is_nudged_out_of_a_wall_when_collision_on()
    {
        var room = new RoomState { CursorCollision = true };
        room.AddTestCursor("u1", 1500, 2000);
        room.AddWall(new Wall { Id = "w", X = 2000, Y = 2000, W = 400, H = 400 });
        room.SetCursorPosition("u1", 2000, 2000);   // dead centre of the wall
        var c = room.GetCursor("u1")!;
        Assert.That(Math.Abs(c.X - 2000) > 190 || Math.Abs(c.Y - 2000) > 190, Is.True,
            $"cursor should be nudged out of the wall, was ({c.X:F0},{c.Y:F0})");

        room.SetCursorPosition("u1", 2600, 2000);   // a point past the wall — lands there (no sweep block)
        Assert.That(room.GetCursor("u1")!.X, Is.EqualTo(2600).Within(0.5));
    }

    /// <summary>With collision off, the cursor is a free pointer and passes through walls.</summary>
    [Test]
    public void Cursor_passes_through_walls_when_collision_off()
    {
        var room = new RoomState { CursorCollision = false };
        room.AddTestCursor("u1", 1500, 2000);
        room.AddWall(new Wall { Id = "w", X = 2000, Y = 2000, W = 400, H = 400 });
        room.SetCursorPosition("u1", 2000, 2000);   // inside the wall — stays, no collision
        Assert.That(room.GetCursor("u1")!.X, Is.EqualTo(2000).Within(0.5));
    }

    /// <summary>With the segmented tether on, orbiting the cursor around a light body wraps the
    /// tether onto successive corners and spins the body — wrap it like a top.</summary>
    [Test]
    public void Segmented_tether_wrap_spins_the_body()
    {
        var room = new RoomState { SegmentedTether = true };
        room.AddTestCursor("u1", CX, CY);
        var b = Block("b", mass: 1, friction: 0.2, size: 200);   // light + low friction
        room.AddBlock(b);
        room.TryAttach("u1", "b", CX + 100, CY);                 // grab the right edge

        // Orbit the cursor around the body; the contact corner walks around, pulling tangentially.
        for (var i = 0; i < 540; i++)
        {
            var a = i * Math.PI / 180.0;
            room.GetCursor("u1")!.X = CX + Math.Cos(a) * 260;
            room.GetCursor("u1")!.Y = CY + Math.Sin(a) * 260;
            room.Step();
        }
        Assert.That(Math.Abs(b.Angle), Is.GreaterThan(0.3), $"wrapping should spin the body, angle={b.Angle}");
    }

    /// <summary>When the cursor swings behind the body, the rope catches a corner — the tether
    /// becomes a multi-point polyline (anchor → corner → contact) rather than a straight line.</summary>
    [Test]
    public void Segmented_tether_catches_a_corner_behind_the_body()
    {
        var room = new RoomState { SegmentedTether = true };
        room.AddTestCursor("u1", CX, CY);
        var b = Block("b", mass: 6, friction: 3.0, size: 200);   // heavy: holds still while we wrap it
        room.AddBlock(b);
        room.TryAttach("u1", "b", CX + 100, CY);                 // grab the right edge
        room.GetCursor("u1")!.X = CX - 400;                      // swing the cursor behind, to the left
        room.GetCursor("u1")!.Y = CY + 250;
        room.Step();

        // [anchorX, anchorY, cornerX, cornerY, …] — at least one wrapped corner ⇒ ≥ 4 entries.
        Assert.That(room.GetCursor("u1")!.TetherPivots.Count, Is.GreaterThanOrEqualTo(4),
            "rope should catch a corner when the cursor is behind the body");
    }

    /// <summary>NaN / infinite cursor input must not poison the simulation.</summary>
    [Test]
    public void NaN_input_is_dropped()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        var startX = room.GetCursor("u1")!.X;
        room.SetCursorPosition("u1", double.NaN, double.PositiveInfinity);
        Assert.That(room.GetCursor("u1")!.X, Is.EqualTo(startX));
    }

    /// <summary>A cursor silent past the cutoff is evicted on the next sweep.</summary>
    [Test]
    public void EvictStaleCursors_drops_silent_cursors()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        for (var i = 0; i < 200; i++) room.Step();
        var dropped = room.EvictStaleCursors(150);
        Assert.That(dropped, Is.GreaterThanOrEqualTo(1));
        Assert.That(room.GetCursor("u1"), Is.Null);
    }

    /// <summary>Static geometry (labels) rides GeometryMessage, not the tick-rate Snapshot.</summary>
    [Test]
    public void GeometryMessage_includes_default_level_label()
    {
        var room = new RoomState();
        var geom = room.GeometryMessage();
        Assert.That(geom.Labels, Has.Some.Matches<WorldLabel>(l => l.Id == "L1-label"));
    }

    /// <summary>All fourteen levels are engine-backed now.</summary>
    [Test]
    public void LevelCount_is_fourteen()
    {
        Assert.That(RoomState.LevelCount, Is.EqualTo(14));
    }

    /// <summary>Every level seeds without throwing and produces broadcastable geometry — a guard
    /// against a typo'd level (bad piece list, missing body) crashing the room on switch.</summary>
    [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
    [TestCase(8)] [TestCase(9)] [TestCase(10)] [TestCase(11)] [TestCase(12)] [TestCase(13)] [TestCase(14)]
    public void Every_level_seeds_and_steps(int level)
    {
        var room = new RoomState();
        room.AddOrUpdatePlayer("u1", "c1", "U1", "#fff");   // solo room → vote quorum 1, resolves at once
        if (level != 1) Assert.That(room.StartLevelVote("u1", level), Is.True);
        Assert.That(room.CurrentLevel, Is.EqualTo(level));
        Assert.DoesNotThrow(() => { for (var i = 0; i < 3; i++) room.Step(); _ = room.Snapshot(); _ = room.GeometryMessage(); });
    }
}
