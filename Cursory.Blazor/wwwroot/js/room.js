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

    canvas.addEventListener('mousedown', onMouseDown);
    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);

    // HUD wiring — Reset button + level dropdown + vote overlay.
    const resetBtn = document.getElementById('room-reset-btn');
    const levelSelect = document.getElementById('room-level-select');
    const voteYesBtn = document.getElementById('room-vote-yes-btn');
    const voteNoBtn  = document.getElementById('room-vote-no-btn');
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
        window.removeEventListener('mousemove', onMouseMove);
        window.removeEventListener('mouseup', onMouseUp);
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
        syncLevelDropdown(snap.currentLevel);
        // Drive a local whistle anim for any whistles we didn't fire ourselves (others' clicks).
        if (snap.whistles && snap.whistles.length) {
            for (const w of snap.whistles) {
                if (w.userId === state.me.userId) continue;
                const recent = state.whistleAnims.find(a =>
                    a.x === w.x && a.y === w.y && a.color === w.color);
                if (recent) continue;
                state.whistleAnims.push({ x: w.x, y: w.y, color: w.color, t0: performance.now() });
                playWhistle(w.color);
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
        const [ax, ay] = blockAnchorWorld(b, c);
        // Truncate the line at the max-force reach. The full segment would run anchor → cursor;
        // we draw at most MAX_PULL_PX of it so an over-stretched pull reads as "maxed out".
        const dx = c.x - ax, dy = c.y - ay;
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
        // Rotation: if attached, point inward at anchor; otherwise point up.
        let rot = 0;
        if (c.attachedBlockId) {
            const b = blockById.get(c.attachedBlockId);
            if (b) {
                const [ax, ay] = blockAnchorWorld(b, c);
                rot = Math.atan2(ay - c.y, ax - c.x);
            }
        } else if (c.attachedShapeId) {
            const s = shapeById.get(c.attachedShapeId);
            if (s) {
                const cos = Math.cos(s.angle), sin = Math.sin(s.angle);
                const ax = s.x + c.anchorLocalX * cos - c.anchorLocalY * sin;
                const ay = s.y + c.anchorLocalX * sin + c.anchorLocalY * cos;
                rot = Math.atan2(ay - c.y, ax - c.x);
            }
        }
        drawArrow(c.x, c.y, rot, c.color, isMe);
        // Name tag
        ctx.fillStyle = withAlpha('#000000', 0.6);
        ctx.fillRect(c.x + 18, c.y + 6, ctx.measureText(c.displayName).width + 10, 18);
        ctx.fillStyle = c.color;
        ctx.font = '12px system-ui';
        ctx.textAlign = 'left';
        ctx.textBaseline = 'top';
        ctx.fillText(c.displayName, c.x + 23, c.y + 9);
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
    ctx.fillStyle = 'rgba(0,0,0,0.5)';
    ctx.strokeStyle = 'rgba(255,255,255,0.2)';
    ctx.lineWidth = 1;
    ctx.fillRect(mx, my, mw, mh);
    ctx.strokeRect(mx, my, mw, mh);
    // Viewport rect
    const z = state.cam.z;
    const vw = (canvas.width / z) / W * mw;
    const vh = (canvas.height / z) / H * mh;
    const vx = mx + (state.cam.x - canvas.width / (2 * z)) / W * mw;
    const vy = my + (state.cam.y - canvas.height / (2 * z)) / H * mh;
    ctx.strokeStyle = 'rgba(255,255,255,0.5)';
    ctx.strokeRect(vx, vy, vw, vh);
    // Cursors as dots
    for (const c of state.snapshot.cursors || []) {
        ctx.fillStyle = c.color;
        ctx.fillRect(mx + (c.x / W) * mw - 1, my + (c.y / H) * mh - 1, 3, 3);
    }
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
        const cos = Math.cos(s.angle), sin = Math.sin(s.angle);
        const ax = s.x + c.anchorLocalX * cos - c.anchorLocalY * sin;
        const ay = s.y + c.anchorLocalX * sin + c.anchorLocalY * cos;
        const dist = Math.hypot(c.x - ax, c.y - ay);
        // Thicker + brighter as distance grows. Scaled so a ~150-unit pull is fully saturated.
        const t = Math.min(1, dist / 200);
        ctx.setLineDash([14, 10]);
        ctx.lineDashOffset = -(performance.now() / 30) % 24;
        ctx.strokeStyle = withAlpha(c.color, 0.35 + 0.55 * t);
        ctx.lineWidth = 2 + 4 * t;
        ctx.beginPath();
        ctx.moveTo(ax, ay);
        ctx.lineTo(c.x, c.y);
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
    const overlay = document.getElementById('room-vote-overlay');
    if (!overlay) return;
    if (!vote) {
        overlay.setAttribute('hidden', '');
        return;
    }
    overlay.removeAttribute('hidden');
    const titleEl = document.getElementById('room-vote-title');
    if (titleEl) {
        if (vote.kind === 1 /* SelectLevel */)
            titleEl.textContent = `Switch to Level ${vote.targetLevel}?`;
        else
            titleEl.textContent = 'Reset the level?';
    }
    const yesEl  = document.getElementById('room-vote-yes');
    const noEl   = document.getElementById('room-vote-no');
    const needEl = document.getElementById('room-vote-need');
    if (yesEl)  yesEl.textContent  = vote.yesUserIds.length;
    if (noEl)   noEl.textContent   = vote.noUserIds.length;
    if (needEl) needEl.textContent = vote.quorum;
    // Disable the buttons if I've already voted on this round.
    const alreadyVoted =
        vote.yesUserIds.includes(state.me.userId) ||
        vote.noUserIds.includes(state.me.userId);
    const yesBtn = document.getElementById('room-vote-yes-btn');
    const noBtn  = document.getElementById('room-vote-no-btn');
    if (yesBtn) yesBtn.disabled = alreadyVoted;
    if (noBtn)  noBtn.disabled  = alreadyVoted;
}

let _lastSeenLevel = 0;
function syncLevelDropdown(level) {
    if (!level) return;
    const sel = document.getElementById('room-level-select');
    if (sel) {
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
    const banner = document.getElementById('room-level-banner');
    const titleEl = document.getElementById('room-level-banner-title');
    const subEl   = document.getElementById('room-level-banner-sub');
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
    const el = document.getElementById('room-status');
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
