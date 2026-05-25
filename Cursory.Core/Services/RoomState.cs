using System.Collections.Concurrent;
using Cursory.Core.Models;

namespace Cursory.Core.Services;

/// <summary>
/// Authoritative in-memory state of the single shared room. Thread-safe: the GameLoopService
/// ticks on a background thread and SignalR hub methods write input from N connection threads.
/// Snapshot() builds a defensive copy so render loops never see a torn frame.
/// </summary>
public class RoomState
{
    private readonly ConcurrentDictionary<string, CursorState> cursors = new();
    private readonly ConcurrentDictionary<string, BlockState> blocks = new();
    private readonly ConcurrentDictionary<string, GoalZone> goals = new();
    private readonly ConcurrentDictionary<string, Wall> walls = new();
    private readonly ConcurrentDictionary<string, SwitchTile> switches = new();
    private readonly ConcurrentDictionary<string, Door> doors = new();
    private readonly ConcurrentDictionary<string, WorldLabel> labels = new();
    private readonly ConcurrentQueue<Whistle> whistles = new();
    private const int WhistleRingCapacity = 256;

    private long currentTick;

    public RoomState()
    {
        SeedPuzzles();
    }

    public long CurrentTick => Interlocked.Read(ref currentTick);
    public long AdvanceTick() => Interlocked.Increment(ref currentTick);

    // ── cursor registration ──────────────────────────────────────────────────

    public CursorState AddOrUpdatePlayer(string userId, string connectionId, string displayName, string color)
    {
        return cursors.AddOrUpdate(
            userId,
            _ => new CursorState
            {
                UserId = userId,
                ConnectionId = connectionId,
                DisplayName = displayName,
                Color = color,
                X = 1500,
                Y = WorldGeometry.Height / 2,
                LastInputTick = CurrentTick,
            },
            (_, existing) =>
            {
                existing.ConnectionId = connectionId;
                existing.DisplayName = displayName;
                existing.Color = color;
                existing.LastInputTick = CurrentTick;
                return existing;
            });
    }

    public void RemovePlayer(string userId) => cursors.TryRemove(userId, out _);

    public void SetCursorPosition(string userId, double x, double y)
    {
        if (!cursors.TryGetValue(userId, out var c)) return;
        c.X = Math.Clamp(x, 0, WorldGeometry.Width);
        c.Y = Math.Clamp(y, 0, WorldGeometry.Height);
        c.LastInputTick = CurrentTick;
    }

    public bool TryAttach(string userId, string blockId, double clickX, double clickY)
    {
        if (!cursors.TryGetValue(userId, out var c)) return false;
        if (!blocks.TryGetValue(blockId, out var b)) return false;
        c.AttachedBlockId = b.Id;
        c.AnchorLocalX = clickX - b.X;
        c.AnchorLocalY = clickY - b.Y;
        return true;
    }

    public void Detach(string userId)
    {
        if (cursors.TryGetValue(userId, out var c)) c.AttachedBlockId = null;
    }

    public void RecordWhistle(string userId, double x, double y)
    {
        if (!cursors.TryGetValue(userId, out var c)) return;
        whistles.Enqueue(new Whistle { UserId = userId, Color = c.Color, X = x, Y = y, Tick = CurrentTick });
        while (whistles.Count > WhistleRingCapacity && whistles.TryDequeue(out _)) { }
    }

    public CursorState? GetCursor(string userId) =>
        cursors.TryGetValue(userId, out var c) ? c : null;

    public IReadOnlyCollection<CursorState> AllCursors => cursors.Values.ToList();
    public IReadOnlyCollection<BlockState> AllBlocks => blocks.Values.ToList();
    public IReadOnlyCollection<Wall> AllWalls => walls.Values.ToList();
    public IReadOnlyCollection<SwitchTile> AllSwitches => switches.Values.ToList();
    public IReadOnlyCollection<Door> AllDoors => doors.Values.ToList();

    /// <summary>Test/seed helper: directly add a wall to the world.</summary>
    public void AddWall(Wall w) => walls[w.Id] = w;
    /// <summary>Test/seed helper: directly add a block.</summary>
    public void AddBlock(BlockState b) => blocks[b.Id] = b;
    /// <summary>Test/seed helper: directly add a switch.</summary>
    public void AddSwitch(SwitchTile s) => switches[s.Id] = s;
    /// <summary>Test/seed helper: directly add a door.</summary>
    public void AddDoor(Door d) => doors[d.Id] = d;
    /// <summary>Test/seed helper: directly add a goal zone.</summary>
    public void AddGoal(GoalZone g) => goals[g.Id] = g;
    /// <summary>Test/seed helper: directly add a world label.</summary>
    public void AddLabel(WorldLabel l) => labels[l.Id] = l;
    /// <summary>Test helper: register a cursor with explicit position.</summary>
    public void AddTestCursor(string userId, double x, double y, string color = "#7F77DD")
    {
        cursors[userId] = new CursorState
        {
            UserId = userId,
            DisplayName = userId,
            Color = color,
            X = x,
            Y = y,
        };
    }

    public WorldSnapshot Snapshot()
    {
        var tick = CurrentTick;
        return new WorldSnapshot
        {
            Tick = tick,
            Cursors = cursors.Values.Select(CloneCursor).ToList(),
            Blocks = blocks.Values.Select(CloneBlock).ToList(),
            Goals = goals.Values.Select(CloneGoal).ToList(),
            Walls = walls.Values.Select(CloneWall).ToList(),
            Switches = switches.Values.Select(CloneSwitch).ToList(),
            Doors = doors.Values.Select(CloneDoor).ToList(),
            Labels = labels.Values.Select(CloneLabel).ToList(),
            Whistles = whistles.Where(w => tick - w.Tick < 30).Select(CloneWhistle).ToList(),
        };
    }

    private static CursorState CloneCursor(CursorState c) => new()
    {
        UserId = c.UserId, DisplayName = c.DisplayName, Color = c.Color, ConnectionId = c.ConnectionId,
        X = c.X, Y = c.Y, LastInputTick = c.LastInputTick,
        AttachedBlockId = c.AttachedBlockId, AnchorLocalX = c.AnchorLocalX, AnchorLocalY = c.AnchorLocalY,
    };
    private static BlockState CloneBlock(BlockState b) => new()
    {
        Id = b.Id, X = b.X, Y = b.Y, W = b.W, H = b.H,
        Vx = b.Vx, Vy = b.Vy, Mass = b.Mass, StaticFriction = b.StaticFriction, Color = b.Color,
    };
    private static GoalZone CloneGoal(GoalZone g) => new()
    {
        Id = g.Id, X = g.X, Y = g.Y, W = g.W, H = g.H,
        TargetBlockId = g.TargetBlockId, IsSolved = g.IsSolved,
    };
    private static Wall CloneWall(Wall w) => new() { Id = w.Id, X = w.X, Y = w.Y, W = w.W, H = w.H };
    private static SwitchTile CloneSwitch(SwitchTile s) => new()
    {
        Id = s.Id, X = s.X, Y = s.Y, W = s.W, H = s.H,
        RequiredCount = s.RequiredCount, CursorsInside = s.CursorsInside, IsActive = s.IsActive, Color = s.Color,
    };
    private static Door CloneDoor(Door d) => new()
    {
        Id = d.Id, X = d.X, Y = d.Y, W = d.W, H = d.H,
        RequiredSwitchIds = [..d.RequiredSwitchIds], IsOpen = d.IsOpen,
    };
    private static WorldLabel CloneLabel(WorldLabel l) => new()
    {
        Id = l.Id, X = l.X, Y = l.Y, Title = l.Title, Subtitle = l.Subtitle,
    };
    private static Whistle CloneWhistle(Whistle w) => new()
    {
        UserId = w.UserId, Color = w.Color, X = w.X, Y = w.Y, Tick = w.Tick,
    };

    // ── world layout ─────────────────────────────────────────────────────────

    private void SeedPuzzles()
    {
        SeedPuzzleA();
        SeedPuzzleB();
        SeedPuzzleC();
        SeedPuzzleD();
    }

    /// <summary>
    /// Puzzle A — "Heave-ho": a single heavy block, one goal zone, no obstacles.
    /// Friction threshold is set so one cursor can't budge it; two pulling the same way can.
    /// The teaching puzzle: shows the sum-of-forces mechanic with the minimum surface area.
    /// </summary>
    private void SeedPuzzleA()
    {
        const string blockId = "block-A";
        blocks[blockId] = new BlockState
        {
            Id = blockId, X = 2000, Y = 5000, W = 220, H = 220,
            Mass = 6, StaticFriction = 1.2, Color = "#3a3a3a",
        };
        var goalId = "goal-A";
        goals[goalId] = new GoalZone
        {
            Id = goalId, X = 3400, Y = 5000, W = 320, H = 320, TargetBlockId = blockId,
        };
        labels["label-A"] = new WorldLabel
        {
            Id = "label-A", X = 2700, Y = 4500,
            Title = "A — Heave-ho",
            Subtitle = "One block, two cursors needed.",
        };
    }

    /// <summary>
    /// Puzzle B — "Hold the gate": two pressure pads, each requiring one cursor on it; the
    /// gate between them opens only when both pads are pressed simultaneously. A third
    /// cursor (or one of the first two, before the pads are pressed) drags the block through.
    /// The teaching puzzle for switches + doors. Three-cursor coordination minimum.
    /// </summary>
    private void SeedPuzzleB()
    {
        const string switchAId = "switch-B1";
        const string switchBId = "switch-B2";
        switches[switchAId] = new SwitchTile
        {
            Id = switchAId, X = 4800, Y = 4400, W = 220, H = 220,
            RequiredCount = 1, Color = "#1D9E75",
        };
        switches[switchBId] = new SwitchTile
        {
            Id = switchBId, X = 4800, Y = 5600, W = 220, H = 220,
            RequiredCount = 1, Color = "#1D9E75",
        };
        // The corridor walls leading to the gate.
        walls["wall-B-top"]    = new Wall { Id = "wall-B-top",    X = 5300, Y = 4400, W = 600, H = 80 };
        walls["wall-B-bot"]    = new Wall { Id = "wall-B-bot",    X = 5300, Y = 5600, W = 600, H = 80 };
        walls["wall-B-back"]   = new Wall { Id = "wall-B-back",   X = 5000, Y = 5000, W = 80,  H = 800 };
        // The gated door — open when both switches are pressed.
        doors["door-B"] = new Door
        {
            Id = "door-B", X = 5600, Y = 5000, W = 80, H = 800,
            RequiredSwitchIds = [switchAId, switchBId],
        };
        // The block that has to make it through, and its goal beyond the door.
        const string blockId = "block-B";
        blocks[blockId] = new BlockState
        {
            Id = blockId, X = 5300, Y = 5000, W = 140, H = 140,
            Mass = 4, StaticFriction = 0.6, Color = "#D85A30",
        };
        goals["goal-B"] = new GoalZone
        {
            Id = "goal-B", X = 6200, Y = 5000, W = 280, H = 280, TargetBlockId = blockId,
        };
        labels["label-B"] = new WorldLabel
        {
            Id = "label-B", X = 5300, Y = 4200,
            Title = "B — Hold the gate",
            Subtitle = "Two pads. Open the door. Push the block through.",
        };
    }

    /// <summary>
    /// Puzzle C — "The slot": a corridor maze. The block has to be pulled through two doglegs
    /// before reaching its goal. Pulls work better than pushes — the cursor anchor stays on the
    /// leading edge of the block, so the team learns to navigate around corners cooperatively.
    /// No switches: the difficulty is geometric, not gated.
    /// </summary>
    private void SeedPuzzleC()
    {
        // The maze walls (a serpentine corridor).
        walls["wall-C1"] = new Wall { Id = "wall-C1", X = 7600, Y = 3500, W = 1200, H = 60 };
        walls["wall-C2"] = new Wall { Id = "wall-C2", X = 7600, Y = 4500, W = 1200, H = 60 };
        walls["wall-C3"] = new Wall { Id = "wall-C3", X = 7000, Y = 4000, W = 60,  H = 940 };
        walls["wall-C4"] = new Wall { Id = "wall-C4", X = 8200, Y = 4000, W = 60,  H = 940 };
        walls["wall-C5"] = new Wall { Id = "wall-C5", X = 7900, Y = 5500, W = 660,  H = 60 };
        walls["wall-C6"] = new Wall { Id = "wall-C6", X = 7250, Y = 5500, W = 60,  H = 1060 };
        walls["wall-C7"] = new Wall { Id = "wall-C7", X = 8550, Y = 5000, W = 60,  H = 1060 };

        const string blockId = "block-C";
        blocks[blockId] = new BlockState
        {
            Id = blockId, X = 7600, Y = 4000, W = 120, H = 120,
            Mass = 3, StaticFriction = 0.5, Color = "#7F77DD",
        };
        goals["goal-C"] = new GoalZone
        {
            Id = "goal-C", X = 7900, Y = 6200, W = 240, H = 240, TargetBlockId = blockId,
        };
        labels["label-C"] = new WorldLabel
        {
            Id = "label-C", X = 7600, Y = 3200,
            Title = "C — The slot",
            Subtitle = "Drag the block down through the corridor.",
        };
    }

    /// <summary>
    /// Puzzle D — "Stand together": a single pressure pad that needs two cursors on it
    /// simultaneously to open the door. Teaches the "RequiredCount > 1" pattern with the
    /// minimum surface area, distinct from puzzle B (which uses two separate switches).
    /// </summary>
    private void SeedPuzzleD()
    {
        const string switchId = "switch-D";
        switches[switchId] = new SwitchTile
        {
            Id = switchId, X = 3600, Y = 7800, W = 280, H = 280,
            RequiredCount = 2, Color = "#BA7517",
        };
        // Walls that frame the corridor so the block can't simply be dragged around the door.
        walls["wall-D-top"] = new Wall { Id = "wall-D-top", X = 4500, Y = 7400, W = 1600, H = 60 };
        walls["wall-D-bot"] = new Wall { Id = "wall-D-bot", X = 4500, Y = 8200, W = 1600, H = 60 };
        walls["wall-D-back"] = new Wall { Id = "wall-D-back", X = 5300, Y = 7800, W = 60, H = 800 };
        doors["door-D"] = new Door
        {
            Id = "door-D", X = 4500, Y = 7800, W = 60, H = 800,
            RequiredSwitchIds = [switchId],
        };
        const string blockId = "block-D";
        blocks[blockId] = new BlockState
        {
            Id = blockId, X = 4100, Y = 7800, W = 140, H = 140,
            Mass = 3, StaticFriction = 0.4, Color = "#D4537E",
        };
        goals["goal-D"] = new GoalZone
        {
            Id = "goal-D", X = 5000, Y = 7800, W = 240, H = 240, TargetBlockId = blockId,
        };
        labels["label-D"] = new WorldLabel
        {
            Id = "label-D", X = 4200, Y = 7100,
            Title = "D — Stand together",
            Subtitle = "Two cursors on the same pad. One drags the block through.",
        };
    }

    // ── physics step (called by GameLoopService) ─────────────────────────────

    private const double SpringK = 0.025;
    private const double Damping = 0.86;
    /// <summary>
    /// Per-tick velocity cap. Prevents tunneling: a huge spring force could otherwise
    /// translate the block past a thin wall in a single tick (continuous collision detection
    /// would solve this too, but a velocity cap is dramatically simpler and ample for the
    /// puzzle scales we care about). The cap is well under the thinnest wall thickness (60).
    /// </summary>
    private const double MaxVelocityPerTick = 30;

    public void Step()
    {
        UpdateSwitches();
        UpdateDoors();

        foreach (var block in blocks.Values)
        {
            double fx = 0, fy = 0;
            foreach (var c in cursors.Values)
            {
                if (c.AttachedBlockId != block.Id) continue;
                var anchorWorldX = block.X + c.AnchorLocalX;
                var anchorWorldY = block.Y + c.AnchorLocalY;
                fx += SpringK * (c.X - anchorWorldX);
                fy += SpringK * (c.Y - anchorWorldY);
            }

            var fmag = Math.Sqrt(fx * fx + fy * fy);
            if (fmag > block.StaticFriction)
            {
                var excess = fmag - block.StaticFriction;
                var nx = fx / fmag;
                var ny = fy / fmag;
                block.Vx += (nx * excess) / block.Mass;
                block.Vy += (ny * excess) / block.Mass;
            }

            block.Vx *= Damping;
            block.Vy *= Damping;
            block.Vx = Math.Clamp(block.Vx, -MaxVelocityPerTick, MaxVelocityPerTick);
            block.Vy = Math.Clamp(block.Vy, -MaxVelocityPerTick, MaxVelocityPerTick);

            // Integrate position with wall + door collision resolved per-axis. Per-axis
            // resolution is the canonical way to slide along walls: an x-axis collision
            // only zeroes vx, leaving vy free to keep moving the body, and vice versa.
            MoveBlockAxis(block, dx: block.Vx, dy: 0);
            MoveBlockAxis(block, dx: 0, dy: block.Vy);

            // Clamp to world bounds.
            block.X = Math.Clamp(block.X, block.W / 2, WorldGeometry.Width  - block.W / 2);
            block.Y = Math.Clamp(block.Y, block.H / 2, WorldGeometry.Height - block.H / 2);
        }

        foreach (var g in goals.Values)
        {
            if (!blocks.TryGetValue(g.TargetBlockId, out var b)) { g.IsSolved = false; continue; }
            var inside =
                b.X > g.X - g.W / 2 && b.X < g.X + g.W / 2 &&
                b.Y > g.Y - g.H / 2 && b.Y < g.Y + g.H / 2;
            g.IsSolved = inside;
        }

        AdvanceTick();
    }

    private void MoveBlockAxis(BlockState b, double dx, double dy)
    {
        if (dx == 0 && dy == 0) return;
        var origX = b.X; var origY = b.Y;
        b.X += dx; b.Y += dy;

        // Test against walls and closed doors.
        if (CollidesWithSolid(b))
        {
            b.X = origX; b.Y = origY;
            if (dx != 0) b.Vx = 0;
            if (dy != 0) b.Vy = 0;
        }
    }

    private bool CollidesWithSolid(BlockState b)
    {
        foreach (var w in walls.Values)
            if (AabbOverlap(b.X, b.Y, b.W, b.H, w.X, w.Y, w.W, w.H)) return true;
        foreach (var d in doors.Values)
            if (!d.IsOpen && AabbOverlap(b.X, b.Y, b.W, b.H, d.X, d.Y, d.W, d.H)) return true;
        return false;
    }

    private static bool AabbOverlap(double ax, double ay, double aw, double ah,
                                    double bx, double by, double bw, double bh)
    {
        return ax - aw / 2 < bx + bw / 2 && ax + aw / 2 > bx - bw / 2 &&
               ay - ah / 2 < by + bh / 2 && ay + ah / 2 > by - bh / 2;
    }

    private void UpdateSwitches()
    {
        foreach (var s in switches.Values)
        {
            var count = 0;
            foreach (var c in cursors.Values)
            {
                if (c.X > s.X - s.W / 2 && c.X < s.X + s.W / 2 &&
                    c.Y > s.Y - s.H / 2 && c.Y < s.Y + s.H / 2)
                    count++;
            }
            s.CursorsInside = count;
            s.IsActive = count >= s.RequiredCount;
        }
    }

    private void UpdateDoors()
    {
        foreach (var d in doors.Values)
        {
            if (d.RequiredSwitchIds.Count == 0) { d.IsOpen = false; continue; }
            var allActive = true;
            foreach (var sid in d.RequiredSwitchIds)
            {
                if (!switches.TryGetValue(sid, out var s) || !s.IsActive) { allActive = false; break; }
            }
            d.IsOpen = allActive;
        }
    }
}
