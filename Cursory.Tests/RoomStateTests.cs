using Cursory.Core.Models;
using Cursory.Core.Services;

namespace Cursory.Tests;

[TestFixture]
public class RoomStateTests
{
    // Tests place objects around (CX, CY) — far from world bounds and from the seeded puzzles
    // (which live in the 2000-8500 range). The world is 10 000 × 10 000.
    private const double CX = 1500;
    private const double CY = 1500;

    /// <summary>
    /// One cursor under the static-friction threshold can't move a heavy block,
    /// even with many tick iterations. Otherwise puzzle A would be trivially solvable solo.
    /// </summary>
    [Test]
    public void Single_cursor_under_threshold_cannot_move_block()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        var block = new BlockState { Id = "b", X = CX, Y = CY, W = 100, H = 100, Mass = 6, StaticFriction = 1.2 };
        room.AddBlock(block);
        room.TryAttach("u1", "b", CX, CY);
        // Pull 30 units right → force = k * 30 = 0.75, below the 1.2 threshold.
        room.GetCursor("u1")!.X = CX + 30;

        var startX = block.X;
        for (var i = 0; i < 60; i++) room.Step();

        Assert.That(block.X, Is.EqualTo(startX).Within(1e-6));
    }

    /// <summary>
    /// Two cursors pulling the same direction stack their forces and break friction.
    /// </summary>
    [Test]
    public void Two_cursors_pulling_together_move_block()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        room.AddTestCursor("u2", CX, CY);
        var block = new BlockState { Id = "b", X = CX, Y = CY, W = 100, H = 100, Mass = 6, StaticFriction = 1.2 };
        room.AddBlock(block);
        room.TryAttach("u1", "b", CX, CY);
        room.TryAttach("u2", "b", CX, CY);
        // Each cursor pulls 40 units to the right → sum = 80 units → force = 0.025 * 80 = 2.0 > 1.2.
        room.GetCursor("u1")!.X = CX + 40;
        room.GetCursor("u2")!.X = CX + 40;

        for (var i = 0; i < 60; i++) room.Step();
        Assert.That(block.X, Is.GreaterThan(CX + 1), $"Expected block to move right but X={block.X}");
    }

    /// <summary>
    /// Two cursors pulling opposite directions cancel each other out. The block stays put.
    /// </summary>
    [Test]
    public void Opposing_cursors_cancel_and_block_does_not_move()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        room.AddTestCursor("u2", CX, CY);
        var block = new BlockState { Id = "b", X = CX, Y = CY, W = 100, H = 100, Mass = 6, StaticFriction = 1.2 };
        room.AddBlock(block);
        room.TryAttach("u1", "b", CX, CY);
        room.TryAttach("u2", "b", CX, CY);
        room.GetCursor("u1")!.X = CX + 200;
        room.GetCursor("u2")!.X = CX - 200;

        for (var i = 0; i < 60; i++) room.Step();
        Assert.That(block.X, Is.EqualTo(CX).Within(0.5));
    }

    /// <summary>
    /// A wall in the block's path stops the block on the x-axis but leaves the y-axis free.
    /// This is the per-axis sliding behaviour: the block grazes along a wall instead of
    /// snagging.
    /// </summary>
    [Test]
    public void Wall_stops_block_on_collision_axis_only()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        room.AddTestCursor("u2", CX, CY);
        var block = new BlockState { Id = "b", X = CX, Y = CY, W = 100, H = 100, Mass = 4, StaticFriction = 0.4 };
        room.AddBlock(block);
        // Vertical wall 300 units right of the block. Tall enough that the y-pull below
        // can't slip the block past its bottom edge in the test duration.
        room.AddWall(new Wall { Id = "w", X = CX + 300, Y = CY + 1500, W = 60, H = 5000 });
        room.TryAttach("u1", "b", CX, CY);
        room.TryAttach("u2", "b", CX, CY);
        // Pull strongly right + down. The MaxVelocityPerTick cap prevents tunneling
        // through the 60-wide wall.
        room.GetCursor("u1")!.X = CX + 600; room.GetCursor("u1")!.Y = CY + 600;
        room.GetCursor("u2")!.X = CX + 600; room.GetCursor("u2")!.Y = CY + 600;

        for (var i = 0; i < 240; i++) room.Step();
        // Block hit the wall — its right edge cannot pass the wall's left edge (x = CX + 270).
        Assert.That(block.X, Is.LessThanOrEqualTo(CX + 270), $"Block crossed wall: X={block.X}");
        Assert.That(block.Y, Is.GreaterThan(CY + 50), $"Block did not slide on y-axis: Y={block.Y}");
    }

    [Test]
    public void Switch_activates_when_enough_cursors_inside()
    {
        var room = new RoomState();
        var sw = new SwitchTile { Id = "s", X = CX, Y = CY, W = 100, H = 100, RequiredCount = 1 };
        room.AddSwitch(sw);
        room.AddTestCursor("u1", CX, CY);
        room.Step();
        Assert.That(sw.IsActive, Is.True);
        Assert.That(sw.CursorsInside, Is.EqualTo(1));
    }

    [Test]
    public void Switch_inactive_with_too_few_cursors()
    {
        var room = new RoomState();
        var sw = new SwitchTile { Id = "s", X = CX, Y = CY, W = 100, H = 100, RequiredCount = 2 };
        room.AddSwitch(sw);
        room.AddTestCursor("u1", CX, CY);
        room.Step();
        Assert.That(sw.IsActive, Is.False);
        Assert.That(sw.CursorsInside, Is.EqualTo(1));
    }

    [Test]
    public void Door_opens_when_all_required_switches_active()
    {
        var room = new RoomState();
        var s1 = new SwitchTile { Id = "s1", X = CX - 500, Y = CY, W = 100, H = 100, RequiredCount = 1 };
        var s2 = new SwitchTile { Id = "s2", X = CX + 500, Y = CY, W = 100, H = 100, RequiredCount = 1 };
        room.AddSwitch(s1);
        room.AddSwitch(s2);
        var door = new Door { Id = "d", X = CX, Y = CY, W = 60, H = 200, RequiredSwitchIds = ["s1", "s2"] };
        room.AddDoor(door);

        room.AddTestCursor("u1", CX - 500, CY);
        room.Step();
        Assert.That(door.IsOpen, Is.False);

        room.AddTestCursor("u2", CX + 500, CY);
        room.Step();
        Assert.That(door.IsOpen, Is.True);
    }

    /// <summary>
    /// A closed door blocks a block; the same door, opened, lets the block pass.
    /// </summary>
    [Test]
    public void Closed_door_blocks_passage_and_open_door_does_not()
    {
        var room = new RoomState();
        var sw = new SwitchTile { Id = "s", X = CX - 1000, Y = CY, W = 100, H = 100, RequiredCount = 1 };
        room.AddSwitch(sw);
        var door = new Door { Id = "d", X = CX + 300, Y = CY, W = 60, H = 400, RequiredSwitchIds = ["s"] };
        room.AddDoor(door);

        var block = new BlockState { Id = "b", X = CX, Y = CY, W = 80, H = 80, Mass = 3, StaticFriction = 0.3 };
        room.AddBlock(block);
        room.AddTestCursor("u1", CX, CY);
        room.TryAttach("u1", "b", CX, CY);
        room.GetCursor("u1")!.X = CX + 600;

        // Pull with closed door — block can't get past x = CX + 270 (door left edge - half block).
        for (var i = 0; i < 240; i++) room.Step();
        Assert.That(block.X, Is.LessThan(CX + 270), $"Closed door did not stop block: X={block.X}");

        // Press the switch and pull longer — block should now make it through.
        room.AddTestCursor("u2", CX - 1000, CY);
        for (var i = 0; i < 480; i++) room.Step();
        Assert.That(block.X, Is.GreaterThan(CX + 400), $"Open door did not let block through: X={block.X}");
    }

    /// <summary>
    /// Puzzle D's single-switch / RequiredCount=2 mechanic: one cursor on the pad keeps
    /// the door closed; two cursors on the same pad open it.
    /// </summary>
    [Test]
    public void Single_switch_requires_two_cursors_to_open_door()
    {
        var room = new RoomState();
        var sw = new SwitchTile { Id = "s", X = CX, Y = CY, W = 200, H = 200, RequiredCount = 2 };
        room.AddSwitch(sw);
        var door = new Door { Id = "d", X = CX + 400, Y = CY, W = 60, H = 400, RequiredSwitchIds = ["s"] };
        room.AddDoor(door);

        room.AddTestCursor("u1", CX, CY);
        room.Step();
        Assert.That(door.IsOpen, Is.False);

        room.AddTestCursor("u2", CX, CY);
        room.Step();
        Assert.That(door.IsOpen, Is.True);
    }

    /// <summary>
    /// Static geometry (labels + walls) lives in GeometryMessage, NOT in the tick-rate
    /// Snapshot — sending labels on every tick is wasted bandwidth at 100 cursors × 30 Hz.
    /// </summary>
    [Test]
    public void GeometryMessage_includes_all_seeded_labels_and_walls()
    {
        var room = new RoomState();
        var geom = room.GeometryMessage();
        Assert.That(geom.Labels, Has.Some.Matches<WorldLabel>(l => l.Id == "label-A"));
        Assert.That(geom.Labels, Has.Some.Matches<WorldLabel>(l => l.Id == "label-B"));
        Assert.That(geom.Labels, Has.Some.Matches<WorldLabel>(l => l.Id == "label-C"));
        Assert.That(geom.Labels, Has.Some.Matches<WorldLabel>(l => l.Id == "label-D"));
        Assert.That(geom.Walls, Is.Not.Empty);
    }

    /// <summary>
    /// Belt-and-braces regression: NaN coordinates must not poison the simulation.
    /// </summary>
    [Test]
    public void NaN_input_is_dropped()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        var startX = room.GetCursor("u1")!.X;
        room.SetCursorPosition("u1", double.NaN, double.PositiveInfinity);
        Assert.That(room.GetCursor("u1")!.X, Is.EqualTo(startX));
    }

    /// <summary>
    /// Stale-cursor eviction: a cursor that hasn't sent input for the configured ticks
    /// is dropped on the next eviction sweep.
    /// </summary>
    [Test]
    public void EvictStaleCursors_drops_silent_cursors()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        // Advance the tick counter past the staleness cutoff without any input from u1.
        for (var i = 0; i < 200; i++) room.Step();
        var dropped = room.EvictStaleCursors(150);
        Assert.That(dropped, Is.GreaterThanOrEqualTo(1));
        Assert.That(room.GetCursor("u1"), Is.Null);
    }

    /// <summary>
    /// Puzzle E: a single cursor below the linear friction threshold can't move the L-shape.
    /// Mirrors the Single_cursor_under_threshold_cannot_move_block invariant.
    /// </summary>
    [Test]
    public void Shape_single_cursor_under_threshold_cannot_move_shape()
    {
        var room = new RoomState();
        var s = new ShapeActor
        {
            Id = "s", X = CX, Y = CY, Mass = 8, MomentOfInertia = 400_000,
            StaticFriction = 0.6, RotationalFriction = 60,
            Pieces = [ new ShapePiece { LocalX = 0, LocalY = 0, HalfW = 200, HalfH = 50 } ],
        };
        room.AddShape(s);
        room.AddTestCursor("u1", CX, CY);
        room.TryAttachShape("u1", "s", CX, CY);
        // 20 units of pull → spring force k*20 = 0.5 < 0.6 threshold.
        room.GetCursor("u1")!.X = CX + 20;

        var startX = s.X; var startA = s.Angle;
        for (var i = 0; i < 60; i++) room.Step();
        Assert.That(s.X, Is.EqualTo(startX).Within(0.001));
        Assert.That(s.Angle, Is.EqualTo(startA).Within(0.001));
    }

    /// <summary>
    /// Puzzle E: two cooperating cursors pulling the same direction on opposite ends of
    /// the L produce a strong torque, rotating the shape — the mechanic that lets a team
    /// rotate a body to fit through a gap.
    /// </summary>
    [Test]
    public void Shape_offset_cursors_generate_torque_and_rotate()
    {
        var room = new RoomState();
        var s = new ShapeActor
        {
            Id = "s", X = CX, Y = CY, Mass = 8, MomentOfInertia = 200_000,
            StaticFriction = 0.6, RotationalFriction = 20,
            Pieces = [ new ShapePiece { LocalX = 0, LocalY = 0, HalfW = 300, HalfH = 50 } ],
        };
        room.AddShape(s);
        // Two cursors grab opposite ends of the bar.
        room.AddTestCursor("u1", CX - 250, CY);
        room.AddTestCursor("u2", CX + 250, CY);
        room.TryAttachShape("u1", "s", CX - 250, CY);
        room.TryAttachShape("u2", "s", CX + 250, CY);
        // Pull in opposite y directions — produces a couple (pure torque, near-zero net force).
        room.GetCursor("u1")!.Y = CY + 200;
        room.GetCursor("u2")!.Y = CY - 200;

        for (var i = 0; i < 240; i++) room.Step();
        // The shape rotated noticeably.
        Assert.That(Math.Abs(s.Angle), Is.GreaterThan(0.1), $"Expected rotation, got Angle={s.Angle}");
    }

    /// <summary>
    /// A wall blocks linear shape motion until the body is rotated to fit through a gap.
    /// </summary>
    [Test]
    public void Shape_cannot_translate_through_solid_wall()
    {
        var room = new RoomState();
        var s = new ShapeActor
        {
            Id = "s", X = CX, Y = CY, Mass = 4, MomentOfInertia = 200_000,
            StaticFriction = 0.3, RotationalFriction = 999_999,  // angular lock for this test
            Pieces = [ new ShapePiece { LocalX = 0, LocalY = 0, HalfW = 200, HalfH = 50 } ],
        };
        room.AddShape(s);
        room.AddWall(new Wall { Id = "w", X = CX + 400, Y = CY, W = 60, H = 5000 });
        room.AddTestCursor("u1", CX, CY);
        room.AddTestCursor("u2", CX, CY);
        room.TryAttachShape("u1", "s", CX, CY);
        room.TryAttachShape("u2", "s", CX, CY);
        room.GetCursor("u1")!.X = CX + 600;
        room.GetCursor("u2")!.X = CX + 600;

        for (var i = 0; i < 240; i++) room.Step();
        // Wall left edge at CX+370. Piece right extent in body frame: localX+halfW = 200.
        // Body centre can sit at most at wall_left - piece_extent = CX+370 - 200 = CX+170.
        Assert.That(s.X, Is.LessThanOrEqualTo(CX + 175), $"Shape phased through wall: X={s.X}");
    }

    [Test]
    public void Detach_removes_cursor_force_from_block()
    {
        var room = new RoomState();
        room.AddTestCursor("u1", CX, CY);
        var block = new BlockState { Id = "b", X = CX, Y = CY, W = 100, H = 100, Mass = 6, StaticFriction = 1.2 };
        room.AddBlock(block);
        room.TryAttach("u1", "b", CX, CY);
        room.GetCursor("u1")!.X = CX + 200;
        room.Step();
        room.Detach("u1");
        var afterDetachX = block.X;

        for (var i = 0; i < 60; i++) room.Step();
        Assert.That(block.X - afterDetachX, Is.LessThan(5), $"Detached cursor still moved block: delta={block.X - afterDetachX}");
    }
}
