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
    /// <summary>Compound shape (ShapeActor) this cursor is attached to, if any. Mutually exclusive with AttachedBlockId.</summary>
    public string? AttachedShapeId { get; set; }
    /// <summary>Anchor in the body's local space. For blocks (axis-aligned), local = world − block centre.
    /// For shapes, local space is the body frame BEFORE rotation; the world anchor each tick is
    /// (X,Y) + Rotate(anchorLocal, Angle).</summary>
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
/// A static labelled marker drawn in the world — used to title each puzzle area so a
/// fresh player can navigate the 10 000 × 10 000 world by signposts instead of memory.
/// </summary>
public class WorldLabel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; }
    public double Y { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
}

/// <summary>
/// One AABB-in-local-space piece of a compound rigid actor (e.g. one arm of an L). The
/// piece's centre sits at (LocalX, LocalY) in the actor's body-local frame; HalfW/HalfH
/// define the extent. World position is computed each tick by rotating the local centre
/// by the actor's Angle and adding the actor's world centre.
/// </summary>
public class ShapePiece
{
    public double LocalX { get; set; }
    public double LocalY { get; set; }
    public double HalfW { get; set; }
    public double HalfH { get; set; }
}

/// <summary>
/// Compound rigid actor with rotation. The shape is built from one or more <see cref="ShapePiece"/>
/// rectangles in body-local space, so an L is two pieces joined at a corner. Unlike <see cref="BlockState"/>
/// (axis-aligned, no rotation), this carries Angle + AngVel and resolves forces via torque
/// (r × F) so cooperating cursors can rotate the body to thread it through a narrow gap. Collision
/// against walls uses the Separating Axis Theorem against each piece's oriented box.
/// </summary>
public class ShapeActor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Body-frame rotation in radians, anti-clockwise from world x-axis.</summary>
    public double Angle { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    /// <summary>Angular velocity in radians per tick.</summary>
    public double AngVel { get; set; }
    public double Mass { get; set; } = 6;
    /// <summary>Moment of inertia. Higher = harder to rotate. Computed from the pieces.</summary>
    public double MomentOfInertia { get; set; } = 2_000_000;
    public double StaticFriction { get; set; } = 1.2;
    /// <summary>Rotational static-friction threshold. Net torque must exceed this to start rotating.</summary>
    public double RotationalFriction { get; set; } = 200;
    public string Color { get; set; } = "#7F77DD";
    public List<ShapePiece> Pieces { get; set; } = [];
}

/// <summary>
/// A cursor's grab anchor on a compound rigid actor. Anchor lives in body-local coordinates;
/// rotating it by the actor's current Angle and translating by (X, Y) gives the anchor's
/// world position, which is what the spring force pulls toward.
/// </summary>
public class ShapeAttachment
{
    public string UserId { get; set; } = "";
    public string ShapeId { get; set; } = "";
    public double AnchorLocalX { get; set; }
    public double AnchorLocalY { get; set; }
}

/// <summary>
/// Static world geometry sent ONCE per connection (when the client subscribes) instead
/// of on every tick. Walls + Labels never change at runtime, so re-sending them at 30 Hz
/// is wasted bandwidth — at 100 cursors × 30 ticks/sec the savings are non-trivial.
/// </summary>
public class WorldGeometryMessage
{
    public double WorldWidth { get; set; }
    public double WorldHeight { get; set; }
    public List<Wall> Walls { get; set; } = [];
    public List<WorldLabel> Labels { get; set; } = [];
}

/// <summary>
/// A square zone that's "satisfied" when every piece of the target compound shape is
/// inside its bounds. Used by Puzzle E — the L-shape has to be fully threaded into the
/// goal square before the level is solved.
/// </summary>
public class ShapeGoal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string TargetShapeId { get; set; } = "";
    public bool IsSolved { get; set; }
}

/// <summary>
/// Authoritative world snapshot broadcast to every connected client at the tick rate.
/// Carries only state that actually changes from tick to tick. Static geometry (walls,
/// labels) is delivered once via <see cref="WorldGeometryMessage"/>.
/// </summary>
public class WorldSnapshot
{
    public long Tick { get; set; }
    public List<CursorState> Cursors { get; set; } = [];
    public List<BlockState> Blocks { get; set; } = [];
    public List<GoalZone> Goals { get; set; } = [];
    public List<SwitchTile> Switches { get; set; } = [];
    public List<Door> Doors { get; set; } = [];
    public List<ShapeActor> Shapes { get; set; } = [];
    public List<ShapeGoal> ShapeGoals { get; set; } = [];
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
