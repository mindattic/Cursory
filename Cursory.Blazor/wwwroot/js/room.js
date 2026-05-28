// Cursory room client.
//
// Renders the shared room to a single HTML5 canvas, drag-pans a viewport over the
// 10 000 × 10 000 world, sends 30Hz cursor input to the server over SignalR, and
// applies authoritative WorldSnapshot frames as they arrive. Clicks on a block
// attach the local cursor (server-side); clicks in empty space fire a "whistle"
// that plays a tone locally and broadcasts a ripple to other clients.
//
// Designed to scale: every player sends ~30 byte messages at 30Hz and receives
// one snapshot at 30Hz containing all players, so per-client bandwidth grows
// linearly with room size — fine for ~100 players per node.

let connection = null;
let raf = 0;
let canvas = null;
let ctx = null;
let viewport = null;
let detachListeners = null;
let audioCtx = null;
// HUD elements cached once in start() — renderVote/syncLevelDropdown/renderStatus run at the
// 30 Hz snapshot rate, so re-resolving them by id every frame is needless DOM churn.
let els = {};

// Mirrors the server VoteKind enum (Cursory.Core.Models). Wire format is the numeric ordinal,
// so name it here rather than sprinkling magic numbers through the vote rendering.
const VoteKind = { Reset: 0, SelectLevel: 1 };

const state = {
    me: { userId: '', displayName: '', color: '#7F77DD' },
    world: { width: 10000, height: 10000 },
    cam: { x: 5000, y: 5000, z: 1 },
    pan: { active: false, sx: 0, sy: 0, ox: 0, oy: 0 },
    mouseWorld: { x: 5000, y: 5000 },
    // Static geometry — populated once via the "Geometry" message on hub connect.
    geometry: { walls: [], labels: [] },
    // Per-tick dynamic snapshot.
    snapshot: { tick: 0, cursors: [], blocks: [], goals: [], switches: [], doors: [], shapes: [], shapeGoals: [], whistles: [] },
    attachedBlockId: null,
    attachedShapeId: null,
    lastSent: 0,
    whistleAnims: [], // {x, y, color, t0}
    seenWhistles: new Map(), // "userId:tick" -> performance.now() first seen. A whistle rides
                             // several snapshots (broadcast window > ripple TTL) but should only
                             // play once; entries are pruned by age so an in-window whistle is
                             // never re-fired (a blunt full clear could do that).
    connection: { status: 'connecting' }, // 'connecting' | 'connected' | 'reconnecting' | 'disconnected'
};

export async function start(opts) {
    state.me.userId = opts.userId;
    state.me.displayName = opts.displayName;
    state.me.color = opts.color;
    state.world.width = opts.worldWidth;
    state.world.height = opts.worldHeight;
    state.cam.x = state.world.width / 2;
    state.cam.y = state.world.height / 2;

    canvas = document.getElementById('room-canvas');
    if (!canvas) { console.error('[cursory] #room-canvas missing'); return; }
    ctx = canvas.getContext('2d');
    viewport = document.getElementById('room-viewport');
    resize();
    window.addEventListener('resize', resize);

    // Audio context lazy-created on first user gesture (browsers block autoplay).
    const ensureAudio = () => {
        if (!audioCtx) {
            try { audioCtx = new (window.AudioContext || window.webkitAudioContext)(); }
            catch (e) { /* no audio is OK */ }
        }
    };

    // Click-vs-drag disambiguation: mousedown on empty space ARMS a pending whistle and
    // a pending pan. The first ≤5 px of movement is absorbed (still a click); past that
    // threshold we commit to a pan and cancel the whistle. On mouseup, if we never
    // crossed the threshold AND it happened within 250 ms, fire the whistle. This is
    // the same pattern Figma/Miro use: clicks shouldn't drag, drags shouldn't whistle.
    const DRAG_THRESHOLD_PX = 5;
    const CLICK_MAX_MS = 250;
    let armed = null;  // { startX, startY, startT, worldX, worldY } | null

    const onMouseDown = (e) => {
        ensureAudio();
        const world = clientToWorld(e.clientX, e.clientY);
        state.mouseWorld = world;
        // Shape hit-test runs first — compound rigid bodies sit "on top of" simple blocks
        // in the click-pick order. If neither hits, we fall through to whistle/pan.
        const shapeHit = pickShape(world.x, world.y);
        if (shapeHit && connection) {
            state.attachedShapeId = shapeHit.id;
            connection.invoke('GrabShape', shapeHit.id, world.x, world.y).catch(noop);
            return;
        }
        const hit = pickBlock(world.x, world.y);
        if (hit && connection) {
            state.attachedBlockId = hit.id;
            connection.invoke('Grab', hit.id, world.x, world.y).catch(noop);
            return;
        }
        // Empty-space mousedown — arm a pending whistle+pan; commit on movement or mouseup.
        armed = {
            startX: e.clientX, startY: e.clientY, startT: performance.now(),
            worldX: world.x, worldY: world.y,
        };
        state.pan.sx = e.clientX; state.pan.sy = e.clientY;
        state.pan.ox = state.cam.x; state.pan.oy = state.cam.y;
    };
    const onMouseMove = (e) => {
        if (armed && !state.pan.active) {
            const dx = e.clientX - armed.startX;
            const dy = e.clientY - armed.startY;
            if (dx * dx + dy * dy >= DRAG_THRESHOLD_PX * DRAG_THRESHOLD_PX) {
                // Crossed the drag threshold — commit to pan; the click is no longer a whistle.
                state.pan.active = true;
                armed = null;
                viewport.classList.add('dragging');
            }
        }
        if (state.pan.active) {
            const dx = (e.clientX - state.pan.sx) / state.cam.z;
            const dy = (e.clientY - state.pan.sy) / state.cam.z;
            state.cam.x = clamp(state.pan.ox - dx, 0, state.world.width);
            state.cam.y = clamp(state.pan.oy - dy, 0, state.world.height);
        }
        const world = clientToWorld(e.clientX, e.clientY);
        state.mouseWorld = world;
    };
    const onMouseUp = () => {
        if (state.pan.active) {
            state.pan.active = false;
            viewport.classList.remove('dragging');
        } else if (armed) {
            // Released without crossing the drag threshold — fire the whistle if still snappy.
            const dt = performance.now() - armed.startT;
            if (dt <= CLICK_MAX_MS) {
                playWhistle(state.me.color);
                state.whistleAnims.push({ x: armed.worldX, y: armed.worldY, color: state.me.color, t0: performance.now() });
                if (connection) connection.invoke('Whistle', armed.worldX, armed.worldY).catch(noop);
            }
        }
        armed = null;
        if ((state.attachedBlockId || state.attachedShapeId) && connection) {
            state.attachedBlockId = null;
            state.attachedShapeId = null;
            connection.invoke('Release').catch(noop);
        }
    };

    // Wheel zoom, anchored at the cursor: the world point under the pointer stays put as the
    // zoom changes, so zooming feels like leaning in rather than recentering. Clamped so you
    // can pull back to roughly the whole 10k world and push in to ~4×.
    const ZOOM_MIN = 0.1, ZOOM_MAX = 4;
    const onWheel = (e) => {
        e.preventDefault();
        const before = clientToWorld(e.clientX, e.clientY);
        const factor = e.deltaY < 0 ? 1.1 : 1 / 1.1;
        state.cam.z = clamp(state.cam.z * factor, ZOOM_MIN, ZOOM_MAX);
        const after = clientToWorld(e.clientX, e.clientY);
        state.cam.x = clamp(state.cam.x + (before.x - after.x), 0, state.world.width);
        state.cam.y = clamp(state.cam.y + (before.y - after.y), 0, state.world.height);
        state.mouseWorld = clientToWorld(e.clientX, e.clientY);
    };

    // Losing window focus: a mouseup can land off-canvas and never reach us, leaving a pan or
    // grab stuck "held". Clear interaction state (and tell the server to release) so returning
    // to the tab is clean rather than dragging whatever was under the cursor when you left.
    const onWindowBlur = () => {
        if (state.pan.active) { state.pan.active = false; if (viewport) viewport.classList.remove('dragging'); }
        armed = null;
        if ((state.attachedBlockId || state.attachedShapeId) && connection) {
            state.attachedBlockId = null;
            state.attachedShapeId = null;
            connection.invoke('Release').catch(noop);
        }
    };
    // Regaining focus / visibility: while the tab was hidden the browser parks
    // requestAnimationFrame, and the in-flight frame may never fire — which breaks the
    // self-rescheduling loop and leaves the canvas frozen (cursor not redrawn) on return.
    // Cancel any stale handle and kick a fresh frame so it repaints immediately.
    const onWindowFocus = () => {
        if (document.hidden) return;
        cancelAnimationFrame(raf);
        raf = requestAnimationFrame(loop);
    };

    canvas.addEventListener('mousedown', onMouseDown);
    canvas.addEventListener('wheel', onWheel, { passive: false });
    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);
    window.addEventListener('blur', onWindowBlur);
    window.addEventListener('focus', onWindowFocus);
    document.addEventListener('visibilitychange', onWindowFocus);

    // Cache the HUD elements once. Hot-path renderers (renderVote/syncLevelDropdown/renderStatus)
    // read from here instead of re-querying the DOM at 30 Hz.
    els = {
        status:       document.getElementById('room-status'),
        levelSelect:  document.getElementById('room-level-select'),
        voteOverlay:  document.getElementById('room-vote-overlay'),
        voteTitle:    document.getElementById('room-vote-title'),
        voteYes:      document.getElementById('room-vote-yes'),
        voteNo:       document.getElementById('room-vote-no'),
        voteNeed:     document.getElementById('room-vote-need'),
        voteYesBtn:   document.getElementById('room-vote-yes-btn'),
        voteNoBtn:    document.getElementById('room-vote-no-btn'),
        banner:       document.getElementById('room-level-banner'),
        bannerTitle:  document.getElementById('room-level-banner-title'),
        bannerSub:    document.getElementById('room-level-banner-sub'),
    };

    // HUD wiring — Reset button + level dropdown + vote overlay.
    const resetBtn = document.getElementById('room-reset-btn');
    const levelSelect = els.levelSelect;
    const voteYesBtn = els.voteYesBtn;
    const voteNoBtn  = els.voteNoBtn;
    const onResetClick  = () => { if (connection) connection.invoke('StartResetVote').catch(noop); };
    const onLevelChange = (e) => {
        const lvl = parseInt(e.target.value, 10);
        if (lvl && connection) connection.invoke('StartLevelVote', lvl).catch(noop);
    };
    const onVoteYes = () => { if (connection) connection.invoke('CastVote', true).catch(noop); };
    const onVoteNo  = () => { if (connection) connection.invoke('CastVote', false).catch(noop); };
    if (resetBtn)    resetBtn.addEventListener('click', onResetClick);
    if (levelSelect) levelSelect.addEventListener('change', onLevelChange);
    if (voteYesBtn)  voteYesBtn.addEventListener('click', onVoteYes);
    if (voteNoBtn)   voteNoBtn.addEventListener('click', onVoteNo);

    detachListeners = () => {
        canvas.removeEventListener('mousedown', onMouseDown);
        canvas.removeEventListener('wheel', onWheel);
        window.removeEventListener('mousemove', onMouseMove);
        window.removeEventListener('mouseup', onMouseUp);
        window.removeEventListener('blur', onWindowBlur);
        window.removeEventListener('focus', onWindowFocus);
        document.removeEventListener('visibilitychange', onWindowFocus);
        window.removeEventListener('resize', resize);
        if (resetBtn)    resetBtn.removeEventListener('click', onResetClick);
        if (levelSelect) levelSelect.removeEventListener('change', onLevelChange);
        if (voteYesBtn)  voteYesBtn.removeEventListener('click', onVoteYes);
        if (voteNoBtn)   voteNoBtn.removeEventListener('click', onVoteNo);
    };

    // SignalR connection.
    if (!window.signalR) { console.error('[cursory] signalR client not loaded'); return; }
    connection = new window.signalR.HubConnectionBuilder()
        .withUrl('/hubs/room')
        .withAutomaticReconnect()
        .build();
    connection.onreconnecting(() => { state.connection.status = 'reconnecting'; renderStatus(); });
    connection.onreconnected(()  => { state.connection.status = 'connected';    renderStatus(); });
    connection.onclose(()        => { state.connection.status = 'disconnected'; renderStatus(); });
    connection.on('Geometry', (msg) => {
        state.geometry.walls = msg.walls || [];
        state.geometry.labels = msg.labels || [];
        if (msg.worldWidth)  state.world.width  = msg.worldWidth;
        if (msg.worldHeight) state.world.height = msg.worldHeight;
    });
    connection.on('LevelLoaded', (level) => showLevelBanner(level));
    connection.on('Snapshot', (snap) => {
        state.snapshot = snap;
        renderVote(snap.vote);
        syncLevelDropdown(snap.currentLevel, snap.levelCount);
        // Drive a local whistle anim for any whistles we didn't fire ourselves (others' clicks).
        // De-dup on a stable (userId, tick) key, not position+colour: the server rebroadcasts a
        // whistle for ~1 s but the ripple only lives 700 ms, so a position-match check would
        // re-fire the tone once the ripple has aged out. The key set is bounded below.
        if (snap.whistles && snap.whistles.length) {
            const nowp = performance.now();
            for (const w of snap.whistles) {
                if (w.userId === state.me.userId) continue;
                const key = w.userId + ':' + w.tick;
                if (state.seenWhistles.has(key)) continue;
                state.seenWhistles.set(key, nowp);
                state.whistleAnims.push({ x: w.x, y: w.y, color: w.color, t0: nowp });
                playWhistle(w.color);
            }
            // Bound the dedup map by AGE, not a hard clear: drop keys older than the broadcast
            // window (~1 s) plus slack, so a whistle still being rebroadcast can never replay.
            if (state.seenWhistles.size > 256) {
                for (const [k, t] of state.seenWhistles) {
                    if (nowp - t > 2000) state.seenWhistles.delete(k);
                }
            }
        }
    });
    try {
        await connection.start();
        state.connection.status = 'connected';
        renderStatus();
    } catch (e) {
        console.error('[cursory] hub connect failed', e);
        state.connection.status = 'disconnected';
        renderStatus();
    }

    raf = requestAnimationFrame(loop);
}

export async function stop() {
    cancelAnimationFrame(raf); raf = 0;
    if (detachListeners) { detachListeners(); detachListeners = null; }
    if (connection) { try { await connection.stop(); } catch {} connection = null; }
    // Drop cached element refs + derived UI state so a fresh start() re-resolves against the
    // newly rendered DOM rather than holding handles to detached nodes.
    els = {};
    _lastSeenLevel = 0;
    _lastLevelCount = 0;
}

function loop(now) {
    sendInput(now);
    render(now);
    raf = requestAnimationFrame(loop);
}

function sendInput(now) {
    if (!connection || connection.state !== window.signalR.HubConnectionState.Connected) return;
    if (now - state.lastSent < 33) return; // ~30Hz
    state.lastSent = now;
    connection.invoke('Move', state.mouseWorld.x, state.mouseWorld.y).catch(noop);
}

function render(now) {
    if (!ctx) return;
    const w = canvas.width, h = canvas.height;
    ctx.fillStyle = '#0c0c0c';
    ctx.fillRect(0, 0, w, h);

    // World → canvas transform.
    const z = state.cam.z;
    ctx.save();
    ctx.translate(w / 2, h / 2);
    ctx.scale(z, z);
    ctx.translate(-state.cam.x, -state.cam.y);

    drawGrid();
    drawLabels();
    drawGoals();
    drawShapeGoals();
    drawSwitches();
    drawWalls();
    drawDoors();
    drawBlocks();
    drawShapes();
    drawAttachLines();
    drawShapeAttachLines();
    drawCursors();
    drawWhistles(now);

    ctx.restore();
    drawMinimap();
}

function drawGrid() {
    const step = 200;
    const W = state.world.width, H = state.world.height;
    ctx.strokeStyle = 'rgba(255,255,255,0.04)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let x = 0; x <= W; x += step) { ctx.moveTo(x, 0); ctx.lineTo(x, H); }
    for (let y = 0; y <= H; y += step) { ctx.moveTo(0, y); ctx.lineTo(W, y); }
    ctx.stroke();
    // World boundary.
    ctx.strokeStyle = 'rgba(255,255,255,0.2)';
    ctx.lineWidth = 4;
    ctx.strokeRect(0, 0, W, H);
}

function drawLabels() {
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    for (const l of state.geometry.labels) {
        ctx.fillStyle = 'rgba(255,255,255,0.85)';
        ctx.font = '600 36px system-ui';
        ctx.fillText(l.title, l.x, l.y);
        ctx.fillStyle = 'rgba(255,255,255,0.45)';
        ctx.font = '18px system-ui';
        ctx.fillText(l.subtitle, l.x, l.y + 42);
    }
}

function drawGoals() {
    for (const g of state.snapshot.goals || []) {
        ctx.fillStyle = g.isSolved ? 'rgba(29,158,117,0.35)' : 'rgba(127,119,221,0.15)';
        ctx.strokeStyle = g.isSolved ? 'rgba(29,158,117,0.9)' : 'rgba(127,119,221,0.6)';
        ctx.lineWidth = 3;
        ctx.fillRect(g.x - g.w / 2, g.y - g.h / 2, g.w, g.h);
        ctx.strokeRect(g.x - g.w / 2, g.y - g.h / 2, g.w, g.h);
        ctx.fillStyle = 'rgba(255,255,255,0.55)';
        ctx.font = '24px system-ui';
        ctx.textAlign = 'center';
        ctx.fillText(g.isSolved ? 'SOLVED' : 'GOAL', g.x, g.y - g.h / 2 - 12);
    }
}

function drawBlocks() {
    for (const b of state.snapshot.blocks || []) {
        ctx.fillStyle = b.color || '#3a3a3a';
        ctx.strokeStyle = 'rgba(255,255,255,0.2)';
        ctx.lineWidth = 2;
        // Blocks are real rigid bodies now — draw them in their own rotated frame.
        ctx.save();
        ctx.translate(b.x, b.y);
        ctx.rotate(b.angle || 0);
        ctx.fillRect(-b.w / 2, -b.h / 2, b.w, b.h);
        ctx.strokeRect(-b.w / 2, -b.h / 2, b.w, b.h);
        ctx.restore();
    }
}

// Render position of a cursor. The local player's own cursor is predicted at the live mouse
// position (state.mouseWorld) instead of the server snapshot, which lags ~1 RTT + a tick — so
// your arrow tracks your hand instead of trailing it. Remote cursors stay authoritative.
function cursorRenderPos(c) {
    if (c.userId === state.me.userId) return { x: state.mouseWorld.x, y: state.mouseWorld.y };
    return { x: c.x, y: c.y };
}

// World position of a cursor's grab anchor on a (rotated) block.
function blockAnchorWorld(b, c) {
    const cos = Math.cos(b.angle || 0), sin = Math.sin(b.angle || 0);
    return [
        b.x + c.anchorLocalX * cos - c.anchorLocalY * sin,
        b.y + c.anchorLocalX * sin + c.anchorLocalY * cos,
    ];
}

// The rendered pull line stops at the distance where a grab hits its max force, so a
// player can read how hard they're pulling: past MAX_PULL_PX, pulling farther adds no force.
const MAX_PULL_PX = 240;

function drawWalls() {
    for (const w of state.geometry.walls) {
        ctx.fillStyle = '#2a2a2a';
        ctx.strokeStyle = 'rgba(255,255,255,0.18)';
        ctx.lineWidth = 2;
        ctx.fillRect(w.x - w.w / 2, w.y - w.h / 2, w.w, w.h);
        ctx.strokeRect(w.x - w.w / 2, w.y - w.h / 2, w.w, w.h);
    }
}

function drawDoors() {
    for (const d of state.snapshot.doors || []) {
        if (d.isOpen) {
            ctx.fillStyle = 'rgba(29,158,117,0.15)';
            ctx.strokeStyle = 'rgba(29,158,117,0.7)';
            ctx.setLineDash([10, 8]);
        } else {
            ctx.fillStyle = '#5a2e22';
            ctx.strokeStyle = 'rgba(216,90,48,0.9)';
            ctx.setLineDash([]);
        }
        ctx.lineWidth = 3;
        ctx.fillRect(d.x - d.w / 2, d.y - d.h / 2, d.w, d.h);
        ctx.strokeRect(d.x - d.w / 2, d.y - d.h / 2, d.w, d.h);
        ctx.setLineDash([]);
    }
}

function drawSwitches() {
    for (const s of state.snapshot.switches || []) {
        const cx = s.x, cy = s.y;
        const r = Math.min(s.w, s.h) / 2 - 8;
        // Pad outline
        ctx.strokeStyle = withAlpha(s.color, s.isActive ? 1 : 0.5);
        ctx.lineWidth = 4;
        ctx.beginPath();
        ctx.arc(cx, cy, r, 0, Math.PI * 2);
        ctx.stroke();
        // Fill when active
        if (s.isActive) {
            ctx.fillStyle = withAlpha(s.color, 0.35);
            ctx.beginPath();
            ctx.arc(cx, cy, r - 4, 0, Math.PI * 2);
            ctx.fill();
        }
        // Required-count indicator (e.g. "1/2" cursors inside)
        ctx.fillStyle = 'rgba(255,255,255,0.85)';
        ctx.font = '20px system-ui';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(`${s.cursorsInside}/${s.requiredCount}`, cx, cy);
    }
}

function drawAttachLines() {
    // For each attached cursor, draw a line from anchor (in world) to cursor.
    const blockById = new Map();
    for (const b of state.snapshot.blocks || []) blockById.set(b.id, b);
    for (const c of state.snapshot.cursors || []) {
        if (!c.attachedBlockId) continue;
        const b = blockById.get(c.attachedBlockId);
        if (!b) continue;
        const p = cursorRenderPos(c);
        const [ax, ay] = blockAnchorWorld(b, c);
        // Truncate the line at the max-force reach. The full segment would run anchor → cursor;
        // we draw at most MAX_PULL_PX of it so an over-stretched pull reads as "maxed out".
        const dx = p.x - ax, dy = p.y - ay;
        const dist = Math.hypot(dx, dy) || 1;
        const reach = Math.min(dist, MAX_PULL_PX);
        const ex = ax + (dx / dist) * reach, ey = ay + (dy / dist) * reach;
        const maxed = dist > MAX_PULL_PX;
        ctx.strokeStyle = withAlpha(c.color, maxed ? 1 : 0.7);
        ctx.lineWidth = maxed ? 3 : 2;
        ctx.beginPath();
        ctx.moveTo(ax, ay);
        ctx.lineTo(ex, ey);
        ctx.stroke();
        // Anchor dot.
        ctx.fillStyle = c.color;
        ctx.beginPath();
        ctx.arc(ax, ay, 4, 0, Math.PI * 2);
        ctx.fill();
        // Max-reach cap marker when the player is pulling at full force.
        if (maxed) {
            ctx.beginPath();
            ctx.arc(ex, ey, 5, 0, Math.PI * 2);
            ctx.stroke();
        }
    }
}

function drawCursors() {
    // Pre-build lookup maps so we don't pay an O(N) find inside the per-cursor render
    // loop. Same idea covers both attachment kinds: blocks (axis-aligned) and shapes
    // (rotated rigid bodies).
    const blockById = new Map();
    for (const b of state.snapshot.blocks || []) blockById.set(b.id, b);
    const shapeById = new Map();
    for (const s of state.snapshot.shapes || []) shapeById.set(s.id, s);
    for (const c of state.snapshot.cursors || []) {
        const isMe = c.userId === state.me.userId;
        const p = cursorRenderPos(c);
        // Rotation: if attached, point inward at anchor; otherwise point up.
        let rot = 0;
        if (c.attachedBlockId) {
            const b = blockById.get(c.attachedBlockId);
            if (b) {
                const [ax, ay] = blockAnchorWorld(b, c);
                rot = Math.atan2(ay - p.y, ax - p.x);
            }
        } else if (c.attachedShapeId) {
            const s = shapeById.get(c.attachedShapeId);
            if (s) {
                const cos = Math.cos(s.angle), sin = Math.sin(s.angle);
                const ax = s.x + c.anchorLocalX * cos - c.anchorLocalY * sin;
                const ay = s.y + c.anchorLocalX * sin + c.anchorLocalY * cos;
                rot = Math.atan2(ay - p.y, ax - p.x);
            }
        }
        drawArrow(p.x, p.y, rot, c.color, isMe);
        // Name tag. Set the font BEFORE measuring — measureText uses the current ctx.font, so
        // measuring first would size the background box with whatever font the last draw call
        // left set (e.g. the 20px switch label), producing a box that doesn't fit the text.
        ctx.font = '12px system-ui';
        ctx.textAlign = 'left';
        ctx.textBaseline = 'top';
        ctx.fillStyle = withAlpha('#000000', 0.6);
        ctx.fillRect(p.x + 18, p.y + 6, ctx.measureText(c.displayName).width + 10, 18);
        ctx.fillStyle = c.color;
        ctx.fillText(c.displayName, p.x + 23, p.y + 9);
    }
}

function drawArrow(x, y, rot, color, isMe) {
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(rot);
    ctx.fillStyle = color;
    ctx.strokeStyle = isMe ? 'white' : 'rgba(0,0,0,0.5)';
    ctx.lineWidth = isMe ? 2 : 1;
    ctx.beginPath();
    ctx.moveTo(0, 0);
    ctx.lineTo(20, 7);
    ctx.lineTo(8, 10);
    ctx.lineTo(5, 22);
    ctx.closePath();
    ctx.fill();
    ctx.stroke();
    ctx.restore();
}

function drawWhistles(now) {
    const ttl = 700;
    state.whistleAnims = state.whistleAnims.filter(a => now - a.t0 < ttl);
    for (const a of state.whistleAnims) {
        const t = (now - a.t0) / ttl;
        const r = 20 + t * 120;
        ctx.strokeStyle = withAlpha(a.color, 0.7 * (1 - t));
        ctx.lineWidth = 3;
        ctx.beginPath();
        ctx.arc(a.x, a.y, r, 0, Math.PI * 2);
        ctx.stroke();
    }
}

function drawMinimap() {
    const W = state.world.width, H = state.world.height;
    const mw = 160, mh = 160 * (H / W);
    const pad = 12;
    const mx = canvas.width - mw - pad, my = canvas.height - mh - pad;
    const sx = mw / W, sy = mh / H;
    // World point → minimap point.
    const px = (wx) => mx + wx * sx;
    const py = (wy) => my + wy * sy;
    // World rect (centre x/y, size w/h) → minimap rect, with a 2-px floor so small geometry
    // doesn't vanish at this scale.
    const fillCentred = (x, y, w, h) => ctx.fillRect(
        px(x - w / 2), py(y - h / 2), Math.max(2, w * sx), Math.max(2, h * sy));

    ctx.fillStyle = 'rgba(0,0,0,0.5)';
    ctx.strokeStyle = 'rgba(255,255,255,0.2)';
    ctx.lineWidth = 1;
    ctx.fillRect(mx, my, mw, mh);
    ctx.strokeRect(mx, my, mw, mh);

    // Clip everything below to the minimap box so nothing bleeds outside it.
    ctx.save();
    ctx.beginPath();
    ctx.rect(mx, my, mw, mh);
    ctx.clip();

    // Goals (block + shape) as targets — green once solved, faint accent otherwise.
    for (const g of state.snapshot.goals || []) {
        ctx.fillStyle = g.isSolved ? 'rgba(29,158,117,0.8)' : 'rgba(127,119,221,0.5)';
        fillCentred(g.x, g.y, g.w, g.h);
    }
    for (const g of state.snapshot.shapeGoals || []) {
        ctx.fillStyle = g.isSolved ? 'rgba(29,158,117,0.8)' : 'rgba(216,90,48,0.4)';
        fillCentred(g.x, g.y, g.w, g.h);
    }
    // Walls.
    ctx.fillStyle = 'rgba(170,170,170,0.8)';
    for (const w of state.geometry.walls) fillCentred(w.x, w.y, w.w, w.h);
    // Doors — coloured by open/closed.
    for (const d of state.snapshot.doors || []) {
        ctx.fillStyle = d.isOpen ? 'rgba(29,158,117,0.6)' : 'rgba(216,90,48,0.9)';
        fillCentred(d.x, d.y, d.w, d.h);
    }
    // Blocks.
    for (const b of state.snapshot.blocks || []) {
        ctx.fillStyle = b.color || '#888';
        fillCentred(b.x, b.y, b.w, b.h);
    }
    // Compound shapes — drop a dot at each body centre (pieces are tiny at minimap scale).
    for (const s of state.snapshot.shapes || []) {
        ctx.fillStyle = s.color || '#D85A30';
        ctx.fillRect(px(s.x) - 2, py(s.y) - 2, 4, 4);
    }

    // Viewport rect (clamped by the clip above).
    const z = state.cam.z;
    const vw = (canvas.width / z) * sx;
    const vh = (canvas.height / z) * sy;
    const vx = px(state.cam.x - canvas.width / (2 * z));
    const vy = py(state.cam.y - canvas.height / (2 * z));
    ctx.strokeStyle = 'rgba(255,255,255,0.5)';
    ctx.lineWidth = 1;
    ctx.strokeRect(vx, vy, vw, vh);

    // Cursors as dots, on top.
    for (const c of state.snapshot.cursors || []) {
        ctx.fillStyle = c.color;
        ctx.fillRect(px(c.x) - 1, py(c.y) - 1, 3, 3);
    }
    ctx.restore();
}

function drawShapeGoals() {
    for (const g of state.snapshot.shapeGoals || []) {
        ctx.fillStyle = g.isSolved ? 'rgba(29,158,117,0.35)' : 'rgba(216,90,48,0.10)';
        ctx.strokeStyle = g.isSolved ? 'rgba(29,158,117,0.9)' : 'rgba(216,90,48,0.55)';
        ctx.lineWidth = 4;
        ctx.setLineDash(g.isSolved ? [] : [16, 10]);
        ctx.fillRect(g.x - g.w / 2, g.y - g.h / 2, g.w, g.h);
        ctx.strokeRect(g.x - g.w / 2, g.y - g.h / 2, g.w, g.h);
        ctx.setLineDash([]);
        ctx.fillStyle = 'rgba(255,255,255,0.55)';
        ctx.font = '28px system-ui';
        ctx.textAlign = 'center';
        ctx.fillText(g.isSolved ? 'SOLVED' : 'TARGET', g.x, g.y - g.h / 2 - 16);
    }
}

function drawShapes() {
    for (const s of state.snapshot.shapes || []) {
        ctx.save();
        ctx.translate(s.x, s.y);
        ctx.rotate(s.angle);
        for (const p of s.pieces || []) {
            ctx.fillStyle = s.color || '#D85A30';
            ctx.strokeStyle = 'rgba(255,255,255,0.25)';
            ctx.lineWidth = 2;
            ctx.fillRect(p.localX - p.halfW, p.localY - p.halfH, p.halfW * 2, p.halfH * 2);
            ctx.strokeRect(p.localX - p.halfW, p.localY - p.halfH, p.halfW * 2, p.halfH * 2);
        }
        // A tiny dot at the body centre to make rotation visible at a glance.
        ctx.fillStyle = 'rgba(255,255,255,0.4)';
        ctx.beginPath();
        ctx.arc(0, 0, 4, 0, Math.PI * 2);
        ctx.fill();
        ctx.restore();
    }
}

function drawShapeAttachLines() {
    // Dotted pull line: anchor → cursor. Longer = harder pull. Visual cue mentioned
    // in the design brief; doubles as a debug overlay so the spring force is legible.
    const shapeById = new Map();
    for (const s of state.snapshot.shapes || []) shapeById.set(s.id, s);
    for (const c of state.snapshot.cursors || []) {
        if (!c.attachedShapeId) continue;
        const s = shapeById.get(c.attachedShapeId);
        if (!s) continue;
        const p = cursorRenderPos(c);
        const cos = Math.cos(s.angle), sin = Math.sin(s.angle);
        const ax = s.x + c.anchorLocalX * cos - c.anchorLocalY * sin;
        const ay = s.y + c.anchorLocalX * sin + c.anchorLocalY * cos;
        const dist = Math.hypot(p.x - ax, p.y - ay);
        // Thicker + brighter as distance grows. Scaled so a ~150-unit pull is fully saturated.
        const t = Math.min(1, dist / 200);
        ctx.setLineDash([14, 10]);
        ctx.lineDashOffset = -(performance.now() / 30) % 24;
        ctx.strokeStyle = withAlpha(c.color, 0.35 + 0.55 * t);
        ctx.lineWidth = 2 + 4 * t;
        ctx.beginPath();
        ctx.moveTo(ax, ay);
        ctx.lineTo(p.x, p.y);
        ctx.stroke();
        ctx.setLineDash([]);
        ctx.lineDashOffset = 0;
        // Anchor knob
        ctx.fillStyle = c.color;
        ctx.beginPath();
        ctx.arc(ax, ay, 5, 0, Math.PI * 2);
        ctx.fill();
    }
}

function pickShape(wx, wy) {
    // World point inside any piece of any shape (after rotation). Pieces are AABBs in
    // body-local space, so the hit test is: inverse-rotate the world point into the body
    // frame, then standard AABB test against each piece. Returns the shape (not the piece).
    for (const s of state.snapshot.shapes || []) {
        const cos = Math.cos(-s.angle), sin = Math.sin(-s.angle);
        const dx = wx - s.x, dy = wy - s.y;
        const lx = dx * cos - dy * sin;
        const ly = dx * sin + dy * cos;
        for (const p of s.pieces || []) {
            if (lx > p.localX - p.halfW && lx < p.localX + p.halfW &&
                ly > p.localY - p.halfH && ly < p.localY + p.halfH) {
                return s;
            }
        }
    }
    return null;
}

function pickBlock(wx, wy) {
    // Blocks rotate now, so inverse-rotate the click into the block's body frame before the
    // AABB test (same trick as pickShape).
    for (const b of state.snapshot.blocks || []) {
        const cos = Math.cos(-(b.angle || 0)), sin = Math.sin(-(b.angle || 0));
        const dx = wx - b.x, dy = wy - b.y;
        const lx = dx * cos - dy * sin;
        const ly = dx * sin + dy * cos;
        if (lx > -b.w / 2 && lx < b.w / 2 && ly > -b.h / 2 && ly < b.h / 2) return b;
    }
    return null;
}

function clientToWorld(cx, cy) {
    const rect = canvas.getBoundingClientRect();
    const lx = (cx - rect.left) * (canvas.width / rect.width);
    const ly = (cy - rect.top) * (canvas.height / rect.height);
    const z = state.cam.z;
    return {
        x: state.cam.x + (lx - canvas.width / 2) / z,
        y: state.cam.y + (ly - canvas.height / 2) / z,
    };
}

function resize() {
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.max(1, Math.floor(rect.width * dpr));
    canvas.height = Math.max(1, Math.floor(rect.height * dpr));
}

function playWhistle(color) {
    if (!audioCtx) return;
    try {
        const now = audioCtx.currentTime;
        const osc = audioCtx.createOscillator();
        const gain = audioCtx.createGain();
        // Pitch keyed deterministically by color hex so each player has a distinct whistle.
        const baseHz = 440 + ((hashStr(color) % 12) * 35);
        osc.type = 'sine';
        osc.frequency.setValueAtTime(baseHz, now);
        osc.frequency.exponentialRampToValueAtTime(baseHz * 1.5, now + 0.08);
        osc.frequency.exponentialRampToValueAtTime(baseHz * 0.9, now + 0.35);
        gain.gain.setValueAtTime(0.0001, now);
        gain.gain.exponentialRampToValueAtTime(0.12, now + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.4);
        osc.connect(gain).connect(audioCtx.destination);
        osc.start(now);
        osc.stop(now + 0.45);
    } catch (e) { /* audio failure should never break the room */ }
}

function renderVote(vote) {
    const overlay = els.voteOverlay;
    if (!overlay) return;
    if (!vote) {
        overlay.setAttribute('hidden', '');
        return;
    }
    overlay.removeAttribute('hidden');
    if (els.voteTitle) {
        if (vote.kind === VoteKind.SelectLevel)
            els.voteTitle.textContent = `Switch to Level ${vote.targetLevel}?`;
        else
            els.voteTitle.textContent = 'Reset the level?';
    }
    if (els.voteYes)  els.voteYes.textContent  = vote.yesUserIds.length;
    if (els.voteNo)   els.voteNo.textContent   = vote.noUserIds.length;
    if (els.voteNeed) els.voteNeed.textContent = vote.quorum;
    // Disable the buttons if I've already voted on this round.
    const alreadyVoted =
        vote.yesUserIds.includes(state.me.userId) ||
        vote.noUserIds.includes(state.me.userId);
    if (els.voteYesBtn) els.voteYesBtn.disabled = alreadyVoted;
    if (els.voteNoBtn)  els.voteNoBtn.disabled  = alreadyVoted;
}

let _lastSeenLevel = 0;
let _lastLevelCount = 0;
function syncLevelDropdown(level, levelCount) {
    if (!level) return;
    const sel = els.levelSelect;
    if (sel) {
        // Hide options the server can't actually load (only LevelCount levels are seeded);
        // otherwise picking 3–14 fires a level vote the server silently rejects. Re-prune only
        // when the count changes so we're not touching the DOM every snapshot.
        if (levelCount && levelCount !== _lastLevelCount) {
            for (const opt of sel.options) {
                opt.hidden = Number(opt.value) > levelCount;
            }
            _lastLevelCount = levelCount;
        }
        const target = String(level);
        if (sel.value !== target) sel.value = target;
    }
    // Pop the level banner whenever the active level changes — covers the case where
    // a client connects mid-game or the server-broadcast LevelLoaded event was missed.
    if (level !== _lastSeenLevel) {
        if (_lastSeenLevel !== 0) showLevelBanner(level);
        _lastSeenLevel = level;
    }
}

function showLevelBanner(level) {
    const labels = state.geometry.labels || [];
    const match = labels.find(l => (l.id || '').startsWith(`L${level}-label`));
    const banner = els.banner;
    const titleEl = els.bannerTitle;
    const subEl   = els.bannerSub;
    if (!banner || !titleEl || !subEl) return;
    titleEl.textContent = match ? match.title : `Level ${level}`;
    subEl.textContent   = match ? match.subtitle : '';
    // Re-trigger the CSS animation by removing + re-adding the element class.
    banner.removeAttribute('hidden');
    banner.style.animation = 'none';
    void banner.offsetWidth;        // force reflow
    banner.style.animation = '';
    clearTimeout(banner._hideTimer);
    banner._hideTimer = setTimeout(() => banner.setAttribute('hidden', ''), 3500);
}

function renderStatus() {
    const el = els.status;
    if (!el) return;
    const s = state.connection.status;
    el.className = 'room-status room-status-' + s;
    el.textContent = s === 'connected' ? 'Live'
        : s === 'reconnecting' ? 'Reconnecting…'
        : s === 'disconnected' ? 'Offline'
        : 'Connecting…';
}

function hashStr(s) { let h = 0; for (let i = 0; i < s.length; i++) h = ((h << 5) - h + s.charCodeAt(i)) | 0; return Math.abs(h); }
function clamp(v, a, b) { return v < a ? a : v > b ? b : v; }
function withAlpha(hex, a) {
    // Accept #rrggbb only — every server-issued colour is in that form.
    if (!hex || hex[0] !== '#' || hex.length !== 7) return hex;
    const r = parseInt(hex.slice(1, 3), 16);
    const g = parseInt(hex.slice(3, 5), 16);
    const b = parseInt(hex.slice(5, 7), 16);
    return `rgba(${r},${g},${b},${a})`;
}
function noop() {}
