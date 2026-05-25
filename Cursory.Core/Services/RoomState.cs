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
    private RoomVote? activeVote;
    private readonly Lock voteLock = new();
    /// <summary>Total number of seeded levels. Drives the UI dropdown.</summary>
    public const int LevelCount = 14;
    private int currentLevel = 1;
    private readonly Lock levelLock = new();
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

    // ── reset vote ───────────────────────────────────────────────────────────

    /// <summary>
    /// Snapshot of the active vote, or null. Defensive copy so the caller can't mutate it.
    /// </summary>
    public RoomVote? CurrentVote
    {
        get
        {
            lock (voteLock)
            {
                return activeVote is null ? null : CloneVote(activeVote);
            }
        }
    }

    /// <summary>The currently-loaded level (1..LevelCount).</summary>
    public int CurrentLevel
    {
        get { lock (levelLock) return currentLevel; }
    }

    /// <summary>Begin a reset vote. Single-player rooms pass immediately.</summary>
    public bool StartResetVote(string userId) =>
        StartVote(userId, VoteKind.Reset, 0);

    /// <summary>Begin a level-switch vote. Target must be in 1..LevelCount.</summary>
    public bool StartLevelVote(string userId, int targetLevel)
    {
        if (targetLevel < 1 || targetLevel > LevelCount) return false;
        if (targetLevel == CurrentLevel) return false;  // no-op votes get rejected
        return StartVote(userId, VoteKind.SelectLevel, targetLevel);
    }

    private bool StartVote(string userId, VoteKind kind, int targetLevel)
    {
        lock (voteLock)
        {
            if (activeVote is not null) return false;
            if (!cursors.ContainsKey(userId)) return false;
            var voters = cursors.Keys.ToList();
            if (voters.Count == 0) return false;
            var quorum = (int)Math.Ceiling(voters.Count * 2.0 / 3.0);
            activeVote = new RoomVote
            {
                Kind = kind,
                TargetLevel = targetLevel,
                StartedAtTick = CurrentTick,
                StartedByUserId = userId,
                Voters = voters,
                YesUserIds = [userId],
                NoUserIds = [],
                Quorum = quorum,
            };
            ResolveVoteIfReady();
            return true;
        }
    }

    /// <summary>
    /// Cast a vote on the active reset. The caller must be in the voter snapshot taken when
    /// the vote started (latecomers don't get a say on the in-progress round). Re-voting
    /// flips your prior choice.
    /// </summary>
    public void CastVote(string userId, bool yes)
    {
        lock (voteLock)
        {
            if (activeVote is null) return;
            if (!activeVote.Voters.Contains(userId)) return;
            activeVote.YesUserIds.Remove(userId);
            activeVote.NoUserIds.Remove(userId);
            if (yes) activeVote.YesUserIds.Add(userId);
            else activeVote.NoUserIds.Add(userId);
            ResolveVoteIfReady();
        }
    }

    private void ResolveVoteIfReady()
    {
        // Must be called under voteLock.
        if (activeVote is null) return;
        if (activeVote.YesUserIds.Count >= activeVote.Quorum)
        {
            ApplyVote(activeVote);
            activeVote = null;
            return;
        }
        // Reject early if YES can no longer reach quorum even with every undecided voter
        // saying yes — saves players from staring at a doomed vote until the timeout.
        var undecided = activeVote.Voters.Count - activeVote.YesUserIds.Count - activeVote.NoUserIds.Count;
        if (activeVote.YesUserIds.Count + undecided < activeVote.Quorum)
        {
            activeVote = null;
        }
    }

    private void ApplyVote(RoomVote v)
    {
        switch (v.Kind)
        {
            case VoteKind.Reset:
                ResetPuzzles();
                break;
            case VoteKind.SelectLevel:
                SwitchToLevel(v.TargetLevel);
                break;
        }
    }

    private void TimeoutVoteIfExpired()
    {
        lock (voteLock)
        {
            if (activeVote is null) return;
            if (CurrentTick - activeVote.StartedAtTick >= RoomVote.TimeoutTicks)
            {
                activeVote = null;
            }
        }
    }

    private static RoomVote CloneVote(RoomVote v) => new()
    {
        Kind = v.Kind,
        TargetLevel = v.TargetLevel,
        StartedAtTick = v.StartedAtTick,
        StartedByUserId = v.StartedByUserId,
        Voters = [..v.Voters],
        YesUserIds = [..v.YesUserIds],
        NoUserIds = [..v.NoUserIds],
        Quorum = v.Quorum,
    };

    private void SwitchToLevel(int level)
    {
        lock (levelLock)
        {
            currentLevel = Math.Clamp(level, 1, LevelCount);
        }
        ResetPuzzles();
        Interlocked.Exchange(ref pendingGeometryRebroadcast, 1);
        Interlocked.Exchange(ref pendingLevelAnnouncement, currentLevel);
    }

    private int pendingGeometryRebroadcast;
    private int pendingLevelAnnouncement;

    /// <summary>
    /// True iff a level switch or reset has happened since the last broadcast. The loop
    /// reads + clears this each tick and, when set, re-sends the Geometry message (walls +
    /// labels change with the level) AND a LevelLoaded event for the banner.
    /// </summary>
    public bool ConsumeGeometryRebroadcast() =>
        Interlocked.Exchange(ref pendingGeometryRebroadcast, 0) == 1;

    /// <summary>
    /// When non-zero, the loop should fire a LevelLoaded event for this level number and
    /// then clear it. Decoupled from the geometry rebroadcast so a pure "Reset" vote (same
    /// level re-seeded) doesn't pop the banner unnecessarily.
    /// </summary>
    public int ConsumeLevelAnnouncement() =>
        Interlocked.Exchange(ref pendingLevelAnnouncement, 0);

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
            Vote = CurrentVote,
            CurrentLevel = CurrentLevel,
            LevelCount = LevelCount,
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
        switch (currentLevel)
        {
            case 1:  SeedLevel1();  break;
            case 2:  SeedLevel2();  break;
            case 3:  SeedLevel3();  break;
            case 4:  SeedLevel4();  break;
            case 5:  SeedLevel5();  break;
            case 6:  SeedLevel6();  break;
            case 7:  SeedLevel7();  break;
            case 8:  SeedLevel8();  break;
            case 9:  SeedLevel9();  break;
            case 10: SeedLevel10(); break;
            case 11: SeedLevel11(); break;
            case 12: SeedLevel12(); break;
            case 13: SeedLevel13(); break;
            case 14: SeedLevel14(); break;
            default: SeedLevel1();  break;
        }
    }

    /// <summary>
    /// Drops every puzzle artefact and re-seeds from scratch. Cursors and whistles are
    /// preserved (the players stay in the room); only level state resets. Called when a
    /// reset vote passes.
    /// </summary>
    internal void ResetPuzzles()
    {
        blocks.Clear();
        goals.Clear();
        walls.Clear();
        switches.Clear();
        doors.Clear();
        shapes.Clear();
        shapeGoals.Clear();
        labels.Clear();
        // Detach every cursor from whatever it was grabbing so the post-reset world
        // doesn't have phantom attachments pointing at deleted ids.
        foreach (var c in cursors.Values)
        {
            c.AttachedBlockId = null;
            c.AttachedShapeId = null;
        }
        SeedPuzzles();
    }

    /// <summary>
    /// Level 1 — "Drop it on the pad". A single light block, a larger goal square.
    /// Friction is low enough that one cursor can drag the block solo; this is the
    /// teaching level. Mass = 2 (very light).
    /// </summary>
    private void SeedLevel1()
    {
        const string blockId = "L1-block";
        blocks[blockId] = new BlockState
        {
            Id = blockId, X = 3500, Y = 5000, W = 140, H = 140,
            Mass = 2, StaticFriction = 0.2, Color = "#D85A30",
        };
        goals["L1-goal"] = new GoalZone
        {
            Id = "L1-goal", X = 6500, Y = 5000, W = 600, H = 600, TargetBlockId = blockId,
        };
        labels["L1-label"] = new WorldLabel
        {
            Id = "L1-label", X = 5000, Y = 3500,
            Title = "Level 1 — Drop it on the pad",
            Subtitle = "Click the small block and drag it into the big square.",
        };
    }

    /// <summary>
    /// Level 2 — "Weigh and pass". A heavy weight-block sits on a pressure pad; a door
    /// stays open while the pad is pressed; a second (light) block has to be dragged
    /// through the now-open door onto the target. The teaching puzzle for the
    /// block-on-switch mechanic: switches count both cursors AND blocks toward their
    /// RequiredCount, so a heavy block planted on the pad holds the door open.
    /// </summary>
    private void SeedLevel2()
    {
        const string weightId = "L2-weight";
        const string passId = "L2-pass";
        const string switchId = "L2-switch";
        const string doorId = "L2-door";

        blocks[weightId] = new BlockState
        {
            Id = weightId, X = 3200, Y = 4400, W = 200, H = 200,
            Mass = 5, StaticFriction = 0.6, Color = "#BA7517",
        };
        blocks[passId] = new BlockState
        {
            Id = passId, X = 3200, Y = 5800, W = 140, H = 140,
            Mass = 2, StaticFriction = 0.2, Color = "#7F77DD",
        };
        switches[switchId] = new SwitchTile
        {
            Id = switchId, X = 4200, Y = 4400, W = 260, H = 260,
            RequiredCount = 1, Color = "#1D9E75",
        };
        // Frame the corridor — the pass-block can't be routed around the door.
        walls["L2-wall-top"]   = new Wall { Id = "L2-wall-top",   X = 5400, Y = 5400, W = 1600, H = 60 };
        walls["L2-wall-bot"]   = new Wall { Id = "L2-wall-bot",   X = 5400, Y = 6200, W = 1600, H = 60 };
        walls["L2-wall-back"]  = new Wall { Id = "L2-wall-back",  X = 4600, Y = 5800, W = 60,   H = 800 };
        doors[doorId] = new Door
        {
            Id = doorId, X = 5400, Y = 5800, W = 60, H = 800,
            RequiredSwitchIds = [switchId],
        };
        goals["L2-goal"] = new GoalZone
        {
            Id = "L2-goal", X = 6400, Y = 5800, W = 400, H = 400, TargetBlockId = passId,
        };
        labels["L2-label"] = new WorldLabel
        {
            Id = "L2-label", X = 4800, Y = 3600,
            Title = "Level 2 — Weigh and pass",
            Subtitle = "Drag the heavy block onto the pad. The door opens. Slide the small block through.",
        };
    }

    /// <summary>
    /// Level 3 — "Pivot the couch". An L-shaped rigid body has to be threaded through a
    /// gap in a wall, like maneuvering a sofa through a doorway. The L's outer envelope
    /// (1 000 × 1 000) is wider than the 800-unit gap; the only way through is to rotate
    /// the L so its 300-thick arm aligns with the gap. Mass = 8, MoI = 400 000 — heavy
    /// enough that one cursor can't move it, two cooperating cursors must coordinate
    /// force and torque to pivot it through. The teaching puzzle for the compound
    /// rigid body with full rotation physics.
    /// </summary>
    private void SeedLevel3()
    {
        walls["L3-wall-top"] = new Wall { Id = "L3-wall-top", X = 5000, Y = 3000, W = 80, H = 4000 };
        walls["L3-wall-bot"] = new Wall { Id = "L3-wall-bot", X = 5000, Y = 7200, W = 80, H = 4000 };

        const string shapeId = "L3-shape";
        var horiz = new ShapePiece { LocalX = 350, LocalY = -150, HalfW = 350, HalfH = 150 };
        var vert  = new ShapePiece { LocalX = -150, LocalY = 350, HalfW = 150, HalfH = 350 };
        shapes[shapeId] = new ShapeActor
        {
            Id = shapeId, X = 3200, Y = 5600,
            Angle = 0, Mass = 8, MomentOfInertia = 400_000,
            StaticFriction = 0.6, RotationalFriction = 60,
            Color = "#D85A30",
            Pieces = [horiz, vert],
        };
        shapeGoals["L3-goal"] = new ShapeGoal
        {
            Id = "L3-goal", X = 7000, Y = 5600, W = 1800, H = 1800,
            TargetShapeId = shapeId,
        };
        labels["L3-label"] = new WorldLabel
        {
            Id = "L3-label", X = 5000, Y = 1800,
            Title = "Level 3 — Pivot the couch",
            Subtitle = "Two cursors. Rotate the L so its arm fits the gap. Thread it through.",
        };
    }

    /// <summary>
    /// Level 4 — "Stand together". A pressure pad in front of a closed door needs two
    /// cursors standing on it to open. While the door is open, both cursors then have to
    /// release the pad and cooperate on dragging a heavy block (mass = 7, friction = 1.1)
    /// through the open passage into the goal — a single cursor can't break friction.
    /// The hard part: keeping the door open while also pulling the block, which forces
    /// timing + role-sharing in a two-player room. Larger pad (RequiredCount = 2) renders
    /// as a circle in the client.
    /// </summary>
    private void SeedLevel4()
    {
        const string switchId = "L4-switch";
        const string blockId = "L4-block";
        const string doorId = "L4-door";

        switches[switchId] = new SwitchTile
        {
            Id = switchId, X = 3500, Y = 5000, W = 320, H = 320,
            RequiredCount = 2, Color = "#7F77DD",
        };
        // Frame the corridor so the block can't be routed around the door.
        walls["L4-wall-top"]  = new Wall { Id = "L4-wall-top",  X = 5800, Y = 4500, W = 2400, H = 60 };
        walls["L4-wall-bot"]  = new Wall { Id = "L4-wall-bot",  X = 5800, Y = 5500, W = 2400, H = 60 };
        walls["L4-wall-back"] = new Wall { Id = "L4-wall-back", X = 4700, Y = 5000, W = 60,   H = 1000 };
        doors[doorId] = new Door
        {
            Id = doorId, X = 5800, Y = 5000, W = 60, H = 1000,
            RequiredSwitchIds = [switchId],
        };
        blocks[blockId] = new BlockState
        {
            Id = blockId, X = 5300, Y = 5000, W = 200, H = 200,
            // Heavy: one cursor's pull (force ≈ k × 50 = 1.25) sits right at the threshold;
            // two cursors cooperating sail past it.
            Mass = 7, StaticFriction = 1.1, Color = "#D4537E",
        };
        goals["L4-goal"] = new GoalZone
        {
            Id = "L4-goal", X = 7000, Y = 5000, W = 700, H = 700, TargetBlockId = blockId,
        };
        labels["L4-label"] = new WorldLabel
        {
            Id = "L4-label", X = 5200, Y = 3500,
            Title = "Level 4 — Stand together, then heave",
            Subtitle = "Both cursors on the pad to open the door. Then both pull the heavy block through.",
        };
    }

    // ── extension puzzles (5..14) ────────────────────────────────────────────
    // Each level is a small focused demonstration of one two-cursor mechanic. The
    // primitives used are: linear force (mass + StaticFriction), torque (offset cursors),
    // cursor-on-switch, block-on-switch, walls, doors, and rotation through narrow gaps.

    /// <summary>Level 5 — "Heavy heave". Single block, friction so high one cursor can't budge it. Two cursors pulling together can.</summary>
    private void SeedLevel5()
    {
        const string b = "L5-block";
        blocks[b] = new BlockState { Id = b, X = 3500, Y = 5000, W = 220, H = 220, Mass = 10, StaticFriction = 2.0, Color = "#3a3a3a" };
        goals["L5-goal"] = new GoalZone { Id = "L5-goal", X = 6500, Y = 5000, W = 500, H = 500, TargetBlockId = b };
        labels["L5-label"] = new WorldLabel { Id = "L5-label", X = 5000, Y = 3500, Title = "Level 5 — Heavy heave", Subtitle = "Both cursors. Pull together. Friction is brutal." };
    }

    /// <summary>Level 6 — "Mirror match". Two blocks, two goals. Cursors split up and move them in parallel.</summary>
    private void SeedLevel6()
    {
        const string b1 = "L6-blockA", b2 = "L6-blockB";
        blocks[b1] = new BlockState { Id = b1, X = 3500, Y = 4000, W = 160, H = 160, Mass = 3, StaticFriction = 0.3, Color = "#7F77DD" };
        blocks[b2] = new BlockState { Id = b2, X = 3500, Y = 6000, W = 160, H = 160, Mass = 3, StaticFriction = 0.3, Color = "#D85A30" };
        goals["L6-goalA"] = new GoalZone { Id = "L6-goalA", X = 6500, Y = 4000, W = 400, H = 400, TargetBlockId = b1 };
        goals["L6-goalB"] = new GoalZone { Id = "L6-goalB", X = 6500, Y = 6000, W = 400, H = 400, TargetBlockId = b2 };
        labels["L6-label"] = new WorldLabel { Id = "L6-label", X = 5000, Y = 3000, Title = "Level 6 — Mirror match", Subtitle = "Two blocks. Two goals. Divide and conquer." };
    }

    /// <summary>Level 7 — "Stand and slide". One cursor holds the switch open, the other slides the block through.</summary>
    private void SeedLevel7()
    {
        const string sw = "L7-switch", b = "L7-block", d = "L7-door";
        switches[sw] = new SwitchTile { Id = sw, X = 3000, Y = 4200, W = 240, H = 240, RequiredCount = 1, Color = "#1D9E75" };
        blocks[b] = new BlockState { Id = b, X = 3500, Y = 5500, W = 160, H = 160, Mass = 2, StaticFriction = 0.2, Color = "#D4537E" };
        walls["L7-top"]  = new Wall { Id = "L7-top",  X = 5400, Y = 5100, W = 2000, H = 60 };
        walls["L7-bot"]  = new Wall { Id = "L7-bot",  X = 5400, Y = 5900, W = 2000, H = 60 };
        walls["L7-back"] = new Wall { Id = "L7-back", X = 4500, Y = 5500, W = 60,   H = 800 };
        doors[d] = new Door { Id = d, X = 5300, Y = 5500, W = 60, H = 800, RequiredSwitchIds = [sw] };
        goals["L7-goal"] = new GoalZone { Id = "L7-goal", X = 6300, Y = 5500, W = 400, H = 400, TargetBlockId = b };
        labels["L7-label"] = new WorldLabel { Id = "L7-label", X = 4800, Y = 3500, Title = "Level 7 — Stand and slide", Subtitle = "One cursor holds the pad. The other slides the block." };
    }

    /// <summary>Level 8 — "Block taxi". Carry a small block onto a switch to hold a door open, then go back for a second block.</summary>
    private void SeedLevel8()
    {
        const string sw = "L8-switch", weight = "L8-weight", pass = "L8-pass", d = "L8-door";
        switches[sw] = new SwitchTile { Id = sw, X = 4000, Y = 4500, W = 240, H = 240, RequiredCount = 1, Color = "#BA7517" };
        blocks[weight] = new BlockState { Id = weight, X = 3000, Y = 4500, W = 180, H = 180, Mass = 4, StaticFriction = 0.4, Color = "#BA7517" };
        blocks[pass]   = new BlockState { Id = pass,   X = 3000, Y = 5800, W = 140, H = 140, Mass = 2, StaticFriction = 0.2, Color = "#7F77DD" };
        walls["L8-top"]  = new Wall { Id = "L8-top",  X = 5500, Y = 5400, W = 2200, H = 60 };
        walls["L8-bot"]  = new Wall { Id = "L8-bot",  X = 5500, Y = 6200, W = 2200, H = 60 };
        walls["L8-back"] = new Wall { Id = "L8-back", X = 4500, Y = 5800, W = 60,   H = 800 };
        doors[d] = new Door { Id = d, X = 5400, Y = 5800, W = 60, H = 800, RequiredSwitchIds = [sw] };
        goals["L8-goal"] = new GoalZone { Id = "L8-goal", X = 6500, Y = 5800, W = 400, H = 400, TargetBlockId = pass };
        labels["L8-label"] = new WorldLabel { Id = "L8-label", X = 5000, Y = 3500, Title = "Level 8 — Block taxi", Subtitle = "Park the heavy block on the pad. Door stays open. Now slide the small block." };
    }

    /// <summary>Level 9 — "Couch corner". A long rectangular ShapeActor must be carried around a 90° turn.</summary>
    private void SeedLevel9()
    {
        const string s = "L9-couch";
        // Long couch: 800 × 240, two collinear pieces for richer collision.
        shapes[s] = new ShapeActor
        {
            Id = s, X = 3000, Y = 3500, Angle = 0,
            Mass = 8, MomentOfInertia = 350_000, StaticFriction = 0.6, RotationalFriction = 50,
            Color = "#7F77DD",
            Pieces = [ new ShapePiece { LocalX = 0, LocalY = 0, HalfW = 400, HalfH = 120 } ],
        };
        walls["L9-w1"] = new Wall { Id = "L9-w1", X = 5000, Y = 3000, W = 4000, H = 60 };
        walls["L9-w2"] = new Wall { Id = "L9-w2", X = 5000, Y = 4500, W = 2000, H = 60 };
        walls["L9-w3"] = new Wall { Id = "L9-w3", X = 6200, Y = 6000, W = 60, H = 3000 };
        walls["L9-w4"] = new Wall { Id = "L9-w4", X = 7400, Y = 6000, W = 60, H = 3000 };
        shapeGoals["L9-goal"] = new ShapeGoal { Id = "L9-goal", X = 6800, Y = 7000, W = 1000, H = 1000, TargetShapeId = s };
        labels["L9-label"] = new WorldLabel { Id = "L9-label", X = 5500, Y = 2400, Title = "Level 9 — Couch corner", Subtitle = "Pivot! Around the corner, down the hallway." };
    }

    /// <summary>Level 10 — "Tug steady". A central block has to be pulled to a goal that requires a SUSTAINED net force.</summary>
    private void SeedLevel10()
    {
        const string b = "L10-block";
        blocks[b] = new BlockState { Id = b, X = 5000, Y = 5000, W = 200, H = 200, Mass = 6, StaticFriction = 1.4, Color = "#378ADD" };
        goals["L10-goal"] = new GoalZone { Id = "L10-goal", X = 7500, Y = 5000, W = 500, H = 500, TargetBlockId = b };
        labels["L10-label"] = new WorldLabel { Id = "L10-label", X = 5500, Y = 3500, Title = "Level 10 — Tug steady", Subtitle = "Two cursors pulling the same direction beats friction. Opposite directions cancel — try not to fight your partner." };
    }

    /// <summary>Level 11 — "Two-key lock". Two separated switches must be active simultaneously to open a door.</summary>
    private void SeedLevel11()
    {
        const string s1 = "L11-s1", s2 = "L11-s2", b = "L11-block", d = "L11-door";
        switches[s1] = new SwitchTile { Id = s1, X = 2500, Y = 3500, W = 240, H = 240, RequiredCount = 1, Color = "#1D9E75" };
        switches[s2] = new SwitchTile { Id = s2, X = 2500, Y = 6500, W = 240, H = 240, RequiredCount = 1, Color = "#1D9E75" };
        walls["L11-top"]  = new Wall { Id = "L11-top",  X = 5500, Y = 4500, W = 3000, H = 60 };
        walls["L11-bot"]  = new Wall { Id = "L11-bot",  X = 5500, Y = 5500, W = 3000, H = 60 };
        walls["L11-back"] = new Wall { Id = "L11-back", X = 4500, Y = 5000, W = 60,   H = 1000 };
        doors[d] = new Door { Id = d, X = 5400, Y = 5000, W = 60, H = 1000, RequiredSwitchIds = [s1, s2] };
        blocks[b] = new BlockState { Id = b, X = 4800, Y = 5000, W = 160, H = 160, Mass = 3, StaticFriction = 0.3, Color = "#D4537E" };
        goals["L11-goal"] = new GoalZone { Id = "L11-goal", X = 6500, Y = 5000, W = 400, H = 400, TargetBlockId = b };
        labels["L11-label"] = new WorldLabel { Id = "L11-label", X = 5000, Y = 2500, Title = "Level 11 — Two-key lock", Subtitle = "Both pads. Far apart. One cursor each. (Hint: a block on a pad is also a press.)" };
    }

    /// <summary>Level 12 — "Spinner". A small square shape must be rotated 90° to fit a vertical slot, with very little linear motion required.</summary>
    private void SeedLevel12()
    {
        const string s = "L12-bar";
        shapes[s] = new ShapeActor
        {
            Id = s, X = 3500, Y = 5000, Angle = 0,
            Mass = 5, MomentOfInertia = 100_000, StaticFriction = 0.7, RotationalFriction = 30,
            Color = "#D85A30",
            Pieces = [ new ShapePiece { LocalX = 0, LocalY = 0, HalfW = 350, HalfH = 80 } ],
        };
        // Vertical slot — short, only 240 tall. Bar is 700 long × 160 thick; must be rotated upright to fit.
        walls["L12-l"] = new Wall { Id = "L12-l", X = 5000, Y = 3000, W = 80, H = 3600 };
        walls["L12-r"] = new Wall { Id = "L12-r", X = 5000, Y = 7000, W = 80, H = 3600 };
        shapeGoals["L12-goal"] = new ShapeGoal { Id = "L12-goal", X = 6500, Y = 5000, W = 1200, H = 1200, TargetShapeId = s };
        labels["L12-label"] = new WorldLabel { Id = "L12-label", X = 5000, Y = 2000, Title = "Level 12 — Spinner", Subtitle = "Rotate the bar vertical. Slide it through the slot. Both cursors needed for torque." };
    }

    /// <summary>Level 13 — "Door hold". Two switches: one opens a door, one closes it. Must time the carry.</summary>
    private void SeedLevel13()
    {
        const string sw = "L13-sw", b = "L13-block", d = "L13-door";
        switches[sw] = new SwitchTile { Id = sw, X = 2800, Y = 5000, W = 280, H = 280, RequiredCount = 1, Color = "#1D9E75" };
        walls["L13-top"]  = new Wall { Id = "L13-top",  X = 5500, Y = 4500, W = 3000, H = 60 };
        walls["L13-bot"]  = new Wall { Id = "L13-bot",  X = 5500, Y = 5500, W = 3000, H = 60 };
        walls["L13-back"] = new Wall { Id = "L13-back", X = 4400, Y = 5000, W = 60,   H = 1000 };
        doors[d] = new Door { Id = d, X = 5400, Y = 5000, W = 60, H = 1000, RequiredSwitchIds = [sw] };
        // The block doubles as the "key" — sitting it on the pad holds the door open for itself? No: block stops when it's on the pad. Use a SECOND block as the weight, this block as the passer.
        const string weight = "L13-weight", pass = "L13-pass";
        blocks[weight] = new BlockState { Id = weight, X = 3500, Y = 5000, W = 180, H = 180, Mass = 4, StaticFriction = 0.4, Color = "#BA7517" };
        blocks[pass]   = new BlockState { Id = pass,   X = 3500, Y = 6200, W = 140, H = 140, Mass = 2, StaticFriction = 0.2, Color = "#7F77DD" };
        // Remove the unused single-block helper
        blocks.TryRemove(b, out _);
        goals["L13-goal"] = new GoalZone { Id = "L13-goal", X = 6500, Y = 5000, W = 400, H = 400, TargetBlockId = pass };
        labels["L13-label"] = new WorldLabel { Id = "L13-label", X = 5000, Y = 2500, Title = "Level 13 — Door hold", Subtitle = "Park the weight. Door opens. Slide the other block through before time runs out." };
    }

    /// <summary>Level 14 — "Long thread". An extra-long L-shape; the gap is tighter than Level 3. Pure pivot puzzle.</summary>
    private void SeedLevel14()
    {
        walls["L14-top"] = new Wall { Id = "L14-top", X = 5000, Y = 3200, W = 80, H = 4200 };
        walls["L14-bot"] = new Wall { Id = "L14-bot", X = 5000, Y = 6800, W = 80, H = 4200 };

        const string s = "L14-shape";
        // Tighter L: outer 1200 × 1200, arm thickness 240. Gap is 600 between top and bot walls.
        var horiz = new ShapePiece { LocalX = 480, LocalY = -240, HalfW = 480, HalfH = 120 };
        var vert  = new ShapePiece { LocalX = -240, LocalY = 480, HalfW = 120, HalfH = 480 };
        shapes[s] = new ShapeActor
        {
            Id = s, X = 3000, Y = 5000, Angle = 0,
            Mass = 9, MomentOfInertia = 500_000, StaticFriction = 0.7, RotationalFriction = 70,
            Color = "#D85A30",
            Pieces = [horiz, vert],
        };
        shapeGoals["L14-goal"] = new ShapeGoal { Id = "L14-goal", X = 7200, Y = 5000, W = 2000, H = 2000, TargetShapeId = s };
        labels["L14-label"] = new WorldLabel { Id = "L14-label", X = 5000, Y = 2000, Title = "Level 14 — Long thread", Subtitle = "A meaner L through a meaner gap. Patience and rotation." };
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
        TimeoutVoteIfExpired();
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
            // Cursors count toward activation.
            foreach (var c in cursors.Values)
            {
                if (c.X > s.X - s.W / 2 && c.X < s.X + s.W / 2 &&
                    c.Y > s.Y - s.H / 2 && c.Y < s.Y + s.H / 2)
                    count++;
            }
            // Blocks count too: any block whose AABB overlaps the switch's AABB activates it.
            // This is what powers Level 2's "weight on the pressure pad opens the door"
            // mechanic — a heavy block planted on the switch holds the door open without
            // needing a cursor stationed there.
            foreach (var b in blocks.Values)
            {
                if (AabbOverlap(b.X, b.Y, b.W, b.H, s.X, s.Y, s.W, s.H)) count++;
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
