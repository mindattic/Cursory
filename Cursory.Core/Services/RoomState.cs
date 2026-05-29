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

    // ── state ─────────────────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, CursorState> cursors = new();
    private readonly ConcurrentDictionary<string, BlockState> blocks = new();
    private readonly ConcurrentDictionary<string, GoalZone> goals = new();
    private readonly ConcurrentDictionary<string, Wall> walls = new();
    private readonly ConcurrentDictionary<string, WorldLabel> labels = new();
    private readonly ConcurrentDictionary<string, ShapeActor> shapes = new();
    private readonly ConcurrentDictionary<string, ShapeGoal> shapeGoals = new();
    private readonly ConcurrentQueue<Whistle> whistles = new();
    private RoomVote? activeVote;
    private readonly Lock voteLock = new();
    /// <summary>Total number of seeded levels. Drives the UI dropdown. Three are engine-backed
    /// (two block levels + the first compound-shape level); the rest of commit 541d39e's fourteen
    /// get re-ported onto the engine as the feel is locked.</summary>
    public const int LevelCount = 3;
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
        // Solid cursor (authoritative — every client sees this copy). First sweep the path from
        // the previous position so a fast frame can't tunnel through a thin wall, then disc-eject
        // from walls and other shapes at the stopped point. The raw click used for grabbing is
        // separate, so you can still grab an edge; you don't collide with the shape you hold.
        (x, y) = SweepCursorAgainstWalls(c.X, c.Y, x, y);
        (x, y) = ResolveOutOfWalls(x, y);
        (c.X, c.Y) = ResolveOutOfShapes(x, y, c.AttachedShapeId);
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
    /// AABB ejection there, and rotate back. <paramref name="skipShapeId"/> is the shape this
    /// cursor is grabbing — never eject off your own grabbed body, that would fight the drag.</summary>
    private (double X, double Y) ResolveOutOfShapes(double x, double y, string? skipShapeId)
    {
        foreach (var s in shapes.Values)
        {
            if (s.Id == skipShapeId) continue;
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

    /// <summary>Release whatever this cursor is holding.</summary>
    public void Detach(string userId)
    {
        lock (worldLock) DetachLocked(userId);
    }

    private void DetachLocked(string userId)
    {
        if (grabByUser.Remove(userId, out var joint))
            world.Remove(joint);   // walls have no joint — only block grabs are in grabByUser
        if (cursors.TryGetValue(userId, out var c))
        {
            c.AttachedBlockId = null;
            c.AttachedShapeId = null;
            c.AttachedWallId = null;
            c.PullMass = 0;
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
            Whistles = whistles.Where(w => tick - w.Tick < WhistleBroadcastTicks).Select(CloneWhistle).ToList(),
            Vote = CurrentVote,
            CurrentLevel = CurrentLevel,
            LevelCount = LevelCount,
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
        AnchorLocalX = c.AnchorLocalX, AnchorLocalY = c.AnchorLocalY,
        AnchorWorldX = c.AnchorWorldX, AnchorWorldY = c.AnchorWorldY, PullMass = c.PullMass,
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
                    var cos = Math.Cos(ab.Angle);
                    var sin = Math.Sin(ab.Angle);
                    c.AnchorWorldX = ab.X + c.AnchorLocalX * cos - c.AnchorLocalY * sin;
                    c.AnchorWorldY = ab.Y + c.AnchorLocalX * sin + c.AnchorLocalY * cos;
                    LeashAndReport(c);
                }
                else if (c.AttachedShapeId is { } sid && shapes.TryGetValue(sid, out var ash))
                {
                    var cos = Math.Cos(ash.Angle);
                    var sin = Math.Sin(ash.Angle);
                    c.AnchorWorldX = ash.X + c.AnchorLocalX * cos - c.AnchorLocalY * sin;
                    c.AnchorWorldY = ash.Y + c.AnchorLocalX * sin + c.AnchorLocalY * cos;
                    LeashAndReport(c);
                }
                else if (c.AttachedWallId is { } wid && walls.TryGetValue(wid, out var aw))
                {
                    c.AnchorWorldX = aw.X + c.AnchorLocalX;
                    c.AnchorWorldY = aw.Y + c.AnchorLocalY;
                    LeashAndReport(c);
                }
                else
                {
                    c.PullMass = 0;
                }
            }
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
            case 3: SeedLevel3(); break;
            case 2: SeedLevel2(); break;
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
            blocks.Clear();
            goals.Clear();
            walls.Clear();
            labels.Clear();
            shapes.Clear();
            shapeGoals.Clear();
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
}
