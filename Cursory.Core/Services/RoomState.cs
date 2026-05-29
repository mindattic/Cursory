using System.Collections.Concurrent;
using Cursory.Core.Models;
using nkast.Aether.Physics2D.Common;
using nkast.Aether.Physics2D.Dynamics;
using nkast.Aether.Physics2D.Dynamics.Joints;

namespace Cursory.Core.Services;

/// <summary>
/// Authoritative in-memory state of the single shared room, backed by the Aether.Physics2D
/// rigid-body engine (a top-down, gravity-free Box2D). The server owns the simulation: clients
/// send cursor positions + grab/release; the engine decides what bodies do. <see cref="Step"/>
/// advances the world; <see cref="Snapshot"/> builds a defensive copy for broadcast.
///
/// Physics model (the "spike" — two teaching levels while the feel is dialled in):
/// <list type="bullet">
/// <item>Each draggable block is a dynamic body. A <see cref="FrictionJoint"/> to a static
/// ground body gives it top-down dry friction (MaxForce / MaxTorque) — this is what makes a
/// body "too heavy for one cursor".</item>
/// <item>Grabbing a block creates a <see cref="FixedMouseJoint"/> whose body anchor is the
/// point under the cursor (clamped to the body) and whose pull force is capped at
/// <see cref="GrabMaxForce"/>. Two grabs = two joints, so cooperative drag, fulcrum-pivots,
/// and torque fall straight out of the solver.</item>
/// </list>
/// The engine works in metres; the wire format stays in world pixels. We scale at the boundary
/// (<see cref="PixelsPerMeter"/>). All engine access is serialised under <see cref="worldLock"/>
/// because the Aether World is not thread-safe and the game loop + hub threads both touch it.
/// </summary>
public class RoomState
{
    // ── engine ────────────────────────────────────────────────────────────────
    private readonly Lock worldLock = new();
    private readonly World world = new(Vector2.Zero);   // gravity off — top-down
    private Body ground;                                  // FrictionJoint anchor
    private readonly Dictionary<string, Body> bodyByBlock = new();
    private readonly Dictionary<string, Body> bodyByWall = new();
    private readonly Dictionary<string, Body> bodyByShape = new();
    private readonly Dictionary<string, FixedMouseJoint> grabByUser = new();
    /// <summary>Per-grab segmented-tether wrap: the ordered corner indices the rope currently
    /// wraps (into the body's corner list), anchor-first. Empty = straight tether. Paired with
    /// <see cref="contactCorner"/>, the corner the joint is currently anchored at (-1 = the edge
    /// anchor itself).</summary>
    private readonly Dictionary<string, List<int>> wrapStack = new();
    private readonly Dictionary<string, int> contactCorner = new();

    /// <summary>World units (pixels) per simulation metre. 10 000-px world = 100 m; a 140-px
    /// block = 1.4 m — comfortably inside Box2D's tuned range, where raw 10 000-unit coords
    /// would make the solver unstable.</summary>
    private const float PixelsPerMeter = 100f;
    private const float Dt = 1f / 30f;                    // matches GameLoopService tick rate

    // Feel knobs — tuned live by running the room.
    private const float LinearDamping = 1.5f;             // gentle coast-down so bodies settle
    private const float AngularDamping = 2.0f;
    /// <summary>Force ceiling of a single grab, in newtons. Friction above this can't be beaten
    /// by one cursor; two grabs (≤ 2×) can. The whole co-op mechanic lives in this relationship.</summary>
    private const float GrabMaxForce = 60f;
    private const float GrabFrequency = 5f;               // soft-constraint stiffness (Hz)
    private const float GrabDamping = 0.7f;
    /// <summary>Newtons per mass-unit — the single dial that ties everything to the legible
    /// "mass" number. A body's friction (move-threshold) is Mass × this, and a pull's reported
    /// strength is force ÷ this. So a body moves exactly when the pulls on it sum past its Mass.</summary>
    private const float ForcePerMass = 40f;
    /// <summary>Kilograms of inertia per mass-unit. Decoupled from the threshold so a body can be
    /// hefty (slow to accelerate) without changing how many cursors it takes to break free.</summary>
    private const float InertiaKgPerMass = 5f;
    /// <summary>Rotational friction (N·m) as a multiple of a body's linear friction.</summary>
    private const float FrictionTorqueScale = 1.2f;
    /// <summary>A single grab's pull ceiling in mass-units (GrabMaxForce ÷ ForcePerMass). One
    /// cursor can move anything below this; cooperating cursors stack toward N× it.</summary>
    public const double SingleGrabMaxMass = GrabMaxForce / ForcePerMass;
    /// <summary>Tether length (world px) at which a pull saturates to <see cref="SingleGrabMaxMass"/>,
    /// and the hard leash length — a tethered cursor can't get farther than this from its anchor,
    /// so the tether end is exactly where max pull force is in effect. Matches room.js MAX_PULL_PX.</summary>
    private const double MaxPullPx = 300;
    /// <summary>Cursor collision radius (world px). The pointer is a small disc, not a zero-size
    /// point, so a fast 30 Hz frame can't land its centre exactly on a wall seam and slip through;
    /// solids are inflated by this when ejecting. Small enough not to feel "fat".</summary>
    private const double CursorRadius = 10;
    /// <summary>Room-wide toggle for the multi-segment wrapping tether: when on, the rope catches
    /// on a body's corners as the cursor swings behind it, accumulating pivots and spinning the
    /// body when pulled. Off (the default) = a plain straight tether. Settable at runtime (menu
    /// toggle); read on the physics thread, so backed by a volatile field.</summary>
    private volatile bool segmentedTether = false;
    public bool SegmentedTether { get => segmentedTether; set => segmentedTether = value; }
    /// <summary>Room-wide toggle for cursor-vs-wall/shape collision (default on). When on, the
    /// pointer is nudged out of solids (no swept blocking, so it can still cross — it just can't
    /// rest inside one); off = a free pointer that passes through. Volatile (read on the tick).</summary>
    private volatile bool cursorCollision = true;
    public bool CursorCollision { get => cursorCollision; set => cursorCollision = value; }

    // ── state ─────────────────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, CursorState> cursors = new();
    private readonly ConcurrentDictionary<string, BlockState> blocks = new();
    private readonly ConcurrentDictionary<string, GoalZone> goals = new();
    private readonly ConcurrentDictionary<string, Wall> walls = new();
    private readonly ConcurrentDictionary<string, WorldLabel> labels = new();
    private readonly ConcurrentDictionary<string, ShapeActor> shapes = new();
    private readonly ConcurrentDictionary<string, ShapeGoal> shapeGoals = new();
    private readonly ConcurrentDictionary<string, CircuitComponent> components = new();
    private readonly ConcurrentDictionary<string, Terminal> terminals = new();
    private readonly ConcurrentDictionary<string, Wire> wires = new();
    /// <summary>World-px radius within which a released wire end snaps onto a terminal.</summary>
    private const double WireSnapRadius = 90;
    private readonly ConcurrentQueue<Whistle> whistles = new();
    private RoomVote? activeVote;
    private readonly Lock voteLock = new();
    /// <summary>Total number of seeded levels. Drives the UI dropdown. All fourteen are now
    /// engine-backed and tuned for two cooperating players (the old switch/door levels are
    /// re-themed as cooperative geometry — gap-heaves, corridors, two-pad locks — since switches
    /// and doors aren't ported to the engine yet).</summary>
    public const int LevelCount = 14;
    private int currentLevel = 1;
    private readonly Lock levelLock = new();
    private const int WhistleRingCapacity = 256;
    /// <summary>Ticks a whistle stays in the broadcast snapshot (~1 s at 30 Hz). The client
    /// renders its ripple for a hair longer, so a whistle leaves the wire before its local
    /// animation expires — see room.js's whistle de-dup.</summary>
    private const int WhistleBroadcastTicks = 30;

    private long currentTick;

    public RoomState()
    {
        ground = world.CreateBody(Vector2.Zero, 0f, BodyType.Static);
        SeedPuzzles();
    }

    /// <summary>The authoritative tick counter.</summary>
    public long CurrentTick => Interlocked.Read(ref currentTick);
    private long AdvanceTick() => Interlocked.Increment(ref currentTick);

    // ── unit conversion ─────────────────────────────────────────────────────
    private static float ToM(double px) => (float)(px / PixelsPerMeter);
    private static double ToPx(float m) => m * PixelsPerMeter;
    private static Vector2 ToMVec(double x, double y) => new(ToM(x), ToM(y));

    // ── cursor registration ──────────────────────────────────────────────────

    /// <summary>Register a player (or refresh an existing one's connection/identity).</summary>
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
                // Deterministic spawn scatter near the warm-up puzzle so two players who join
                // at the same instant don't land on the same pixel.
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
    /// Deterministic 31-bit hash of a string. Used for spawn-position scatter so the same
    /// user always lands in the same neighbourhood. Don't use for security.
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

    /// <summary>Drop a player and release whatever they were holding.</summary>
    public void RemovePlayer(string userId)
    {
        Detach(userId);
        cursors.TryRemove(userId, out _);
    }

    /// <summary>Client → server cursor input (world coords). Server-clamped; NaN dropped.
    /// Returns false if there is no cursor registered for <paramref name="userId"/> — the
    /// caller can use that to re-register a player whose cursor was evicted while their tab
    /// was backgrounded (rAF throttling stalls the 30 Hz input stream past the stale cutoff).</summary>
    public bool SetCursorPosition(string userId, double x, double y)
    {
        if (!cursors.TryGetValue(userId, out var c)) return false;
        if (!IsFinite(x) || !IsFinite(y)) return true;
        x = Math.Clamp(x, 0, WorldGeometry.Width);
        y = Math.Clamp(y, 0, WorldGeometry.Height);
        // Cursor collision (toggleable): nudge the pointer out of any wall/shape it lands inside.
        // No swept blocking — it can still cross solids (the in-between mouse points just get
        // pushed to the surface), it simply can't rest inside one. Off = free pointer.
        if (cursorCollision)
        {
            (x, y) = ResolveOutOfWalls(x, y);
            (x, y) = ResolveOutOfShapes(x, y);
        }
        c.X = x;
        c.Y = y;
        c.LastInputTick = CurrentTick;
        return true;
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>Push the cursor disc out of any wall it overlaps, along the shallowest axis
    /// (nearest edge). Walls are axis-aligned; the AABB is inflated by <see cref="CursorRadius"/>
    /// so the disc — not just its centre — stays clear. Mirrors room.js.</summary>
    private (double X, double Y) ResolveOutOfWalls(double x, double y)
    {
        foreach (var w in walls.Values)
        {
            var hw = w.W / 2 + CursorRadius;
            var hh = w.H / 2 + CursorRadius;
            var dx = x - w.X;
            var dy = y - w.Y;
            if (Math.Abs(dx) >= hw || Math.Abs(dy) >= hh) continue;   // disc clear of this wall
            if (hw - Math.Abs(dx) < hh - Math.Abs(dy)) x = w.X + (dx >= 0 ? hw : -hw);
            else y = w.Y + (dy >= 0 ? hh : -hh);
        }
        return (x, y);
    }

    /// <summary>Sweep the cursor disc from its previous position to the requested one and stop it
    /// at the first wall surface the path crosses — so a fast frame can't tunnel through a thin
    /// wall between ticks (the static disc ejection only checks the endpoint). Walls are inflated
    /// by <see cref="CursorRadius"/>; returns the earliest contact point, else the target.</summary>
    private (double X, double Y) SweepCursorAgainstWalls(double x0, double y0, double x1, double y1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var bestT = 1.0;
        foreach (var w in walls.Values)
        {
            var minX = w.X - w.W / 2 - CursorRadius;
            var maxX = w.X + w.W / 2 + CursorRadius;
            var minY = w.Y - w.H / 2 - CursorRadius;
            var maxY = w.Y + w.H / 2 + CursorRadius;
            // If we started inside this inflated box, the static eject handles it — don't sweep.
            if (x0 > minX && x0 < maxX && y0 > minY && y0 < maxY) continue;
            if (TrySegmentAabbEntry(x0, y0, dx, dy, minX, minY, maxX, maxY, out var t) && t < bestT)
                bestT = t;
        }
        return (x0 + dx * bestT, y0 + dy * bestT);
    }

    /// <summary>Slab-method segment/AABB entry time. True (with <paramref name="tEnter"/> in
    /// [0,1]) if the segment (x0,y0)→(x0+dx,y0+dy) first enters the box within its length.</summary>
    private static bool TrySegmentAabbEntry(
        double x0, double y0, double dx, double dy,
        double minX, double minY, double maxX, double maxY, out double tEnter)
    {
        tEnter = 0;
        double tmin = 0, tmax = 1;
        if (Math.Abs(dx) < 1e-9)
        {
            if (x0 < minX || x0 > maxX) return false;
        }
        else
        {
            var t1 = (minX - x0) / dx;
            var t2 = (maxX - x0) / dx;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1);
            tmax = Math.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        if (Math.Abs(dy) < 1e-9)
        {
            if (y0 < minY || y0 > maxY) return false;
        }
        else
        {
            var t1 = (minY - y0) / dy;
            var t2 = (maxY - y0) / dy;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1);
            tmax = Math.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        if (tmin < 0 || tmin > 1) return false;
        tEnter = tmin;
        return true;
    }

    /// <summary>Push the cursor disc out of any shape piece it overlaps. Pieces are axis-aligned
    /// boxes in the shape's body frame, so we rotate the point into that frame, do the inflated
    /// AABB ejection there, and rotate back. Applies even to the shape you're holding — with an
    /// edge grab + leash the cursor rides just outside the surface, so collision reads correctly
    /// without fighting the drag (you pull from outside; pushing inward is what gets ejected).</summary>
    private (double X, double Y) ResolveOutOfShapes(double x, double y)
    {
        foreach (var s in shapes.Values)
        {
            var cosN = Math.Cos(-s.Angle);
            var sinN = Math.Sin(-s.Angle);
            var dx = x - s.X;
            var dy = y - s.Y;
            var lx = dx * cosN - dy * sinN;
            var ly = dx * sinN + dy * cosN;
            foreach (var p in s.Pieces)
            {
                var hw = p.HalfW + CursorRadius;
                var hh = p.HalfH + CursorRadius;
                var px = lx - p.LocalX;
                var py = ly - p.LocalY;
                if (Math.Abs(px) >= hw || Math.Abs(py) >= hh) continue;
                if (hw - Math.Abs(px) < hh - Math.Abs(py)) lx = p.LocalX + (px >= 0 ? hw : -hw);
                else ly = p.LocalY + (py >= 0 ? hh : -hh);
                var cosP = Math.Cos(s.Angle);
                var sinP = Math.Sin(s.Angle);
                x = s.X + lx * cosP - ly * sinP;
                y = s.Y + lx * sinP + ly * cosP;
                break;   // resolved against this shape; good enough for the disc
            }
        }
        return (x, y);
    }

    /// <summary>
    /// Grab a block at the point under the cursor (clamped to the body, not snapped to a
    /// corner). A capped-force <see cref="FixedMouseJoint"/> then pulls that point toward the
    /// cursor each tick; the cursor's stored body-local anchor lets the client draw the pull line.
    /// </summary>
    public bool TryAttach(string userId, string blockId, double clickX, double clickY)
    {
        if (!cursors.TryGetValue(userId, out var c)) return false;
        if (!IsFinite(clickX) || !IsFinite(clickY)) return false;
        lock (worldLock)
        {
            if (!blocks.TryGetValue(blockId, out var b)) return false;
            if (!bodyByBlock.TryGetValue(blockId, out var body)) return false;

            DetachLocked(userId);

            // Anchor on the body's edge nearest the cursor — un-rotate the click into the body
            // frame, then project it onto the perimeter. You grab the rim, not the interior; an
            // edge/corner grab gives the body real torque, and where on the edge you grab is the
            // interesting part of the physics.
            var dx = clickX - b.X;
            var dy = clickY - b.Y;
            var cosN = Math.Cos(-b.Angle);
            var sinN = Math.Sin(-b.Angle);
            var (localX, localY) = ProjectToEdge(dx * cosN - dy * sinN, dx * sinN + dy * cosN, b.W / 2, b.H / 2);

            // Rotate the local anchor back into world space for the joint.
            var cosP = Math.Cos(b.Angle);
            var sinP = Math.Sin(b.Angle);
            var anchorWX = b.X + localX * cosP - localY * sinP;
            var anchorWY = b.Y + localX * sinP + localY * cosP;

            var joint = new FixedMouseJoint(body, ToMVec(anchorWX, anchorWY))
            {
                MaxForce = GrabMaxForce,
                Frequency = GrabFrequency,
                DampingRatio = GrabDamping,
            };
            joint.WorldAnchorB = ToMVec(c.X, c.Y);
            world.Add(joint);
            grabByUser[userId] = joint;
            contactCorner[userId] = -1;   // contact starts at the edge anchor (no wrap yet)

            c.AttachedBlockId = blockId;
            c.AttachedShapeId = null;
            c.AnchorLocalX = localX;
            c.AnchorLocalY = localY;
            return true;
        }
    }

    /// <summary>
    /// Grab a compound shape on the edge of whichever piece is nearest the click. Same capped
    /// <see cref="FixedMouseJoint"/> as a block — the shape is a real rigid body, so two cursors
    /// pulling different pieces rotate and translate it together.
    /// </summary>
    public bool TryAttachShape(string userId, string shapeId, double clickX, double clickY)
    {
        if (!cursors.TryGetValue(userId, out var c)) return false;
        if (!IsFinite(clickX) || !IsFinite(clickY)) return false;
        lock (worldLock)
        {
            if (!shapes.TryGetValue(shapeId, out var s)) return false;
            if (!bodyByShape.TryGetValue(shapeId, out var body)) return false;
            DetachLocked(userId);

            // World click → shape body-local frame.
            var cosN = Math.Cos(-s.Angle);
            var sinN = Math.Sin(-s.Angle);
            var dx = clickX - s.X;
            var dy = clickY - s.Y;
            var lx = dx * cosN - dy * sinN;
            var ly = dx * sinN + dy * cosN;

            // Nearest piece (by clamped distance), then snap to that piece's edge.
            ShapePiece? best = null;
            double bestD = double.PositiveInfinity, bcx = 0, bcy = 0;
            foreach (var p in s.Pieces)
            {
                var cx = Math.Clamp(lx, p.LocalX - p.HalfW, p.LocalX + p.HalfW);
                var cy = Math.Clamp(ly, p.LocalY - p.HalfH, p.LocalY + p.HalfH);
                var d = (cx - lx) * (cx - lx) + (cy - ly) * (cy - ly);
                if (d < bestD) { bestD = d; best = p; bcx = cx; bcy = cy; }
            }
            if (best is null) return false;
            var (ex, ey) = ProjectToEdge(bcx - best.LocalX, bcy - best.LocalY, best.HalfW, best.HalfH);
            var localX = best.LocalX + ex;
            var localY = best.LocalY + ey;

            var cosP = Math.Cos(s.Angle);
            var sinP = Math.Sin(s.Angle);
            var anchorWX = s.X + localX * cosP - localY * sinP;
            var anchorWY = s.Y + localX * sinP + localY * cosP;

            var joint = new FixedMouseJoint(body, ToMVec(anchorWX, anchorWY))
            {
                MaxForce = GrabMaxForce,
                Frequency = GrabFrequency,
                DampingRatio = GrabDamping,
            };
            joint.WorldAnchorB = ToMVec(c.X, c.Y);
            world.Add(joint);
            grabByUser[userId] = joint;
            contactCorner[userId] = -1;   // contact starts at the edge anchor (no wrap yet)

            c.AttachedBlockId = null;
            c.AttachedWallId = null;
            c.AttachedShapeId = shapeId;
            c.AnchorLocalX = localX;
            c.AnchorLocalY = localY;
            return true;
        }
    }

    /// <summary>
    /// Anchor to a static wall at its nearest edge. The wall never moves, so there's no joint —
    /// the grab just records the tether so the client tenses the cursor toward the anchor and
    /// reports the (futile) pull. Releasing is the same <see cref="Detach"/> as any other grab.
    /// </summary>
    public bool TryAttachWall(string userId, string wallId, double clickX, double clickY)
    {
        if (!cursors.TryGetValue(userId, out var c)) return false;
        if (!IsFinite(clickX) || !IsFinite(clickY)) return false;
        lock (worldLock)
        {
            if (!walls.TryGetValue(wallId, out var w)) return false;
            DetachLocked(userId);
            var (localX, localY) = ProjectToEdge(clickX - w.X, clickY - w.Y, w.W / 2, w.H / 2);
            c.AttachedBlockId = null;
            c.AttachedShapeId = null;
            c.AttachedWallId = wallId;
            c.AnchorLocalX = localX;
            c.AnchorLocalY = localY;
            return true;
        }
    }

    /// <summary>
    /// Grab one end of a wire (0 = A, 1 = B). The end follows the cursor while held; picking it up
    /// unplugs it from whatever terminal it was on. <see cref="Detach"/> snaps it to a nearby
    /// terminal on release. This is how the electronics levels are wired.
    /// </summary>
    public bool TryAttachWireEnd(string userId, string wireId, int end, double clickX, double clickY)
    {
        if (!cursors.TryGetValue(userId, out var c)) return false;
        lock (worldLock)
        {
            if (!wires.TryGetValue(wireId, out var w)) return false;
            DetachLocked(userId);
            c.AttachedWireId = wireId;
            c.AttachedWireEnd = end == 1 ? 1 : 0;
            if (c.AttachedWireEnd == 0) w.ATerminalId = null; else w.BTerminalId = null;
            return true;
        }
    }

    /// <summary>Snap a just-released wire end onto the nearest terminal within
    /// <see cref="WireSnapRadius"/>, else leave it loose where the cursor dropped it.</summary>
    private void SnapWireEnd(Wire w, int end)
    {
        var ex = end == 0 ? w.Ax : w.Bx;
        var ey = end == 0 ? w.Ay : w.By;
        Terminal? best = null;
        var bestD = WireSnapRadius * WireSnapRadius;
        foreach (var t in terminals.Values)
        {
            var d = (t.X - ex) * (t.X - ex) + (t.Y - ey) * (t.Y - ey);
            if (d <= bestD) { bestD = d; best = t; }
        }
        if (best is null) return;
        if (end == 0) { w.ATerminalId = best.Id; w.Ax = best.X; w.Ay = best.Y; }
        else { w.BTerminalId = best.Id; w.Bx = best.X; w.By = best.Y; }
    }

    /// <summary>Bulb lights iff the wires + components form a closed loop battery+ → … → battery−
    /// with both the resistor and the bulb in series (removing either internal edge breaks the
    /// loop). A correct, minimal series-circuit check — the seed for breadboards later.</summary>
    private void EvaluateCircuit()
    {
        var bulb = components.Values.FirstOrDefault(c => c.Kind == "bulb");
        if (bulb is null) return;
        var resistor = components.Values.FirstOrDefault(c => c.Kind == "resistor");
        var posT = terminals.Values.FirstOrDefault(t => t.Polarity == "pos");
        var negT = terminals.Values.FirstOrDefault(t => t.Polarity == "neg");
        if (resistor is null || posT is null || negT is null
            || bulb.TerminalIds.Count < 2 || resistor.TerminalIds.Count < 2)
        {
            bulb.Lit = false;
            return;
        }

        var edges = new List<(string A, string B)>();
        foreach (var w in wires.Values)
            if (w.ATerminalId != null && w.BTerminalId != null)
                edges.Add((w.ATerminalId, w.BTerminalId));
        var bulbEdge = (bulb.TerminalIds[0], bulb.TerminalIds[1]);
        var resEdge = (resistor.TerminalIds[0], resistor.TerminalIds[1]);
        edges.Add(bulbEdge);
        edges.Add(resEdge);

        var connected = CircuitConnected(posT.Id, negT.Id, edges, null);
        var bulbInSeries = connected && !CircuitConnected(posT.Id, negT.Id, edges, bulbEdge);
        var resInSeries = connected && !CircuitConnected(posT.Id, negT.Id, edges, resEdge);
        bulb.Lit = connected && bulbInSeries && resInSeries;
    }

    private static bool CircuitConnected(
        string src, string dst, List<(string A, string B)> edges, (string A, string B)? exclude)
    {
        var adj = new Dictionary<string, List<string>>();
        void Link(string a, string b)
        {
            if (!adj.TryGetValue(a, out var l)) { l = []; adj[a] = l; }
            l.Add(b);
        }
        foreach (var (a, b) in edges)
        {
            if (exclude is { } ex && ((a == ex.A && b == ex.B) || (a == ex.B && b == ex.A))) continue;
            Link(a, b);
            Link(b, a);
        }
        if (src == dst) return true;
        var seen = new HashSet<string> { src };
        var q = new Queue<string>();
        q.Enqueue(src);
        while (q.Count > 0)
        {
            var n = q.Dequeue();
            if (n == dst) return true;
            if (adj.TryGetValue(n, out var ns))
                foreach (var m in ns)
                    if (seen.Add(m)) q.Enqueue(m);
        }
        return false;
    }

    /// <summary>Clamp a body-local point into the box, then snap it to whichever of the four
    /// edges is nearest — i.e. the closest point on the perimeter. Grabs land on the rim.</summary>
    private static (double X, double Y) ProjectToEdge(double lx, double ly, double hw, double hh)
    {
        lx = Math.Clamp(lx, -hw, hw);
        ly = Math.Clamp(ly, -hh, hh);
        var toLeft = lx + hw;
        var toRight = hw - lx;
        var toTop = ly + hh;
        var toBottom = hh - ly;
        var min = Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));
        if (min == toRight) lx = hw;
        else if (min == toLeft) lx = -hw;
        else if (min == toBottom) ly = hh;
        else ly = -hh;
        return (lx, ly);
    }

    /// <summary>Reported pull strength of a grab, in mass-units: the tether stretch saturated to
    /// a single grab's ceiling. Matches the soft constraint closely enough to read against a
    /// body's Mass ("1.2 / 2 → not yet").</summary>
    private double PullMassFor(CursorState c)
    {
        var dx = c.X - c.AnchorWorldX;
        var dy = c.Y - c.AnchorWorldY;
        var frac = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / MaxPullPx, 0, 1);
        return SingleGrabMaxMass * frac;
    }

    /// <summary>Hold a tethered cursor at or inside the leash length (<see cref="MaxPullPx"/>) from
    /// its anchor, then report its pull. The leash end is exactly where the pull saturates, so the
    /// readout and the rendered tether agree. <see cref="CursorState.PullMass"/> is in mass-units
    /// (force ÷ ForcePerMass) so it reads directly against the body's printed Mass; the newton
    /// equivalent is PullMass × ForcePerMass.</summary>
    private void LeashAndReport(CursorState c)
    {
        var dx = c.X - c.AnchorWorldX;
        var dy = c.Y - c.AnchorWorldY;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len > MaxPullPx && len > 1e-9)
        {
            c.X = c.AnchorWorldX + dx / len * MaxPullPx;
            c.Y = c.AnchorWorldY + dy / len * MaxPullPx;
        }
        c.PullMass = PullMassFor(c);
    }

    /// <summary>
    /// Segmented-tether update: maintain the rope as an accumulating polyline that wraps the body's
    /// sharp corners. The anchor stays fixed on the edge where you grabbed; whenever the last
    /// segment cuts through the body the rope catches a new corner (push), and reversing direction
    /// pops corners off (the rope unwinds). Force is applied at the last contact, so pulling spins
    /// the body until the rope is straight again. Sets <see cref="CursorState.AnchorWorldX"/>/Y to
    /// the contact and <see cref="CursorState.TetherPivots"/> to the full chain for rendering.
    /// </summary>
    private void UpdateTether(CursorState c, Body body, List<(double X, double Y)> corners,
        List<(double Cx, double Cy, double Hw, double Hh)> rectsLocal, double bodyX, double bodyY, double angle)
    {
        var userId = c.UserId;
        if (!grabByUser.ContainsKey(userId)) return;
        if (!wrapStack.TryGetValue(userId, out var stack)) { stack = []; wrapStack[userId] = stack; }

        var cosN = Math.Cos(-angle);
        var sinN = Math.Sin(-angle);
        var cosP = Math.Cos(angle);
        var sinP = Math.Sin(angle);
        var aWorldX = bodyX + c.AnchorLocalX * cosP - c.AnchorLocalY * sinP;
        var aWorldY = bodyY + c.AnchorLocalX * sinP + c.AnchorLocalY * cosP;

        bool Cuts(double fromX, double fromY, double toX, double toY, double margin)
        {
            var flx = (fromX - bodyX) * cosN - (fromY - bodyY) * sinN;
            var fly = (fromX - bodyX) * sinN + (fromY - bodyY) * cosN;
            var tlx = (toX - bodyX) * cosN - (toY - bodyY) * sinN;
            var tly = (toX - bodyX) * sinN + (toY - bodyY) * cosN;
            var best = double.MaxValue;
            foreach (var r in rectsLocal)
                if (SegmentRectEntry(flx - r.Cx, fly - r.Cy, tlx - r.Cx, tly - r.Cy, r.Hw + margin, r.Hh + margin, out var t))
                    best = Math.Min(best, t);
            _ = best;
            return best < double.MaxValue;
        }
        double EntryT(double fromX, double fromY, double toX, double toY)
        {
            var flx = (fromX - bodyX) * cosN - (fromY - bodyY) * sinN;
            var fly = (fromX - bodyX) * sinN + (fromY - bodyY) * cosN;
            var tlx = (toX - bodyX) * cosN - (toY - bodyY) * sinN;
            var tly = (toX - bodyX) * sinN + (toY - bodyY) * cosN;
            var best = double.MaxValue;
            foreach (var r in rectsLocal)
                if (SegmentRectEntry(flx - r.Cx, fly - r.Cy, tlx - r.Cx, tly - r.Cy, r.Hw, r.Hh, out var t))
                    best = Math.Min(best, t);
            return best;
        }

        // Unwind: pop a pivot once the rope from the point before it to the cursor no longer cuts
        // the body (with a small clearance margin so it doesn't pop-and-re-catch on the boundary).
        const double clearMargin = 6;
        while (stack.Count > 0)
        {
            var prevX = stack.Count >= 2 ? corners[stack[^2]].X : aWorldX;
            var prevY = stack.Count >= 2 ? corners[stack[^2]].Y : aWorldY;
            if (Cuts(prevX, prevY, c.X, c.Y, clearMargin)) break;
            stack.RemoveAt(stack.Count - 1);
        }

        // Wrap: while the last segment cuts the body, catch the corner nearest where it enters.
        for (var guard = 0; guard < 8; guard++)
        {
            var lastX = stack.Count > 0 ? corners[stack[^1]].X : aWorldX;
            var lastY = stack.Count > 0 ? corners[stack[^1]].Y : aWorldY;
            // Require the rope to cut clearly INTO the body (rect inset by 12 px) before catching a
            // corner — so the slight rotation of a normal off-centre pull doesn't spuriously wrap.
            // With the +6 clearance on the unwind side this gives a stable hysteresis band.
            if (!Cuts(lastX, lastY, c.X, c.Y, -12)) break;
            var t = EntryT(lastX, lastY, c.X, c.Y);
            var ex = lastX + (c.X - lastX) * t;
            var ey = lastY + (c.Y - lastY) * t;
            var bi = -1;
            var bd = double.MaxValue;
            for (var i = 0; i < corners.Count; i++)
            {
                if (stack.Count > 0 && i == stack[^1]) continue;
                var d = (corners[i].X - ex) * (corners[i].X - ex) + (corners[i].Y - ey) * (corners[i].Y - ey);
                if (d < bd) { bd = d; bi = i; }
            }
            if (bi < 0 || stack.Contains(bi)) break;   // no new corner to catch (or would loop)
            stack.Add(bi);
        }

        // Contact = last pivot (or the anchor). Re-anchor the joint there if it moved.
        int contactIdx;
        double contactX, contactY;
        if (stack.Count > 0) { contactIdx = stack[^1]; contactX = corners[contactIdx].X; contactY = corners[contactIdx].Y; }
        else { contactIdx = -1; contactX = aWorldX; contactY = aWorldY; }

        if (!contactCorner.TryGetValue(userId, out var prevIdx) || prevIdx != contactIdx)
        {
            if (grabByUser.TryGetValue(userId, out var oldJoint)) world.Remove(oldJoint);
            var nj = new FixedMouseJoint(body, ToMVec(contactX, contactY))
            {
                MaxForce = GrabMaxForce,
                Frequency = GrabFrequency,
                DampingRatio = GrabDamping,
            };
            nj.WorldAnchorB = ToMVec(c.X, c.Y);
            world.Add(nj);
            grabByUser[userId] = nj;
            contactCorner[userId] = contactIdx;
        }

        // Force/leash/rotation key off the contact; the chain (anchor → corners) renders the rope.
        c.AnchorWorldX = contactX;
        c.AnchorWorldY = contactY;
        c.TetherPivots.Clear();
        c.TetherPivots.Add(aWorldX);
        c.TetherPivots.Add(aWorldY);
        foreach (var idx in stack) { c.TetherPivots.Add(corners[idx].X); c.TetherPivots.Add(corners[idx].Y); }
    }

    /// <summary>Straight (un-segmented) tether: anchor world from the fixed body-local point, and a
    /// single-point pivot chain. Used for walls and when <see cref="SegmentedTether"/> is off.
    private static void SetStraightAnchor(CursorState c, double bodyX, double bodyY, double angle)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        c.AnchorWorldX = bodyX + c.AnchorLocalX * cos - c.AnchorLocalY * sin;
        c.AnchorWorldY = bodyY + c.AnchorLocalX * sin + c.AnchorLocalY * cos;
        c.TetherPivots.Clear();
        c.TetherPivots.Add(c.AnchorWorldX);
        c.TetherPivots.Add(c.AnchorWorldY);
    }

    /// <summary>Slab clip of segment (ax,ay)→(bx,by) against rect [-hw,hw]×[-hh,hh]. Returns true if
    /// it enters within its length; <paramref name="tEnter"/> is the entry parameter in [0,1].</summary>
    private static bool SegmentRectEntry(double ax, double ay, double bx, double by, double hw, double hh, out double tEnter)
    {
        tEnter = 0;
        double dx = bx - ax, dy = by - ay, tmin = 0, tmax = 1;
        if (Math.Abs(dx) < 1e-9) { if (ax < -hw || ax > hw) return false; }
        else
        {
            var t1 = (-hw - ax) / dx;
            var t2 = (hw - ax) / dx;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1);
            tmax = Math.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        if (Math.Abs(dy) < 1e-9) { if (ay < -hh || ay > hh) return false; }
        else
        {
            var t1 = (-hh - ay) / dy;
            var t2 = (hh - ay) / dy;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1);
            tmax = Math.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        tEnter = Math.Max(0, tmin);
        return tmax > 1e-3;
    }

    private static List<(double X, double Y)> BlockCornersWorld(BlockState b)
    {
        var cos = Math.Cos(b.Angle);
        var sin = Math.Sin(b.Angle);
        var hw = b.W / 2;
        var hh = b.H / 2;
        Span<(double x, double y)> local = [(-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh)];
        var r = new List<(double, double)>(4);
        foreach (var (lx, ly) in local) r.Add((b.X + lx * cos - ly * sin, b.Y + lx * sin + ly * cos));
        return r;
    }

    private static List<(double X, double Y)> ShapeCornersWorld(ShapeActor s)
    {
        var cos = Math.Cos(s.Angle);
        var sin = Math.Sin(s.Angle);
        var r = new List<(double, double)>(s.Pieces.Count * 4);
        foreach (var p in s.Pieces)
        {
            Span<(double x, double y)> local =
            [
                (p.LocalX - p.HalfW, p.LocalY - p.HalfH), (p.LocalX + p.HalfW, p.LocalY - p.HalfH),
                (p.LocalX + p.HalfW, p.LocalY + p.HalfH), (p.LocalX - p.HalfW, p.LocalY + p.HalfH),
            ];
            foreach (var (lx, ly) in local) r.Add((s.X + lx * cos - ly * sin, s.Y + lx * sin + ly * cos));
        }
        return r;
    }

    /// <summary>Release whatever this cursor is holding.</summary>
    public void Detach(string userId)
    {
        lock (worldLock) DetachLocked(userId);
    }

    private void DetachLocked(string userId)
    {
        if (grabByUser.Remove(userId, out var joint))
            world.Remove(joint);   // walls/wires have no joint — only block/shape grabs are in grabByUser
        wrapStack.Remove(userId);
        contactCorner.Remove(userId);
        if (cursors.TryGetValue(userId, out var c))
        {
            if (c.AttachedWireId is { } wid && wires.TryGetValue(wid, out var w))
                SnapWireEnd(w, c.AttachedWireEnd);   // releasing a wire end plugs it into a nearby terminal
            c.AttachedBlockId = null;
            c.AttachedShapeId = null;
            c.AttachedWallId = null;
            c.AttachedWireId = null;
            c.PullMass = 0;
            c.TetherPivots.Clear();
        }
    }

    // ── votes ──────────────────────────────────────────────────────────────

    /// <summary>Snapshot of the active vote, or null. Defensive copy.</summary>
    public RoomVote? CurrentVote
    {
        get
        {
            lock (voteLock) return activeVote is null ? null : CloneVote(activeVote);
        }
    }

    /// <summary>The currently-loaded level (1..LevelCount).</summary>
    public int CurrentLevel
    {
        get { lock (levelLock) return currentLevel; }
    }

    /// <summary>Begin a reset vote.</summary>
    public bool StartResetVote(string userId) => StartVote(userId, VoteKind.Reset, 0);

    /// <summary>Begin a level-switch vote. Target must be in 1..LevelCount.</summary>
    public bool StartLevelVote(string userId, int targetLevel)
    {
        if (targetLevel < 1 || targetLevel > LevelCount) return false;
        if (targetLevel == CurrentLevel) return false;
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

    /// <summary>Cast yes/no on the active vote. Latecomers (not in the voter snapshot) are
    /// ignored; re-voting flips your prior choice.</summary>
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
        if (activeVote is null) return;
        if (activeVote.YesUserIds.Count >= activeVote.Quorum)
        {
            ApplyVote(activeVote);
            activeVote = null;
            return;
        }
        var undecided = activeVote.Voters.Count - activeVote.YesUserIds.Count - activeVote.NoUserIds.Count;
        if (activeVote.YesUserIds.Count + undecided < activeVote.Quorum)
            activeVote = null;
    }

    private void ApplyVote(RoomVote v)
    {
        switch (v.Kind)
        {
            case VoteKind.Reset: ResetPuzzles(); break;
            case VoteKind.SelectLevel: SwitchToLevel(v.TargetLevel); break;
        }
    }

    private void TimeoutVoteIfExpired()
    {
        lock (voteLock)
        {
            if (activeVote is null) return;
            if (CurrentTick - activeVote.StartedAtTick >= RoomVote.TimeoutTicks)
                activeVote = null;
        }
    }

    private static RoomVote CloneVote(RoomVote v) => new()
    {
        Kind = v.Kind,
        TargetLevel = v.TargetLevel,
        StartedAtTick = v.StartedAtTick,
        StartedByUserId = v.StartedByUserId,
        Voters = [.. v.Voters],
        YesUserIds = [.. v.YesUserIds],
        NoUserIds = [.. v.NoUserIds],
        Quorum = v.Quorum,
    };

    private void SwitchToLevel(int level)
    {
        lock (levelLock) currentLevel = Math.Clamp(level, 1, LevelCount);
        ResetPuzzles();
        Interlocked.Exchange(ref pendingGeometryRebroadcast, 1);
        Interlocked.Exchange(ref pendingLevelAnnouncement, currentLevel);
    }

    private int pendingGeometryRebroadcast;
    private int pendingLevelAnnouncement;

    /// <summary>True iff a level switch or reset happened since the last broadcast; the loop
    /// reads + clears this and, when set, re-sends the static Geometry message.</summary>
    public bool ConsumeGeometryRebroadcast() =>
        Interlocked.Exchange(ref pendingGeometryRebroadcast, 0) == 1;

    /// <summary>When non-zero, the loop fires a LevelLoaded banner event for this level number
    /// and clears it. Decoupled from geometry so a plain Reset doesn't pop the banner.</summary>
    public int ConsumeLevelAnnouncement() =>
        Interlocked.Exchange(ref pendingLevelAnnouncement, 0);

    /// <summary>Record a "whistle" ping for clients to render + sound.</summary>
    public void RecordWhistle(string userId, double x, double y)
    {
        if (!cursors.TryGetValue(userId, out var c)) return;
        if (!IsFinite(x) || !IsFinite(y)) return;
        var cx = Math.Clamp(x, 0, WorldGeometry.Width);
        var cy = Math.Clamp(y, 0, WorldGeometry.Height);
        whistles.Enqueue(new Whistle { UserId = userId, Color = c.Color, X = cx, Y = cy, Tick = CurrentTick });
        while (whistles.Count > WhistleRingCapacity && whistles.TryDequeue(out _)) { }
    }

    /// <summary>Read a cursor by id (live reference).</summary>
    public CursorState? GetCursor(string userId) =>
        cursors.TryGetValue(userId, out var c) ? c : null;

    /// <summary>
    /// Drop cursors silent for <paramref name="staleAfterTicks"/> ticks — a faster ghost
    /// cleanup than SignalR's keep-alive for silent network drops.
    /// </summary>
    public int EvictStaleCursors(long staleAfterTicks)
    {
        var cutoff = CurrentTick - staleAfterTicks;
        var evicted = 0;
        foreach (var c in cursors.Values)
        {
            if (c.LastInputTick < cutoff)
            {
                RemovePlayer(c.UserId);
                evicted++;
            }
        }
        return evicted;
    }

    /// <summary>All live cursors (defensive copy).</summary>
    public IReadOnlyCollection<CursorState> AllCursors => cursors.Values.ToList();
    /// <summary>Live cursor count without the defensive copy <see cref="AllCursors"/> allocates —
    /// cheap enough to read on the hot tick path (e.g. slow-tick telemetry).</summary>
    public int CursorCount => cursors.Count;
    /// <summary>All blocks (defensive copy).</summary>
    public IReadOnlyCollection<BlockState> AllBlocks => blocks.Values.ToList();
    /// <summary>All walls (defensive copy).</summary>
    public IReadOnlyCollection<Wall> AllWalls => walls.Values.ToList();

    // ── seed / test helpers ──────────────────────────────────────────────────

    /// <summary>Test/seed helper: add a draggable block and its engine body.</summary>
    internal void AddBlock(BlockState b)
    {
        lock (worldLock) AddBlockLocked(b);
    }

    private void AddBlockLocked(BlockState b)
    {
        blocks[b.Id] = b;

        // Inertia (kg) scales with Mass but is decoupled from the move-threshold below, so a
        // body can feel heavy to accelerate without changing how many cursors it takes to budge.
        var areaM2 = ToM(b.W) * ToM(b.H);
        var inertiaKg = (float)b.Mass * InertiaKgPerMass;
        var density = areaM2 > 1e-6f ? inertiaKg / areaM2 : 1f;
        var body = world.CreateRectangle(
            ToM(b.W), ToM(b.H), density, ToMVec(b.X, b.Y), (float)b.Angle, BodyType.Dynamic);
        body.LinearDamping = LinearDamping;
        body.AngularDamping = AngularDamping;

        // Top-down dry friction: a FrictionJoint to the static ground. MaxForce = Mass × ForcePerMass
        // is the move-threshold — the pulls on the body must sum past its Mass to break it free, so
        // a body heavier than one grab's ceiling (SingleGrabMaxMass) needs cooperating cursors.
        var linFric = (float)b.Mass * ForcePerMass;
        var fric = new FrictionJoint(ground, body, body.WorldCenter, true)
        {
            MaxForce = linFric,
            MaxTorque = linFric * FrictionTorqueScale,
        };
        world.Add(fric);

        bodyByBlock[b.Id] = body;
    }

    /// <summary>Test/seed helper: add a static wall (collidable engine body + geometry record).</summary>
    internal void AddWall(Wall w)
    {
        lock (worldLock) AddWallLocked(w);
    }

    private void AddWallLocked(Wall w)
    {
        walls[w.Id] = w;
        bodyByWall[w.Id] = world.CreateRectangle(
            ToM(w.W), ToM(w.H), 1f, ToMVec(w.X, w.Y), 0f, BodyType.Static);
    }

    /// <summary>Test/seed helper: add a goal zone (pure data; solved-check runs in Step).</summary>
    internal void AddGoal(GoalZone g) => goals[g.Id] = g;
    /// <summary>Test/seed helper: add a world label.</summary>
    internal void AddLabel(WorldLabel l) => labels[l.Id] = l;
    /// <summary>Test/seed helper: add a compound shape and its engine body.</summary>
    internal void AddShape(ShapeActor s)
    {
        lock (worldLock) AddShapeLocked(s);
    }

    private void AddShapeLocked(ShapeActor s)
    {
        shapes[s.Id] = s;
        var body = world.CreateBody(ToMVec(s.X, s.Y), (float)s.Angle, BodyType.Dynamic);
        body.LinearDamping = LinearDamping;
        body.AngularDamping = AngularDamping;

        // One density across all pieces so the body's total mass = Mass × InertiaKgPerMass; the
        // engine derives the (compound) moment of inertia from the fixture layout for free.
        var totalAreaM2 = 0f;
        foreach (var p in s.Pieces) totalAreaM2 += ToM(p.HalfW * 2) * ToM(p.HalfH * 2);
        var density = totalAreaM2 > 1e-6f ? (float)s.Mass * InertiaKgPerMass / totalAreaM2 : 1f;
        foreach (var p in s.Pieces)
            body.CreateRectangle(ToM(p.HalfW * 2), ToM(p.HalfH * 2), density,
                new Vector2(ToM(p.LocalX), ToM(p.LocalY)));

        var linFric = (float)s.Mass * ForcePerMass;
        var fric = new FrictionJoint(ground, body, body.WorldCenter, true)
        {
            MaxForce = linFric,
            MaxTorque = linFric * FrictionTorqueScale,
        };
        world.Add(fric);

        bodyByShape[s.Id] = body;
    }

    /// <summary>Test/seed helper: add a shape goal (pure data; solved-check runs in Step).</summary>
    internal void AddShapeGoal(ShapeGoal g) => shapeGoals[g.Id] = g;
    /// <summary>Test/seed helper: add a circuit component.</summary>
    internal void AddComponent(CircuitComponent c) => components[c.Id] = c;
    /// <summary>Test/seed helper: add a terminal.</summary>
    internal void AddTerminal(Terminal t) => terminals[t.Id] = t;
    /// <summary>Test/seed helper: add a wire.</summary>
    internal void AddWire(Wire w) => wires[w.Id] = w;
    /// <summary>Read a component by id (live reference) — test helper for the circuit eval.</summary>
    internal CircuitComponent? GetComponent(string id) => components.TryGetValue(id, out var c) ? c : null;

    /// <summary>Test helper: register a cursor at an explicit position.</summary>
    internal void AddTestCursor(string userId, double x, double y, string color = "#7F77DD")
    {
        cursors[userId] = new CursorState
        {
            UserId = userId, DisplayName = userId, Color = color, X = x, Y = y,
        };
    }

    // ── snapshot ──────────────────────────────────────────────────────────────

    /// <summary>Authoritative world snapshot for broadcast. Switches/doors/shapes ride empty
    /// lists in the spike — the wire contract is unchanged, so the client needs no rewrite.</summary>
    public WorldSnapshot Snapshot()
    {
        var tick = CurrentTick;
        return new WorldSnapshot
        {
            Tick = tick,
            Cursors = cursors.Values.Select(CloneCursor).ToList(),
            Blocks = blocks.Values.Select(CloneBlock).ToList(),
            Goals = goals.Values.Select(CloneGoal).ToList(),
            Switches = [],
            Doors = [],
            Shapes = shapes.Values.Select(CloneShape).ToList(),
            ShapeGoals = shapeGoals.Values.Select(CloneShapeGoal).ToList(),
            Components = components.Values.Select(CloneComponent).ToList(),
            Terminals = terminals.Values.Select(CloneTerminal).ToList(),
            Wires = wires.Values.Select(CloneWire).ToList(),
            Whistles = whistles.Where(w => tick - w.Tick < WhistleBroadcastTicks).Select(CloneWhistle).ToList(),
            Vote = CurrentVote,
            CurrentLevel = CurrentLevel,
            LevelCount = LevelCount,
            CursorCollision = cursorCollision,
            SegmentedTether = segmentedTether,
        };
    }

    /// <summary>Static-geometry snapshot delivered once per connection (walls + labels).</summary>
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
        AttachedBlockId = c.AttachedBlockId, AttachedShapeId = c.AttachedShapeId, AttachedWallId = c.AttachedWallId,
        AttachedWireId = c.AttachedWireId, AttachedWireEnd = c.AttachedWireEnd,
        AnchorLocalX = c.AnchorLocalX, AnchorLocalY = c.AnchorLocalY,
        AnchorWorldX = c.AnchorWorldX, AnchorWorldY = c.AnchorWorldY, PullMass = c.PullMass,
        TetherPivots = [.. c.TetherPivots],
    };
    private static BlockState CloneBlock(BlockState b) => new()
    {
        Id = b.Id, X = b.X, Y = b.Y, W = b.W, H = b.H, Angle = b.Angle,
        Vx = b.Vx, Vy = b.Vy, Mass = b.Mass, StaticFriction = b.StaticFriction, Color = b.Color,
    };
    private static GoalZone CloneGoal(GoalZone g) => new()
    {
        Id = g.Id, X = g.X, Y = g.Y, W = g.W, H = g.H, TargetBlockId = g.TargetBlockId, IsSolved = g.IsSolved,
    };
    private static Wall CloneWall(Wall w) => new() { Id = w.Id, X = w.X, Y = w.Y, W = w.W, H = w.H };
    private static ShapeActor CloneShape(ShapeActor s) => new()
    {
        Id = s.Id, X = s.X, Y = s.Y, Angle = s.Angle, Mass = s.Mass, Color = s.Color,
        Pieces = s.Pieces.Select(p => new ShapePiece
        {
            LocalX = p.LocalX, LocalY = p.LocalY, HalfW = p.HalfW, HalfH = p.HalfH,
        }).ToList(),
    };
    private static ShapeGoal CloneShapeGoal(ShapeGoal g) => new()
    {
        Id = g.Id, X = g.X, Y = g.Y, W = g.W, H = g.H, TargetShapeId = g.TargetShapeId, IsSolved = g.IsSolved,
    };
    private static CircuitComponent CloneComponent(CircuitComponent c) => new()
    {
        Id = c.Id, Kind = c.Kind, X = c.X, Y = c.Y, W = c.W, H = c.H, Lit = c.Lit, Label = c.Label,
        TerminalIds = [.. c.TerminalIds],
    };
    private static Terminal CloneTerminal(Terminal t) => new() { Id = t.Id, X = t.X, Y = t.Y, Polarity = t.Polarity };
    private static Wire CloneWire(Wire w) => new()
    {
        Id = w.Id, Color = w.Color, Ax = w.Ax, Ay = w.Ay, Bx = w.Bx, By = w.By,
        ATerminalId = w.ATerminalId, BTerminalId = w.BTerminalId,
    };
    private static WorldLabel CloneLabel(WorldLabel l) => new()
    {
        Id = l.Id, X = l.X, Y = l.Y, Title = l.Title, Subtitle = l.Subtitle,
    };
    private static Whistle CloneWhistle(Whistle w) => new()
    {
        UserId = w.UserId, Color = w.Color, X = w.X, Y = w.Y, Tick = w.Tick,
    };

    // ── physics step (called by GameLoopService at 30 Hz) ─────────────────────

    /// <summary>One simulation tick: feed each grab joint its cursor target, step the engine,
    /// sync body poses back to the snapshot records, and evaluate goals.</summary>
    public void Step()
    {
        lock (worldLock)
        {
            // Drive each grab's target to its cursor's current world position.
            foreach (var (userId, joint) in grabByUser)
            {
                if (cursors.TryGetValue(userId, out var c))
                    joint.WorldAnchorB = ToMVec(c.X, c.Y);
            }

            world.Step(Dt);

            // Engine → snapshot records (metres → pixels).
            foreach (var (id, body) in bodyByBlock)
            {
                if (!blocks.TryGetValue(id, out var b)) continue;
                b.X = ToPx(body.Position.X);
                b.Y = ToPx(body.Position.Y);
                b.Angle = body.Rotation;
                b.Vx = ToPx(body.LinearVelocity.X);
                b.Vy = ToPx(body.LinearVelocity.Y);
            }
            foreach (var (id, body) in bodyByShape)
            {
                if (!shapes.TryGetValue(id, out var s)) continue;
                s.X = ToPx(body.Position.X);
                s.Y = ToPx(body.Position.Y);
                s.Angle = body.Rotation;
            }

            // Tether anchor (world) + leash + reported pull for every grabbing cursor, from the
            // fresh body pose. Anchors are stored body-local so they ride a rotating body; a wall
            // is static so its anchor is fixed.
            foreach (var c in cursors.Values)
            {
                if (c.AttachedBlockId is { } bid && blocks.TryGetValue(bid, out var ab))
                {
                    if (segmentedTether && bodyByBlock.TryGetValue(bid, out var bbody))
                        UpdateTether(c, bbody, BlockCornersWorld(ab), [(0, 0, ab.W / 2, ab.H / 2)], ab.X, ab.Y, ab.Angle);
                    else
                        SetStraightAnchor(c, ab.X, ab.Y, ab.Angle);
                    LeashAndReport(c);
                }
                else if (c.AttachedShapeId is { } sid && shapes.TryGetValue(sid, out var ash))
                {
                    if (segmentedTether && bodyByShape.TryGetValue(sid, out var sbody))
                        UpdateTether(c, sbody, ShapeCornersWorld(ash),
                            ash.Pieces.Select(p => (p.LocalX, p.LocalY, p.HalfW, p.HalfH)).ToList(),
                            ash.X, ash.Y, ash.Angle);
                    else
                        SetStraightAnchor(c, ash.X, ash.Y, ash.Angle);
                    LeashAndReport(c);
                }
                else if (c.AttachedWallId is { } wid && walls.TryGetValue(wid, out var aw))
                {
                    SetStraightAnchor(c, aw.X, aw.Y, 0);   // walls are static + axis-aligned
                    LeashAndReport(c);
                }
                else
                {
                    c.PullMass = 0;
                    c.TetherPivots.Clear();
                }

                // A held wire end follows the (collision-resolved) cursor.
                if (c.AttachedWireId is { } heldWireId && wires.TryGetValue(heldWireId, out var hw))
                {
                    if (c.AttachedWireEnd == 0) { hw.Ax = c.X; hw.Ay = c.Y; }
                    else { hw.Bx = c.X; hw.By = c.Y; }
                }
            }

            EvaluateCircuit();   // light the bulb when the loop is complete (no-op if no circuit)
        }

        foreach (var g in goals.Values)
        {
            if (!blocks.TryGetValue(g.TargetBlockId, out var b)) { g.IsSolved = false; continue; }
            g.IsSolved =
                b.X > g.X - g.W / 2 && b.X < g.X + g.W / 2 &&
                b.Y > g.Y - g.H / 2 && b.Y < g.Y + g.H / 2;
        }

        // A shape goal is satisfied only when every piece's world centre sits inside it — the
        // whole shape has to be threaded in, not just its origin.
        foreach (var g in shapeGoals.Values)
        {
            if (!shapes.TryGetValue(g.TargetShapeId, out var s) || s.Pieces.Count == 0)
            {
                g.IsSolved = false;
                continue;
            }
            var cos = Math.Cos(s.Angle);
            var sin = Math.Sin(s.Angle);
            var solved = true;
            foreach (var p in s.Pieces)
            {
                var wx = s.X + p.LocalX * cos - p.LocalY * sin;
                var wy = s.Y + p.LocalX * sin + p.LocalY * cos;
                if (wx <= g.X - g.W / 2 || wx >= g.X + g.W / 2 || wy <= g.Y - g.H / 2 || wy >= g.Y + g.H / 2)
                {
                    solved = false;
                    break;
                }
            }
            g.IsSolved = solved;
        }

        // Drop whistles older than the broadcast window so Snapshot's per-tick scan stays
        // bounded by recent activity, not the ring capacity. Single-consumer dequeue (only the
        // game loop drains) so this is safe against concurrent RecordWhistle enqueues.
        var whistleCutoff = CurrentTick - WhistleBroadcastTicks;
        while (whistles.TryPeek(out var oldest) && oldest.Tick < whistleCutoff)
            whistles.TryDequeue(out _);

        AdvanceTick();
        TimeoutVoteIfExpired();
    }

    // ── world layout ─────────────────────────────────────────────────────────

    private void SeedPuzzles()
    {
        switch (currentLevel)
        {
            case 2: SeedLevel2(); break;
            case 3: SeedLevel3(); break;
            case 4: SeedLevel4(); break;
            case 5: SeedLevel5(); break;
            case 6: SeedLevel6(); break;
            case 7: SeedLevel7(); break;
            case 8: SeedLevel8(); break;
            case 9: SeedLevel9(); break;
            case 10: SeedLevel10(); break;
            case 11: SeedLevel11(); break;
            case 12: SeedLevel12(); break;
            case 13: SeedLevel13(); break;
            case 14: SeedLevel14(); break;
            default: SeedLevel1(); break;
        }
    }

    /// <summary>
    /// Drop every puzzle artefact and re-seed. Cursors + whistles are kept (players stay in
    /// the room); the engine world is fully cleared and the ground body re-created.
    /// </summary>
    internal void ResetPuzzles()
    {
        lock (worldLock)
        {
            world.Clear();
            ground = world.CreateBody(Vector2.Zero, 0f, BodyType.Static);
            bodyByBlock.Clear();
            bodyByWall.Clear();
            bodyByShape.Clear();
            grabByUser.Clear();
            wrapStack.Clear();
            contactCorner.Clear();
            blocks.Clear();
            goals.Clear();
            walls.Clear();
            labels.Clear();
            shapes.Clear();
            shapeGoals.Clear();
            components.Clear();
            terminals.Clear();
            wires.Clear();
            foreach (var c in cursors.Values)
            {
                c.AttachedBlockId = null;
                c.AttachedShapeId = null;
            }
            SeedPuzzles();
        }
        // Walls + labels are static geometry the client only receives on connect or on an
        // explicit rebroadcast. A reset re-seeds them (identical for the current spike levels,
        // but not once a re-ported level carries walls), so push them out so every client's
        // geometry matches the freshly seeded world. SwitchToLevel sets this too — a double
        // set is harmless (the loop reads-and-clears once).
        Interlocked.Exchange(ref pendingGeometryRebroadcast, 1);
    }

    /// <summary>
    /// Level 1 — "Drop it on the pad". One light, low-friction block and a generous goal
    /// square. A single cursor can drag it solo: this is the controls-teaching level — slow,
    /// almost too easy, on purpose, so the drag/momentum feel reads clearly.
    /// </summary>
    private void SeedLevel1()
    {
        const string blockId = "L1-block";
        AddBlockLocked(new BlockState
        {
            Id = blockId, X = 3500, Y = 5000, W = 160, H = 160,
            Mass = 1, Color = "#D85A30",
        });
        goals["L1-goal"] = new GoalZone
        {
            Id = "L1-goal", X = 6500, Y = 5000, W = 700, H = 700, TargetBlockId = blockId,
        };
        labels["L1-label"] = new WorldLabel
        {
            Id = "L1-label", X = 5000, Y = 3500,
            Title = "Level 1 — Drop it on the pad",
            Subtitle = "Click the block's edge and drag it into the big square. The number is its mass.",
        };
    }

    /// <summary>
    /// Level 2 — "Too heavy for one". A single massive, high-friction block. One cursor's pull
    /// can't beat the friction; two cursors grabbing different corners and pulling together do
    /// — and if they pull from different points the block visibly rotates. The first taste of
    /// cooperation and torque, still on a single body.
    /// </summary>
    private void SeedLevel2()
    {
        const string blockId = "L2-block";
        AddBlockLocked(new BlockState
        {
            Id = blockId, X = 3000, Y = 5000, W = 260, H = 260,
            Mass = 2, Color = "#378ADD",
        });
        // A wall the team has to route the block around — and that your cursor can't pass
        // through (try grabbing its edge: the wall won't budge, but your cursor tenses toward it).
        AddWallLocked(new Wall { Id = "L2-wall", X = 5000, Y = 5000, W = 180, H = 1200 });
        goals["L2-goal"] = new GoalZone
        {
            Id = "L2-goal", X = 6700, Y = 5000, W = 700, H = 700, TargetBlockId = blockId,
        };
        labels["L2-label"] = new WorldLabel
        {
            Id = "L2-label", X = 5000, Y = 3500,
            Title = "Level 2 — Too heavy for one",
            Subtitle = "Mass 2: one cursor (max ~1.5) can't. Two of you grab edges and pull around the wall.",
        };
    }

    /// <summary>
    /// Level 3 — "Pivot the couch". The first engine-backed compound shape: an L the team has to
    /// grab by the edges, rotate, and thread around a wall into the goal pad. A single cursor
    /// (max pull ~1.5) can't beat its Mass; two cooperating cursors on different arms can pivot it.
    /// </summary>
    private void SeedLevel3()
    {
        const string shapeId = "L3-shape";
        AddShapeLocked(new ShapeActor
        {
            Id = shapeId, X = 2800, Y = 5000, Mass = 2, Color = "#7FBF5A",
            Pieces =
            [
                new ShapePiece { LocalX = 0,    LocalY = 0,    HalfW = 220, HalfH = 55 },   // long arm
                new ShapePiece { LocalX = -165, LocalY = -165, HalfW = 55,  HalfH = 165 },  // short arm (the foot of the L)
            ],
        });
        // Shorter wall + roomier goal than the first cut: enough of an obstacle to force a route
        // around, but the L (and a rotation) still clears it comfortably above or below.
        AddWallLocked(new Wall { Id = "L3-wall", X = 5000, Y = 5000, W = 180, H = 700 });
        AddShapeGoal(new ShapeGoal
        {
            Id = "L3-goal", X = 7200, Y = 5000, W = 1000, H = 1000, TargetShapeId = shapeId,
        });
        labels["L3-label"] = new WorldLabel
        {
            Id = "L3-label", X = 5000, Y = 3300,
            Title = "Level 3 — Pivot the couch",
            Subtitle = "Grab the L's edges and route it around the wall onto the pad. Two cursors to turn it.",
        };
    }

    // Levels 4–14: engine-backed, tuned for two players. Cooperative single-bodies use Mass > the
    // single-grab ceiling (1.5) so one cursor can't solo them; mirror/two-key levels use two light
    // bodies (one per player). Geometry is a forgiving first pass — easy to tune by feel.

    /// <summary>Level 4 — "Stand together, then heave". A heavy block lined up and heaved straight
    /// through a gap in a wall; one cursor can't beat its Mass, two pulling together can.</summary>
    private void SeedLevel4()
    {
        const string id = "L4-block";
        AddBlockLocked(new BlockState { Id = id, X = 2800, Y = 5000, W = 240, H = 240, Mass = 2.2, Color = "#D4537E" });
        AddWallLocked(new Wall { Id = "L4-wt", X = 5000, Y = 4150, W = 220, H = 1300 });   // gap from y4800
        AddWallLocked(new Wall { Id = "L4-wb", X = 5000, Y = 5850, W = 220, H = 1300 });   // to y5200 (400 tall)
        goals["L4-goal"] = new GoalZone { Id = "L4-goal", X = 7200, Y = 5000, W = 800, H = 800, TargetBlockId = id };
        labels["L4-label"] = new WorldLabel
        {
            Id = "L4-label", X = 5000, Y = 3200,
            Title = "Level 4 — Stand together, then heave",
            Subtitle = "Mass 2.2 — one cursor can't. Line up and heave it straight through the gap together.",
        };
    }

    /// <summary>Level 5 — "Heavy heave". The heaviest straight drag: only two cursors near full
    /// stretch beat it. No obstacles — pure cooperative strength.</summary>
    private void SeedLevel5()
    {
        const string id = "L5-block";
        AddBlockLocked(new BlockState { Id = id, X = 2800, Y = 5000, W = 300, H = 300, Mass = 2.6, Color = "#3a3a3a" });
        goals["L5-goal"] = new GoalZone { Id = "L5-goal", X = 7200, Y = 5000, W = 850, H = 850, TargetBlockId = id };
        labels["L5-label"] = new WorldLabel
        {
            Id = "L5-label", X = 5000, Y = 3300,
            Title = "Level 5 — Heavy heave",
            Subtitle = "Mass 2.6. Both cursors, both pulling hard the same way. Watch your pull numbers add up.",
        };
    }

    /// <summary>Level 6 — "Mirror match". Two light blocks, two pads — divide and conquer, a cursor
    /// each. Each block is solo-movable; doing both at once wants two players.</summary>
    private void SeedLevel6()
    {
        const string a = "L6-a", b = "L6-b";
        AddBlockLocked(new BlockState { Id = a, X = 3000, Y = 4200, W = 170, H = 170, Mass = 1.1, Color = "#7F77DD" });
        AddBlockLocked(new BlockState { Id = b, X = 3000, Y = 5800, W = 170, H = 170, Mass = 1.1, Color = "#D85A30" });
        goals["L6-ga"] = new GoalZone { Id = "L6-ga", X = 7000, Y = 4200, W = 550, H = 550, TargetBlockId = a };
        goals["L6-gb"] = new GoalZone { Id = "L6-gb", X = 7000, Y = 5800, W = 550, H = 550, TargetBlockId = b };
        labels["L6-label"] = new WorldLabel
        {
            Id = "L6-label", X = 5000, Y = 3300,
            Title = "Level 6 — Mirror match",
            Subtitle = "Two blocks, two pads. Take one each.",
        };
    }

    /// <summary>Level 7 — "Stand and slide". Slide a block down a narrow corridor to the pad at the
    /// far end. The corridor keeps it on rails; Mass 2 wants two cursors.</summary>
    private void SeedLevel7()
    {
        const string id = "L7-block";
        AddBlockLocked(new BlockState { Id = id, X = 2700, Y = 5000, W = 200, H = 200, Mass = 2.0, Color = "#D4537E" });
        AddWallLocked(new Wall { Id = "L7-top", X = 4500, Y = 4760, W = 4200, H = 120 });   // corridor inner
        AddWallLocked(new Wall { Id = "L7-bot", X = 4500, Y = 5240, W = 4200, H = 120 });   // y4820..5180
        goals["L7-goal"] = new GoalZone { Id = "L7-goal", X = 7100, Y = 5000, W = 600, H = 600, TargetBlockId = id };
        labels["L7-label"] = new WorldLabel
        {
            Id = "L7-label", X = 5000, Y = 3300,
            Title = "Level 7 — Stand and slide",
            Subtitle = "Keep it in the corridor and slide it to the far pad. Two cursors to move it.",
        };
    }

    /// <summary>Level 8 — "Block taxi". Taxi a block along a zig-zag route around two jutting walls
    /// to the far pad. Mass 2 wants two cursors steering together.</summary>
    private void SeedLevel8()
    {
        const string id = "L8-block";
        AddBlockLocked(new BlockState { Id = id, X = 2700, Y = 5000, W = 200, H = 200, Mass = 2.0, Color = "#7F77DD" });
        AddWallLocked(new Wall { Id = "L8-w1", X = 4800, Y = 4100, W = 200, H = 1700 });   // hangs from top to y4950
        AddWallLocked(new Wall { Id = "L8-w2", X = 5800, Y = 5900, W = 200, H = 1700 });   // rises from bottom to y5050
        goals["L8-goal"] = new GoalZone { Id = "L8-goal", X = 7200, Y = 5000, W = 700, H = 700, TargetBlockId = id };
        labels["L8-label"] = new WorldLabel
        {
            Id = "L8-label", X = 5000, Y = 3200,
            Title = "Level 8 — Block taxi",
            Subtitle = "Weave the block around the walls — below the first, above the second — to the pad.",
        };
    }

    /// <summary>Level 9 — "Couch corner". Carry a long bar through an L-bend hallway: rotate it from
    /// horizontal to vertical to turn the corner. Two cursors on the ends for the torque.</summary>
    private void SeedLevel9()
    {
        const string id = "L9-bar";
        AddShapeLocked(new ShapeActor
        {
            Id = id, X = 3000, Y = 5400, Mass = 2.0, Color = "#7FBF5A",
            Pieces = [new ShapePiece { LocalX = 0, LocalY = 0, HalfW = 280, HalfH = 70 }],
        });
        // Horizontal hallway (y≈5000, x2200..5400) meeting a vertical hallway (x≈5000, y up to 3200).
        AddWallLocked(new Wall { Id = "L9-h-top", X = 3800, Y = 4600, W = 3200, H = 120 });   // ceiling of horizontal run
        AddWallLocked(new Wall { Id = "L9-h-bot", X = 3500, Y = 5800, W = 2600, H = 120 });   // floor of horizontal run
        AddWallLocked(new Wall { Id = "L9-v-left", X = 4600, Y = 4000, W = 120, H = 2000 });  // left wall of vertical run
        AddWallLocked(new Wall { Id = "L9-v-right", X = 5800, Y = 3700, W = 120, H = 2400 });// right wall of vertical run
        AddShapeGoal(new ShapeGoal { Id = "L9-goal", X = 5200, Y = 3500, W = 1000, H = 1000, TargetShapeId = id });
        labels["L9-label"] = new WorldLabel
        {
            Id = "L9-label", X = 4000, Y = 2900,
            Title = "Level 9 — Couch corner",
            Subtitle = "Pivot the bar around the corner and up the hallway to the pad.",
        };
    }

    /// <summary>Level 10 — "Tug steady". A heavy straight drag where pulling opposite directions
    /// cancels — the lesson is to pull together, steadily, the same way.</summary>
    private void SeedLevel10()
    {
        const string id = "L10-block";
        AddBlockLocked(new BlockState { Id = id, X = 2800, Y = 5000, W = 280, H = 280, Mass = 2.6, Color = "#378ADD" });
        goals["L10-goal"] = new GoalZone { Id = "L10-goal", X = 7200, Y = 5000, W = 850, H = 850, TargetBlockId = id };
        labels["L10-label"] = new WorldLabel
        {
            Id = "L10-label", X = 5000, Y = 3300,
            Title = "Level 10 — Tug steady",
            Subtitle = "Same direction beats friction; opposite directions cancel. Don't fight your partner.",
        };
    }

    /// <summary>Level 11 — "Two-key lock". Two pads in separate lanes, a block for each — both must
    /// be parked. One cursor each; the divider keeps the lanes apart.</summary>
    private void SeedLevel11()
    {
        const string a = "L11-a", b = "L11-b";
        AddBlockLocked(new BlockState { Id = a, X = 3000, Y = 4300, W = 180, H = 180, Mass = 1.3, Color = "#1D9E75" });
        AddBlockLocked(new BlockState { Id = b, X = 3000, Y = 5700, W = 180, H = 180, Mass = 1.3, Color = "#1D9E75" });
        AddWallLocked(new Wall { Id = "L11-div", X = 5000, Y = 5000, W = 5400, H = 140 });   // lane divider
        goals["L11-ga"] = new GoalZone { Id = "L11-ga", X = 7000, Y = 4300, W = 550, H = 550, TargetBlockId = a };
        goals["L11-gb"] = new GoalZone { Id = "L11-gb", X = 7000, Y = 5700, W = 550, H = 550, TargetBlockId = b };
        labels["L11-label"] = new WorldLabel
        {
            Id = "L11-label", X = 5000, Y = 3200,
            Title = "Level 11 — Two-key lock",
            Subtitle = "Both pads, both lanes, at once. A cursor each.",
        };
    }

    /// <summary>Level 12 — "Spinner". A long bar that only fits the gap edge-on: rotate it vertical
    /// to thread the narrow slot, then onto the pad. Two cursors on the ends for the spin.</summary>
    private void SeedLevel12()
    {
        const string id = "L12-bar";
        AddShapeLocked(new ShapeActor
        {
            Id = id, X = 3000, Y = 5000, Mass = 2.0, Color = "#D85A30",
            Pieces = [new ShapePiece { LocalX = 0, LocalY = 0, HalfW = 350, HalfH = 80 }],
        });
        // A narrow vertical slot: the 700-long bar (160 thick) only passes the 600-wide gap upright.
        AddWallLocked(new Wall { Id = "L12-left", X = 4350, Y = 5000, W = 700, H = 1700 });   // gap from x4700
        AddWallLocked(new Wall { Id = "L12-right", X = 5650, Y = 5000, W = 700, H = 1700 });  // to x5300 (600 wide)
        AddShapeGoal(new ShapeGoal { Id = "L12-goal", X = 7000, Y = 5000, W = 1100, H = 1100, TargetShapeId = id });
        labels["L12-label"] = new WorldLabel
        {
            Id = "L12-label", X = 5000, Y = 3200,
            Title = "Level 12 — Spinner",
            Subtitle = "The bar only fits the slot end-on. Rotate it upright and thread it through.",
        };
    }

    /// <summary>Level 13 — "Door hold". Thread a block through a tight gap; it takes two cursors to
    /// control it steadily enough not to jam on the way through.</summary>
    private void SeedLevel13()
    {
        const string id = "L13-block";
        AddBlockLocked(new BlockState { Id = id, X = 2800, Y = 5000, W = 220, H = 220, Mass = 2.2, Color = "#7F77DD" });
        AddWallLocked(new Wall { Id = "L13-wt", X = 5000, Y = 4100, W = 240, H = 1500 });   // gap from y4850
        AddWallLocked(new Wall { Id = "L13-wb", X = 5000, Y = 5900, W = 240, H = 1500 });   // to y5150 (300 tall)
        goals["L13-goal"] = new GoalZone { Id = "L13-goal", X = 7200, Y = 5000, W = 700, H = 700, TargetBlockId = id };
        labels["L13-label"] = new WorldLabel
        {
            Id = "L13-label", X = 5000, Y = 3200,
            Title = "Level 13 — Door hold",
            Subtitle = "A tight gap. Control the block together and ease it through without snagging.",
        };
    }

    /// <summary>
    /// Level 14 — "Light the bulb". The first electronics puzzle: drag the loose wire ends onto
    /// terminals to build a series loop battery+ → resistor → bulb → battery−. The resistor must be
    /// in the loop (it "tames" the current) and the bulb in series, or it stays dark. Six terminals
    /// and three wires split naturally between two players. The seed for breadboards / a potato clock.
    /// </summary>
    private void SeedLevel14()
    {
        // Battery (left), with + post on top and − on bottom.
        AddTerminal(new Terminal { Id = "bat+", X = 3000, Y = 4650, Polarity = "pos" });
        AddTerminal(new Terminal { Id = "bat-", X = 3000, Y = 5350, Polarity = "neg" });
        AddComponent(new CircuitComponent
        {
            Id = "battery", Kind = "battery", X = 3000, Y = 5000, W = 360, H = 900,
            Label = "Battery", TerminalIds = ["bat+", "bat-"],
        });

        // Resistor (top middle), two terminals left/right.
        AddTerminal(new Terminal { Id = "r-a", X = 4700, Y = 3900 });
        AddTerminal(new Terminal { Id = "r-b", X = 5300, Y = 3900 });
        AddComponent(new CircuitComponent
        {
            Id = "resistor", Kind = "resistor", X = 5000, Y = 3900, W = 700, H = 200,
            Label = "Resistor", TerminalIds = ["r-a", "r-b"],
        });

        // Bulb (right), two terminals left/right.
        AddTerminal(new Terminal { Id = "b-a", X = 6500, Y = 4400 });
        AddTerminal(new Terminal { Id = "b-b", X = 7100, Y = 4400 });
        AddComponent(new CircuitComponent
        {
            Id = "bulb", Kind = "bulb", X = 6800, Y = 4400, W = 360, H = 360,
            Label = "Bulb", TerminalIds = ["b-a", "b-b"],
        });

        // Three loose wires, laid out low so both players can grab an end each.
        AddWire(new Wire { Id = "w1", Color = "#d98a3d", Ax = 3400, Ay = 6100, Bx = 3900, By = 6100 });
        AddWire(new Wire { Id = "w2", Color = "#caa472", Ax = 4600, Ay = 6300, Bx = 5100, By = 6300 });
        AddWire(new Wire { Id = "w3", Color = "#8fb3d9", Ax = 5800, Ay = 6100, Bx = 6300, By = 6100 });

        labels["L14-label"] = new WorldLabel
        {
            Id = "L14-label", X = 5000, Y = 2900,
            Title = "Level 14 — Light the bulb",
            Subtitle = "Drag the wire ends onto the posts: battery + → resistor → bulb → battery −. Light it up.",
        };
    }
}
