using Cursory.Core.Models;
using Cursory.Core.Services;

namespace Cursory.Tests;

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
    [Fact]
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

        Assert.Equal(startX, block.X, precision: 6);
    }

    /// <summary>
    /// Two cursors pulling the same direction stack their forces and break friction.
    /// </summary>
    [Fact]
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
        Assert.True(block.X > CX + 1, $"Expected block to move right but X={block.X}");
    }

    /// <summary>
    /// Two cursors pulling opposite directions cancel each other out. The block stays put.
    /// </summary>
    [Fact]
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
        Assert.Equal(CX, block.X, precision: 0);
    }

    /// <summary>
    /// A wall in the block's path stops the block on the x-axis but leaves the y-axis free.
    /// This is the per-axis sliding behaviour: the block grazes along a wall instead of
    /// snagging.
    /// </summary>
    [Fact]
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
        Assert.True(block.X <= CX + 270, $"Block crossed wall: X={block.X}");
        Assert.True(block.Y > CY + 50, $"Block did not slide on y-axis: Y={block.Y}");
    }

    [Fact]
    public void Switch_activates_when_enough_cursors_inside()
    {
        var room = new RoomState();
        var sw = new SwitchTile { Id = "s", X = CX, Y = CY, W = 100, H = 100, RequiredCount = 1 };
        room.AddSwitch(sw);
        room.AddTestCursor("u1", CX, CY);
        room.Step();
        Assert.True(sw.IsActive);
        Assert.Equal(1, sw.CursorsInside);
    }

    [Fact]
    public void Switch_inactive_with_too_few_cursors()
    {
        var room = new RoomState();
        var sw = new SwitchTile { Id = "s", X = CX, Y = CY, W = 100, H = 100, RequiredCount = 2 };
        room.AddSwitch(sw);
        room.AddTestCursor("u1", CX, CY);
        room.Step();
        Assert.False(sw.IsActive);
        Assert.Equal(1, sw.CursorsInside);
    }

    [Fact]
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
        Assert.False(door.IsOpen);

        room.AddTestCursor("u2", CX + 500, CY);
        room.Step();
        Assert.True(door.IsOpen);
    }

    /// <summary>
    /// A closed door blocks a block; the same door, opened, lets the block pass.
    /// </summary>
    [Fact]
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
        Assert.True(block.X < CX + 270, $"Closed door did not stop block: X={block.X}");

        // Press the switch and pull longer — block should now make it through.
        room.AddTestCursor("u2", CX - 1000, CY);
        for (var i = 0; i < 480; i++) room.Step();
        Assert.True(block.X > CX + 400, $"Open door did not let block through: X={block.X}");
    }

    [Fact]
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
        Assert.True(block.X - afterDetachX < 5, $"Detached cursor still moved block: delta={block.X - afterDetachX}");
    }
}
