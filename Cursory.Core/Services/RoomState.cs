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
    private readonly ConcurrentDictionary<string, ShapeActor> shapes = new();
    private readonly ConcurrentDictionary<string, ShapeGoal> shapeGoals = new();
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
                // Deterministic spawn scatter near the warm-up puzzle. Without this, two
                // players joining at the same moment land on the same pixel and the user
                // can't tell their cursor apart from the other player's.
                X = 1500 + (StableHash(userId) % 800) - 400,
                Y = (WorldGeometry.Height / 2) + ((StableHash(userId) / 1000) % 600) - 300,
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

    /// <summary>
    /// Deterministic 31-bit hash of a string. Used for spawn-position scatter so the
    /// same user always lands in the same neighbourhood. Don't use for security.
    /// </summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 5381;
            foreach (var ch in s) h = (h * 33) ^ ch;
            return h & 0x7FFFFFFF;
        }
    }

    public void RemovePlayer(string userId) => cursors.TryRemove(userId, out _);

    public void SetCursorPosition(string userId, double x, double y)
    {
        if (!cursors.TryGetValue(userId, out var c)) return;
        if (!IsFinite(x) || !IsFinite(y)) return;
        c.X = Math.Clamp(x, 0, WorldGeometry.Width);
        c.Y = Math.Clamp(y, 0, WorldGeometry.Height);
        c.LastInputTick = CurrentTick;
    }

    public bool TryAttach(string userId, string blockId, double clickX, double clickY)
    {
        if (!cursors.TryGetValue(userId, out var c)) return false;
        if (!blocks.TryGetValue(blockId, out var b)) return false;
        if (!IsFinite(clickX) || !IsFinite(clickY)) return false;
        // The anchor lives in block-local space — clamp the click to the block's AABB so a
        // hostile client can't claim an anchor 10 000 units off the body and then yank.
        var localX = Math.Clamp(clickX - b.X, -b.W / 2, b.W / 2);
        var localY = Math.Clamp(clickY - b.Y, -b.H / 2, b.H / 2);
        c.AttachedBlockId = b.Id;
        c.AnchorLocalX = localX;
        c.AnchorLocalY = localY;
        return true;
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    public void Detach(string userId)
    {
        if (cursors.TryGetValue(userId, out var c))
        {
            c.AttachedBlockId = null;
            c.AttachedShapeId = null;
        }
    }

    /// <summary>
    /// Attach a cursor to a compound rigid shape. <paramref name="clickX"/> / <paramref name="clickY"/>
    /// are world coordinates; we project them into the shape's body frame (un-rotate, un-translate)
    /// and store that as the anchor. Each tick, the anchor is re-rotated by the current angle so the
    /// spring "follows" the body as it rotates.
    /// </summary>
    public bool TryAttachShape(string userId, string shapeId, double clickX, double clickY)
    {
        if (!cursors.TryGetValue(userId, out var c)) return false;
        if (!shapes.TryGetValue(shapeId, out var s)) return false;
        if (!IsFinite(clickX) || !IsFinite(clickY)) return false;
        // World → body-local: translate by -centre, then rotate by -Angle.
        var dx = clickX - s.X;
        var dy = clickY - s.Y;
        var cos = Math.Cos(-s.Angle);
        var sin = Math.Sin(-s.Angle);
        var localX = dx * cos - dy * sin;
        var localY = dx * sin + dy * cos;
        // Clamp the anchor to the shape's overall AABB so a hostile client can't claim an
        // anchor far from the body. A tighter "must be inside at least one piece" check could
        // be added; the AABB bound is a reasonable safety net.
        var (minX, minY, maxX, maxY) = ShapeBoundsLocal(s);
        c.AttachedBlockId = null;
        c.AttachedShapeId = s.Id;
        c.AnchorLocalX = Math.Clamp(localX, minX, maxX);
        c.AnchorLocalY = Math.Clamp(localY, minY, maxY);
        return true;
    }

    private static (double minX, double minY, double maxX, double maxY) ShapeBoundsLocal(ShapeActor s)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var p in s.Pieces)
        {
            if (p.LocalX - p.HalfW < minX) minX = p.LocalX - p.HalfW;
            if (p.LocalY - p.HalfH < minY) minY = p.LocalY - p.HalfH;
            if (p.LocalX + p.HalfW > maxX) maxX = p.LocalX + p.HalfW;
            if (p.LocalY + p.HalfH > maxY) maxY = p.LocalY + p.HalfH;
        }
        return (minX, minY, maxX, maxY);
    }

    public void RecordWhistle(string userId, double x, double y)
    {
        if (!cursors.TryGetValue(userId, out var c)) return;
        if (!IsFinite(x) || !IsFinite(y)) return;
        var cx = Math.Clamp(x, 0, WorldGeometry.Width);
        var cy = Math.Clamp(y, 0, WorldGeometry.Height);
        whistles.Enqueue(new Whistle { UserId = userId, Color = c.Color, X = cx, Y = cy, Tick = CurrentTick });
        while (whistles.Count > WhistleRingCapacity && whistles.TryDequeue(out _)) { }
    }

    public CursorState? GetCursor(string userId) =>
        cursors.TryGetValue(userId, out var c) ? c : null;

    /// <summary>
    /// Drop cursors that haven't sent input for <paramref name="staleAfterTicks"/> ticks.
    /// Hub OnDisconnectedAsync handles graceful disconnects, but silent network drops
    /// (laptop lid closed, WiFi loss) leave the connection dangling on the server side
    /// for SignalR's keep-alive timeout (~30 s default). This is a faster ghost cleanup —
    /// called from the game loop, runs every tick at O(cursors). At 150 ticks (~5 s) we
    /// drop a stale cursor; SignalR's own cleanup still runs and is harmless if the
    /// cursor is already gone.
    /// </summary>
    public int EvictStaleCursors(long staleAfterTicks)
    {
        var cutoff = CurrentTick - staleAfterTicks;
        var evicted = 0;
        foreach (var c in cursors.Values)
        {
            if (c.LastInputTick < cutoff)
            {
                if (cursors.TryRemove(c.UserId, out _)) evicted++;
            }
        }
        return evicted;
    }

    public IReadOnlyCollection<CursorState> AllCursors => cursors.Values.ToList();
    public IReadOnlyCollection<BlockState> AllBlocks => blocks.Values.ToList();
    public IReadOnlyCollection<Wall> AllWalls => walls.Values.ToList();
    public IReadOnlyCollection<SwitchTile> AllSwitches => switches.Values.ToList();
    public IReadOnlyCollection<Door> AllDoors => doors.Values.ToList();

    /// <summary>Test/seed helper: directly add a wall to the world.</summary>
    internal void AddWall(Wall w) => walls[w.Id] = w;
    /// <summary>Test/seed helper: directly add a block.</summary>
    internal void AddBlock(BlockState b) => blocks[b.Id] = b;
    /// <summary>Test/seed helper: directly add a switch.</summary>
    internal void AddSwitch(SwitchTile s) => switches[s.Id] = s;
    /// <summary>Test/seed helper: directly add a door.</summary>
    internal void AddDoor(Door d) => doors[d.Id] = d;
    /// <summary>Test/seed helper: directly add a goal zone.</summary>
    internal void AddGoal(GoalZone g) => goals[g.Id] = g;
    /// <summary>Test/seed helper: directly add a world label.</summary>
    internal void AddLabel(WorldLabel l) => labels[l.Id] = l;
    /// <summary>Test/seed helper: directly add a compound rigid shape.</summary>
    internal void AddShape(ShapeActor s) => shapes[s.Id] = s;
    /// <summary>Test/seed helper: directly add a shape goal.</summary>
    internal void AddShapeGoal(ShapeGoal g) => shapeGoals[g.Id] = g;
    /// <summary>Test helper: read a shape by id.</summary>
    internal ShapeActor? GetShape(string id) => shapes.TryGetValue(id, out var s) ? s : null;
    /// <summary>Test helper: register a cursor with explicit position.</summary>
    internal void AddTestCursor(string userId, double x, double y, string color = "#7F77DD")
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
            Switches = switches.Values.Select(CloneSwitch).ToList(),
            Doors = doors.Values.Select(CloneDoor).ToList(),
            Shapes = shapes.Values.Select(CloneShape).ToList(),
            ShapeGoals = shapeGoals.Values.Select(CloneShapeGoal).ToList(),
            Whistles = whistles.Where(w => tick - w.Tick < 30).Select(CloneWhistle).ToList(),
        };
    }

    /// <summary>
    /// Static-geometry snapshot delivered once per connection. Walls + labels don't change
    /// at runtime, so they ride this one-shot message instead of the 30 Hz Snapshot stream.
    /// </summary>
    public WorldGeometryMessage GeometryMessage() => new()
    {
        WorldWidth = WorldGeometry.Width,
        WorldHeight = WorldGeometry.Height,
        Walls = walls.Values.Select(CloneWall).ToList(),
        Labels = labels.Values.Select(CloneLabel).ToList(),
    };

    private static CursorState CloneCursor(CursorState c) => new()
    {
        UserId = c.UserId, DisplayName = c.DisplayName, Color = c.Color, ConnectionId = c.ConnectionId,
        X = c.X, Y = c.Y, LastInputTick = c.LastInputTick,
        AttachedBlockId = c.AttachedBlockId, AttachedShapeId = c.AttachedShapeId,
        AnchorLocalX = c.AnchorLocalX, AnchorLocalY = c.AnchorLocalY,
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
    private static ShapeActor CloneShape(ShapeActor s) => new()
    {
        Id = s.Id, X = s.X, Y = s.Y, Angle = s.Angle,
        Vx = s.Vx, Vy = s.Vy, AngVel = s.AngVel,
        Mass = s.Mass, MomentOfInertia = s.MomentOfInertia,
        StaticFriction = s.StaticFriction, RotationalFriction = s.RotationalFriction,
        Color = s.Color,
        Pieces = s.Pieces.Select(p => new ShapePiece
        {
            LocalX = p.LocalX, LocalY = p.LocalY, HalfW = p.HalfW, HalfH = p.HalfH,
        }).ToList(),
    };
    private static ShapeGoal CloneShapeGoal(ShapeGoal g) => new()
    {
        Id = g.Id, X = g.X, Y = g.Y, W = g.W, H = g.H,
        TargetShapeId = g.TargetShapeId, IsSolved = g.IsSolved,
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
        SeedPuzzleE();
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

    /// <summary>
    /// Puzzle E — "Thread the needle": the headline cooperative-rigid-body level.
    /// An L-shape sits on the left, a vertical wall with a narrow gap runs down the middle,
    /// a goal square waits on the right. Two cursors must coordinate force + torque to
    /// rotate and thread the L through the gap, then drag every piece inside the goal.
    /// </summary>
    private void SeedPuzzleE()
    {
        // The two wall segments framing the gap. Gap is 800 wide vertically (y 5200..6000),
        // narrower than the L's outer dimensions (1000 × 1000) but wider than its arm
        // thickness (300), so threading requires rotating the L to align its arm with the gap.
        walls["wall-E-top"]    = new Wall { Id = "wall-E-top",    X = 2500, Y = 3000, W = 80, H = 4000 };
        walls["wall-E-bot"]    = new Wall { Id = "wall-E-bot",    X = 2500, Y = 7200, W = 80, H = 4000 };
        // Frame the corridor so the L can't be routed around the gap.
        walls["wall-E-floor"]  = new Wall { Id = "wall-E-floor",  X = 0,    Y = 9200, W = 5000, H = 80 };
        walls["wall-E-ceil"]   = new Wall { Id = "wall-E-ceil",   X = 0,    Y = 800,  W = 5000, H = 80 };

        const string shapeId = "shape-E";
        // The L is two AABB pieces in body-local space. Centre of the body frame is the
        // inside corner of the L. The horizontal arm extends to the right, the vertical
        // arm extends downward. Outer dimensions ~ 1000 × 1000, arm thickness 300.
        var horiz = new ShapePiece { LocalX = 350, LocalY = -150, HalfW = 350, HalfH = 150 };
        var vert  = new ShapePiece { LocalX = -150, LocalY = 350, HalfW = 150, HalfH = 350 };
        shapes[shapeId] = new ShapeActor
        {
            Id = shapeId, X = 1500, Y = 5600,
            Angle = 0, Mass = 8, MomentOfInertia = 400_000,
            StaticFriction = 0.6, RotationalFriction = 60,
            Color = "#D85A30",
            Pieces = [horiz, vert],
        };
        shapeGoals["shape-goal-E"] = new ShapeGoal
        {
            Id = "shape-goal-E", X = 3800, Y = 5600, W = 1800, H = 1800,
            TargetShapeId = shapeId,
        };
        labels["label-E"] = new WorldLabel
        {
            Id = "label-E", X = 2500, Y = 1200,
            Title = "E — Thread the needle",
            Subtitle = "Rotate the L. Pull it through the gap. Both cursors needed.",
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
        StepShapes();

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

    // ── compound-rigid-body step ─────────────────────────────────────────────

    private const double ShapeAngularDamping = 0.88;
    private const double ShapeLinearDamping  = 0.88;
    private const double ShapeMaxAngVel = 0.06;          // radians per tick
    private const double ShapeMaxLinearVel = 28;          // world units per tick

    /// <summary>
    /// One simulation tick for every compound rigid actor. For each attached cursor we
    /// compute the world-space anchor (rotated body-local), produce a spring force
    /// F = k * (cursor − anchorWorld), sum forces and torques (τ = r × F where r is
    /// anchorWorld − bodyCentre), gate on static friction, integrate linear and angular
    /// motion, and roll back any sub-step that pushed any piece into a wall.
    /// </summary>
    private void StepShapes()
    {
        foreach (var s in shapes.Values)
        {
            double fx = 0, fy = 0, torque = 0;
            foreach (var c in cursors.Values)
            {
                if (c.AttachedShapeId != s.Id) continue;
                // World anchor = body centre + R(angle) · anchorLocal
                var cos = Math.Cos(s.Angle);
                var sin = Math.Sin(s.Angle);
                var anchorWorldX = s.X + c.AnchorLocalX * cos - c.AnchorLocalY * sin;
                var anchorWorldY = s.Y + c.AnchorLocalX * sin + c.AnchorLocalY * cos;
                var fxi = SpringK * (c.X - anchorWorldX);
                var fyi = SpringK * (c.Y - anchorWorldY);
                fx += fxi;
                fy += fyi;
                // 2D cross product: r × F = rx·Fy − ry·Fx
                var rx = anchorWorldX - s.X;
                var ry = anchorWorldY - s.Y;
                torque += rx * fyi - ry * fxi;
            }

            // Linear: net force must beat static friction to budge the body.
            var fmag = Math.Sqrt(fx * fx + fy * fy);
            if (fmag > s.StaticFriction)
            {
                var excess = fmag - s.StaticFriction;
                s.Vx += (fx / fmag) * excess / s.Mass;
                s.Vy += (fy / fmag) * excess / s.Mass;
            }

            // Angular: scale torque to per-tick angular acceleration. Rotational friction
            // models the body resisting rotation until torque magnitude beats the threshold.
            var tmag = Math.Abs(torque);
            if (tmag > s.RotationalFriction)
            {
                var excess = tmag - s.RotationalFriction;
                s.AngVel += Math.Sign(torque) * excess / s.MomentOfInertia;
            }

            s.Vx *= ShapeLinearDamping;
            s.Vy *= ShapeLinearDamping;
            s.AngVel *= ShapeAngularDamping;
            s.Vx = Math.Clamp(s.Vx, -ShapeMaxLinearVel, ShapeMaxLinearVel);
            s.Vy = Math.Clamp(s.Vy, -ShapeMaxLinearVel, ShapeMaxLinearVel);
            s.AngVel = Math.Clamp(s.AngVel, -ShapeMaxAngVel, ShapeMaxAngVel);

            // Try linear move; revert + zero linear velocity if it overlaps a wall.
            var oldX = s.X; var oldY = s.Y;
            s.X += s.Vx; s.Y += s.Vy;
            if (ShapeCollidesWithSolid(s))
            {
                s.X = oldX; s.Y = oldY; s.Vx = 0; s.Vy = 0;
            }
            // Try angular move; revert + zero angular velocity if it overlaps a wall.
            var oldA = s.Angle;
            s.Angle += s.AngVel;
            if (ShapeCollidesWithSolid(s))
            {
                s.Angle = oldA; s.AngVel = 0;
            }

            // Clamp body centre to the world to avoid drift out of bounds.
            s.X = Math.Clamp(s.X, 0, WorldGeometry.Width);
            s.Y = Math.Clamp(s.Y, 0, WorldGeometry.Height);
        }

        // Shape goals: the shape is "inside" iff every piece's world AABB sits entirely
        // inside the goal rectangle. Per-piece test is cheap and matches the player intuition
        // that the whole L has to be in the box, not just its centre.
        foreach (var g in shapeGoals.Values)
        {
            if (!shapes.TryGetValue(g.TargetShapeId, out var s)) { g.IsSolved = false; continue; }
            var allInside = true;
            foreach (var p in s.Pieces)
            {
                var corners = PieceWorldCorners(s, p);
                foreach (var (cx, cy) in corners)
                {
                    if (cx < g.X - g.W / 2 || cx > g.X + g.W / 2 ||
                        cy < g.Y - g.H / 2 || cy > g.Y + g.H / 2)
                    { allInside = false; break; }
                }
                if (!allInside) break;
            }
            g.IsSolved = allInside;
        }
    }

    private bool ShapeCollidesWithSolid(ShapeActor s)
    {
        foreach (var p in s.Pieces)
        {
            var corners = PieceWorldCorners(s, p);
            foreach (var w in walls.Values)
            {
                if (ObbAabbOverlap(corners, w.X, w.Y, w.W, w.H)) return true;
            }
            foreach (var d in doors.Values)
            {
                if (!d.IsOpen && ObbAabbOverlap(corners, d.X, d.Y, d.W, d.H)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// World-space corners of a body-local AABB after the actor's rotation + translation.
    /// </summary>
    private static (double X, double Y)[] PieceWorldCorners(ShapeActor s, ShapePiece p)
    {
        var cos = Math.Cos(s.Angle);
        var sin = Math.Sin(s.Angle);
        var lx = new[] { p.LocalX - p.HalfW, p.LocalX + p.HalfW, p.LocalX + p.HalfW, p.LocalX - p.HalfW };
        var ly = new[] { p.LocalY - p.HalfH, p.LocalY - p.HalfH, p.LocalY + p.HalfH, p.LocalY + p.HalfH };
        var corners = new (double X, double Y)[4];
        for (var i = 0; i < 4; i++)
        {
            corners[i] = (
                s.X + lx[i] * cos - ly[i] * sin,
                s.Y + lx[i] * sin + ly[i] * cos);
        }
        return corners;
    }

    /// <summary>
    /// Separating Axis Theorem between an OBB (given as its four world-space corners) and
    /// a world AABB. Projects both onto each of the OBB's two axes plus each of the AABB's
    /// two axes (which are world x / world y); if any projection interval is disjoint,
    /// the shapes are separated. The 2D OBB has only two unique axes, so four projections
    /// suffice.
    /// </summary>
    private static bool ObbAabbOverlap((double X, double Y)[] corners,
                                       double ax, double ay, double aw, double ah)
    {
        var aabb = new (double X, double Y)[]
        {
            (ax - aw / 2, ay - ah / 2),
            (ax + aw / 2, ay - ah / 2),
            (ax + aw / 2, ay + ah / 2),
            (ax - aw / 2, ay + ah / 2),
        };
        // Axis 0,1 = AABB axes (world x, world y); 2,3 = OBB axes derived from its edges.
        var axes = new (double X, double Y)[]
        {
            (1, 0),
            (0, 1),
            Normalize(corners[1].X - corners[0].X, corners[1].Y - corners[0].Y),
            Normalize(corners[3].X - corners[0].X, corners[3].Y - corners[0].Y),
        };
        foreach (var axis in axes)
        {
            ProjectInterval(corners, axis, out var minA, out var maxA);
            ProjectInterval(aabb,    axis, out var minB, out var maxB);
            if (maxA < minB || maxB < minA) return false;
        }
        return true;
    }

    private static (double X, double Y) Normalize(double x, double y)
    {
        var m = Math.Sqrt(x * x + y * y);
        if (m < 1e-9) return (1, 0);
        return (x / m, y / m);
    }

    private static void ProjectInterval((double X, double Y)[] pts, (double X, double Y) axis,
                                        out double min, out double max)
    {
        min = double.PositiveInfinity;
        max = double.NegativeInfinity;
        foreach (var p in pts)
        {
            var d = p.X * axis.X + p.Y * axis.Y;
            if (d < min) min = d;
            if (d > max) max = d;
        }
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
