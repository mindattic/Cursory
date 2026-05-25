namespace Cursory.Core.Models;

/// <summary>
/// One connected player's live state. The server is authoritative — clients send their
/// cursor world-coords as input; the server stamps them into this record and broadcasts.
/// </summary>
public class CursorState
{
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Color { get; set; } = "#7F77DD";
    public string ConnectionId { get; set; } = "";

    /// <summary>World-space x. 0..WORLD_W. Client-reported, server-clamped.</summary>
    public double X { get; set; }
    /// <summary>World-space y. 0..WORLD_H. Client-reported, server-clamped.</summary>
    public double Y { get; set; }

    /// <summary>Server tick at which X/Y was last updated. Used to evict stale ghosts.</summary>
    public long LastInputTick { get; set; }

    /// <summary>Block this cursor is currently attached to, if any. Server-managed.</summary>
    public string? AttachedBlockId { get; set; }
    /// <summary>Anchor in the block's local space (the point under the cursor at grab time).</summary>
    public double AnchorLocalX { get; set; }
    public double AnchorLocalY { get; set; }
}

/// <summary>
/// A draggable rigid body. Sum-of-springs physics: net force = sum over attached cursors of
/// k * (cursor_world - anchor_world). When |net| exceeds StaticFriction, the body accelerates.
/// </summary>
public class BlockState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    public double Mass { get; set; } = 6;
    /// <summary>Static-friction threshold in force units. Net cursor force must exceed this to move.</summary>
    public double StaticFriction { get; set; } = 1.2;
    public string Color { get; set; } = "#3a3a3a";
}

/// <summary>
/// A static rectangle on the world map. Block enters → "solved" event fires.
/// </summary>
public class GoalZone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string TargetBlockId { get; set; } = "";
    public bool IsSolved { get; set; }
}

/// <summary>
/// A "whistle" event: a player clicked at (X, Y). Clients render a ripple + play a tone keyed
/// by Color. Whistles live in a short-lived ring buffer on the server; clients render them
/// for ~600ms and then discard.
/// </summary>
public class Whistle
{
    public string UserId { get; set; } = "";
    public string Color { get; set; } = "#7F77DD";
    public double X { get; set; }
    public double Y { get; set; }
    public long Tick { get; set; }
}

/// <summary>
/// A static rectangular obstacle. Blocks colliding with a wall stop at the wall surface.
/// Cursors pass through walls freely (they're abstract pointers, not bodies).
/// </summary>
public class Wall
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}

/// <summary>
/// A pressure-pad tile. While the number of cursors inside the tile's AABB is at or above
/// <see cref="RequiredCount"/>, IsActive becomes true and stays true for the tick. Cursors
/// only need to be inside — no click required. Connected doors read this state.
/// </summary>
public class SwitchTile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public int RequiredCount { get; set; } = 1;
    public int CursorsInside { get; set; }
    public bool IsActive { get; set; }
    public string Color { get; set; } = "#7F77DD";
}

/// <summary>
/// A door that toggles between solid and pass-through based on a set of switches. The
/// door is open (pass-through) when ALL referenced switches are active simultaneously.
/// Visually rendered like a wall but coloured.
/// </summary>
public class Door
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public List<string> RequiredSwitchIds { get; set; } = [];
    public bool IsOpen { get; set; }
}

/// <summary>
/// Authoritative world snapshot broadcast to every connected client at the tick rate.
/// Keep this lean — at 100 players × 30 ticks/sec this travels a lot.
/// </summary>
public class WorldSnapshot
{
    public long Tick { get; set; }
    public List<CursorState> Cursors { get; set; } = [];
    public List<BlockState> Blocks { get; set; } = [];
    public List<GoalZone> Goals { get; set; } = [];
    public List<Wall> Walls { get; set; } = [];
    public List<SwitchTile> Switches { get; set; } = [];
    public List<Door> Doors { get; set; } = [];
    public List<Whistle> Whistles { get; set; } = [];
}

/// <summary>
/// Compile-time world geometry. The viewport pans over this region.
/// </summary>
public static class WorldGeometry
{
    public const double Width = 10_000;
    public const double Height = 10_000;
}
