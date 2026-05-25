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
    snapshot: { tick: 0, cursors: [], blocks: [], goals: [], whistles: [] },
    attachedBlockId: null,
    lastSent: 0,
    whistleAnims: [], // {x, y, color, t0}
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

    const onMouseDown = (e) => {
        ensureAudio();
        const world = clientToWorld(e.clientX, e.clientY);
        state.mouseWorld = world;
        const hit = pickBlock(world.x, world.y);
        if (hit && connection) {
            state.attachedBlockId = hit.id;
            connection.invoke('Grab', hit.id, world.x, world.y).catch(noop);
        } else {
            // Click in empty space = whistle.
            playWhistle(state.me.color);
            state.whistleAnims.push({ x: world.x, y: world.y, color: state.me.color, t0: performance.now() });
            if (connection) connection.invoke('Whistle', world.x, world.y).catch(noop);
            // Begin pan
            state.pan.active = true;
            state.pan.sx = e.clientX; state.pan.sy = e.clientY;
            state.pan.ox = state.cam.x; state.pan.oy = state.cam.y;
            viewport.classList.add('dragging');
        }
    };
    const onMouseMove = (e) => {
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
        }
        if (state.attachedBlockId && connection) {
            state.attachedBlockId = null;
            connection.invoke('Release').catch(noop);
        }
    };

    canvas.addEventListener('mousedown', onMouseDown);
    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);
    detachListeners = () => {
        canvas.removeEventListener('mousedown', onMouseDown);
        window.removeEventListener('mousemove', onMouseMove);
        window.removeEventListener('mouseup', onMouseUp);
        window.removeEventListener('resize', resize);
    };

    // SignalR connection.
    if (!window.signalR) { console.error('[cursory] signalR client not loaded'); return; }
    connection = new window.signalR.HubConnectionBuilder()
        .withUrl('/hubs/room')
        .withAutomaticReconnect()
        .build();
    connection.on('Snapshot', (snap) => {
        state.snapshot = snap;
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
    try { await connection.start(); }
    catch (e) { console.error('[cursory] hub connect failed', e); }

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
    drawGoals();
    drawBlocks();
    drawAttachLines();
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
        ctx.fillRect(b.x - b.w / 2, b.y - b.h / 2, b.w, b.h);
        ctx.strokeRect(b.x - b.w / 2, b.y - b.h / 2, b.w, b.h);
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
        const ax = b.x + c.anchorLocalX, ay = b.y + c.anchorLocalY;
        ctx.strokeStyle = withAlpha(c.color, 0.7);
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.moveTo(ax, ay);
        ctx.lineTo(c.x, c.y);
        ctx.stroke();
        ctx.fillStyle = c.color;
        ctx.beginPath();
        ctx.arc(ax, ay, 4, 0, Math.PI * 2);
        ctx.fill();
    }
}

function drawCursors() {
    for (const c of state.snapshot.cursors || []) {
        const isMe = c.userId === state.me.userId;
        // Rotation: if attached, point inward at anchor; otherwise point up.
        let rot = 0;
        if (c.attachedBlockId) {
            const blockById = (state.snapshot.blocks || []).find(b => b.id === c.attachedBlockId);
            if (blockById) {
                const ax = blockById.x + c.anchorLocalX, ay = blockById.y + c.anchorLocalY;
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

function pickBlock(wx, wy) {
    for (const b of state.snapshot.blocks || []) {
        if (wx > b.x - b.w / 2 && wx < b.x + b.w / 2 &&
            wy > b.y - b.h / 2 && wy < b.y + b.h / 2) return b;
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
