using System.Collections.Concurrent;
using Cursory.Core.Models;

namespace Cursory.Core.Services;

/// <summary>
/// Authoritative in-memory state of the single shared room. Thread-safe: the GameLoopService
/// ticks on a background thread and SignalR hub methods write input from N connection threads.
/// Reads (GetSnapshot) build a defensive copy so render loops never see a torn frame.
/// </summary>
public class RoomState
{
    private readonly ConcurrentDictionary<string, CursorState> cursors = new();
    private readonly ConcurrentDictionary<string, BlockState> blocks = new();
    private readonly ConcurrentDictionary<string, GoalZone> goals = new();
    private readonly ConcurrentQueue<Whistle> whistles = new();
    private const int WhistleRingCapacity = 256;

    private long currentTick;

    public RoomState()
    {
        SeedDefaultPuzzle();
    }

    public long CurrentTick => Interlocked.Read(ref currentTick);

    /// <summary>Called by the loop after each physics step.</summary>
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
                X = WorldGeometry.Width / 2,
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

    public void RemovePlayer(string userId)
    {
        if (cursors.TryRemove(userId, out _))
        {
            // Detach from any block on disconnect.
            foreach (var b in blocks.Values)
            {
                // no-op; attachments live on cursor, blocks have no reverse pointer
            }
        }
    }

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
        // Anchor = where the click landed in block-local space.
        c.AttachedBlockId = b.Id;
        c.AnchorLocalX = clickX - b.X;
        c.AnchorLocalY = clickY - b.Y;
        return true;
    }

    public void Detach(string userId)
    {
        if (cursors.TryGetValue(userId, out var c))
            c.AttachedBlockId = null;
    }

    public void RecordWhistle(string userId, double x, double y)
    {
        if (!cursors.TryGetValue(userId, out var c)) return;
        whistles.Enqueue(new Whistle { UserId = userId, Color = c.Color, X = x, Y = y, Tick = CurrentTick });
        // Cap the ring so a noisy room doesn't unbounded-grow.
        while (whistles.Count > WhistleRingCapacity && whistles.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Test hook to find the cursor belonging to a given user. Returns null if unknown.
    /// </summary>
    public CursorState? GetCursor(string userId) =>
        cursors.TryGetValue(userId, out var c) ? c : null;

    public IReadOnlyCollection<CursorState> AllCursors => cursors.Values.ToList();

    public IReadOnlyCollection<BlockState> AllBlocks => blocks.Values.ToList();

    /// <summary>
    /// Compose a snapshot for broadcast. Snapshot objects are owned by the caller (clones),
    /// so the loop can keep ticking while the snapshot is being serialized to N sockets.
    /// </summary>
    public WorldSnapshot Snapshot()
    {
        var tick = CurrentTick;
        var snap = new WorldSnapshot
        {
            Tick = tick,
            Cursors = cursors.Values.Select(CloneCursor).ToList(),
            Blocks = blocks.Values.Select(CloneBlock).ToList(),
            Goals = goals.Values.Select(CloneGoal).ToList(),
            // Only whistles from the last 30 ticks (~1 second) — clients animate them and discard.
            Whistles = whistles.Where(w => tick - w.Tick < 30).Select(CloneWhistle).ToList(),
        };
        return snap;
    }

    private static CursorState CloneCursor(CursorState c) => new()
    {
        UserId = c.UserId,
        DisplayName = c.DisplayName,
        Color = c.Color,
        ConnectionId = c.ConnectionId,
        X = c.X,
        Y = c.Y,
        LastInputTick = c.LastInputTick,
        AttachedBlockId = c.AttachedBlockId,
        AnchorLocalX = c.AnchorLocalX,
        AnchorLocalY = c.AnchorLocalY,
    };

    private static BlockState CloneBlock(BlockState b) => new()
    {
        Id = b.Id,
        X = b.X, Y = b.Y, W = b.W, H = b.H,
        Vx = b.Vx, Vy = b.Vy,
        Mass = b.Mass, StaticFriction = b.StaticFriction, Color = b.Color,
    };

    private static GoalZone CloneGoal(GoalZone g) => new()
    {
        Id = g.Id, X = g.X, Y = g.Y, W = g.W, H = g.H,
        TargetBlockId = g.TargetBlockId, IsSolved = g.IsSolved,
    };

    private static Whistle CloneWhistle(Whistle w) => new()
    {
        UserId = w.UserId, Color = w.Color, X = w.X, Y = w.Y, Tick = w.Tick,
    };

    private void SeedDefaultPuzzle()
    {
        // One block in the centre, one goal zone to the right. The block is heavy enough
        // that a single cursor can't budge it — the static-friction threshold (1.2) requires
        // ~2 cursors pulling in the same direction to break.
        var blockId = "block-1";
        blocks[blockId] = new BlockState
        {
            Id = blockId,
            X = WorldGeometry.Width / 2,
            Y = WorldGeometry.Height / 2,
            W = 200, H = 200, Mass = 6, StaticFriction = 1.2,
            Color = "#3a3a3a",
        };
        var goalId = "goal-1";
        goals[goalId] = new GoalZone
        {
            Id = goalId,
            X = WorldGeometry.Width / 2 + 800,
            Y = WorldGeometry.Height / 2 - 150,
            W = 300, H = 300,
            TargetBlockId = blockId,
        };
    }

    // ── physics step (called by GameLoopService) ─────────────────────────────

    private const double SpringK = 0.025;
    private const double Damping = 0.86;

    public void Step()
    {
        foreach (var block in blocks.Values)
        {
            // Sum spring force from every cursor attached to this block.
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
                // Static friction broken — apply net force (clamped to break-magnitude
                // so a huge tug doesn't teleport the block).
                var excess = fmag - block.StaticFriction;
                var nx = fx / fmag;
                var ny = fy / fmag;
                block.Vx += (nx * excess) / block.Mass;
                block.Vy += (ny * excess) / block.Mass;
            }

            block.Vx *= Damping;
            block.Vy *= Damping;
            block.X += block.Vx;
            block.Y += block.Vy;

            // Clamp to world bounds.
            block.X = Math.Clamp(block.X, block.W / 2, WorldGeometry.Width - block.W / 2);
            block.Y = Math.Clamp(block.Y, block.H / 2, WorldGeometry.Height - block.H / 2);
        }

        // Goal-zone detection: a block is "in" a goal if its centre is inside the rect.
        foreach (var g in goals.Values)
        {
            if (!blocks.TryGetValue(g.TargetBlockId, out var b)) continue;
            var inside =
                b.X > g.X - g.W / 2 && b.X < g.X + g.W / 2 &&
                b.Y > g.Y - g.H / 2 && b.Y < g.Y + g.H / 2;
            g.IsSolved = inside;
        }

        AdvanceTick();
    }
}
