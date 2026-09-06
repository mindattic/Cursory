// Canvas2D world renderer — draws the solid game world (grid, walls, blocks, shapes, goals,
// switches, doors, circuit) directly with 2D canvas calls. Shares its single <canvas>/context
// with shared/room-core.js's overlay draws (there is no separate #room-overlay for this engine),
// so this is the exact drawing room.js always did, just split out as the "world" half of it.

export function createWorldRenderer({ canvas }) {
    const ctx = canvas.getContext('2d');

    function renderWorld(state, now) {
        const w = canvas.width, h = canvas.height;
        ctx.fillStyle = '#0c0c0c';
        ctx.fillRect(0, 0, w, h);

        const z = state.cam.z;
        ctx.save();
        ctx.translate(w / 2, h / 2);
        ctx.scale(z, z);
        ctx.translate(-state.cam.x, -state.cam.y);

        drawGrid(ctx, state);
        drawGoals(ctx, state);
        drawShapeGoals(ctx, state);
        drawSwitches(ctx, state);
        drawWalls(ctx, state);
        drawDoors(ctx, state);
        drawBlocks(ctx, state);
        drawShapes(ctx, state);
        drawCircuit(ctx, state);

        ctx.restore();
    }

    return { renderWorld, resize() {}, dispose() {} };
}

function drawGrid(ctx, state) {
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

function drawGoals(ctx, state) {
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

function drawShapeGoals(ctx, state) {
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

function drawSwitches(ctx, state) {
    for (const s of state.snapshot.switches || []) {
        const cx = s.x, cy = s.y;
        const r = Math.min(s.w, s.h) / 2 - 8;
        ctx.strokeStyle = withAlpha(s.color, s.isActive ? 1 : 0.5);
        ctx.lineWidth = 4;
        ctx.beginPath();
        ctx.arc(cx, cy, r, 0, Math.PI * 2);
        ctx.stroke();
        if (s.isActive) {
            ctx.fillStyle = withAlpha(s.color, 0.35);
            ctx.beginPath();
            ctx.arc(cx, cy, r - 4, 0, Math.PI * 2);
            ctx.fill();
        }
        ctx.fillStyle = 'rgba(255,255,255,0.85)';
        ctx.font = '20px system-ui';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(`${s.cursorsInside}/${s.requiredCount}`, cx, cy);
    }
}

function drawWalls(ctx, state) {
    for (const w of state.geometry.walls) {
        ctx.fillStyle = '#2a2a2a';
        ctx.strokeStyle = 'rgba(255,255,255,0.18)';
        ctx.lineWidth = 2;
        ctx.fillRect(w.x - w.w / 2, w.y - w.h / 2, w.w, w.h);
        ctx.strokeRect(w.x - w.w / 2, w.y - w.h / 2, w.w, w.h);
    }
}

function drawDoors(ctx, state) {
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

function drawBlocks(ctx, state) {
    for (const b of state.snapshot.blocks || []) {
        ctx.fillStyle = b.color || '#3a3a3a';
        ctx.strokeStyle = 'rgba(255,255,255,0.2)';
        ctx.lineWidth = 2;
        // Blocks are real rigid bodies — draw them in their own rotated frame.
        ctx.save();
        ctx.translate(b.x, b.y);
        ctx.rotate(b.angle || 0);
        ctx.fillRect(-b.w / 2, -b.h / 2, b.w, b.h);
        ctx.strokeRect(-b.w / 2, -b.h / 2, b.w, b.h);
        ctx.restore();
        // Mass number is drawn by the shared overlay (room-core.js drawMassLabels) so every
        // engine gets it for free instead of hand-rolling 3D text.
    }
}

function drawShapes(ctx, state) {
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
        // Mass number is drawn by the shared overlay (room-core.js drawMassLabels) so every
        // engine gets it for free instead of hand-rolling 3D text.
    }
}

// Circuit levels: components (battery/resistor/bulb), terminals, and the wires the players route.
function drawCircuit(ctx, state) {
    const snap = state.snapshot;
    if ((!snap.components || !snap.components.length) && (!snap.wires || !snap.wires.length)) return;

    for (const comp of snap.components || []) drawComponent(ctx, comp);

    // Terminals as posts; brighter when a held wire end is hovering close enough to snap.
    for (const t of snap.terminals || []) {
        let near = false;
        if (state.attachedWireId) {
            const d = Math.hypot(state.mouseWorld.x - t.x, state.mouseWorld.y - t.y);
            near = d < 90;
        }
        ctx.fillStyle = t.polarity === 'pos' ? '#e0564f' : t.polarity === 'neg' ? '#5b8def' : 'rgba(220,220,220,0.9)';
        ctx.beginPath();
        ctx.arc(t.x, t.y, near ? 16 : 11, 0, Math.PI * 2);
        ctx.fill();
        if (near) { ctx.strokeStyle = 'rgba(255,255,255,0.9)'; ctx.lineWidth = 3; ctx.stroke(); }
        if (t.polarity) {
            ctx.fillStyle = 'rgba(255,255,255,0.95)';
            ctx.font = '600 28px system-ui';
            ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
            ctx.fillText(t.polarity === 'pos' ? '+' : '−', t.x, t.y - 34);
        }
    }

    // Wires. A held end follows my live cursor (prediction); the rest come from the snapshot.
    for (const w of snap.wires || []) {
        let ax = w.ax, ay = w.ay, bx = w.bx, by = w.by;
        if (state.attachedWireId === w.id) {
            if (state.attachedWireEnd === 0) { ax = state.mouseWorld.x; ay = state.mouseWorld.y; }
            else { bx = state.mouseWorld.x; by = state.mouseWorld.y; }
        }
        ctx.strokeStyle = w.color || '#caa472';
        ctx.lineWidth = 7;
        ctx.lineCap = 'round';
        ctx.beginPath();
        ctx.moveTo(ax, ay); ctx.lineTo(bx, by);
        ctx.stroke();
        for (const [ex, ey, plugged] of [[ax, ay, w.aTerminalId], [bx, by, w.bTerminalId]]) {
            ctx.fillStyle = plugged ? w.color || '#caa472' : '#2a2a2a';
            ctx.strokeStyle = w.color || '#caa472';
            ctx.lineWidth = 3;
            ctx.beginPath();
            ctx.arc(ex, ey, 10, 0, Math.PI * 2);
            ctx.fill(); ctx.stroke();
        }
    }
    ctx.lineCap = 'butt';
}

function drawComponent(ctx, comp) {
    const x = comp.x, y = comp.y, hw = comp.w / 2, hh = comp.h / 2;
    ctx.textAlign = 'center';
    if (comp.kind === 'bulb') {
        const r = Math.min(hw, hh);
        // Glow when lit.
        if (comp.lit) {
            const g = ctx.createRadialGradient(x, y, r * 0.3, x, y, r * 2.4);
            g.addColorStop(0, 'rgba(255,231,120,0.55)');
            g.addColorStop(1, 'rgba(255,231,120,0)');
            ctx.fillStyle = g;
            ctx.beginPath(); ctx.arc(x, y, r * 2.4, 0, Math.PI * 2); ctx.fill();
        }
        ctx.fillStyle = comp.lit ? '#ffe06a' : '#3a3a40';
        ctx.strokeStyle = comp.lit ? '#fff3b0' : 'rgba(255,255,255,0.3)';
        ctx.lineWidth = 4;
        ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
        // Filament cross.
        ctx.strokeStyle = comp.lit ? 'rgba(120,80,0,0.7)' : 'rgba(255,255,255,0.25)';
        ctx.lineWidth = 3;
        ctx.beginPath();
        ctx.moveTo(x - r * 0.4, y); ctx.lineTo(x, y - r * 0.4); ctx.lineTo(x + r * 0.4, y);
        ctx.stroke();
    } else if (comp.kind === 'resistor') {
        ctx.fillStyle = '#5a4a2a';
        ctx.strokeStyle = '#caa472';
        ctx.lineWidth = 3;
        ctx.fillRect(x - hw, y - hh, comp.w, comp.h);
        ctx.strokeRect(x - hw, y - hh, comp.w, comp.h);
        // Zig-zag.
        ctx.strokeStyle = '#e0c89a';
        ctx.lineWidth = 4;
        ctx.beginPath();
        const n = 6, step = comp.w / n;
        ctx.moveTo(x - hw, y);
        for (let i = 0; i < n; i++) ctx.lineTo(x - hw + step * (i + 0.5), y + (i % 2 ? hh * 0.6 : -hh * 0.6));
        ctx.lineTo(x + hw, y);
        ctx.stroke();
    } else { // battery
        ctx.fillStyle = '#2f3a2f';
        ctx.strokeStyle = '#7FBF5A';
        ctx.lineWidth = 4;
        ctx.fillRect(x - hw, y - hh, comp.w, comp.h);
        ctx.strokeRect(x - hw, y - hh, comp.w, comp.h);
    }
    // Label below the component.
    ctx.fillStyle = 'rgba(255,255,255,0.7)';
    ctx.font = '22px system-ui';
    ctx.textBaseline = 'top';
    ctx.fillText(comp.label || '', x, y + hh + 10);
}

function withAlpha(hex, a) {
    if (!hex || hex[0] !== '#' || hex.length !== 7) return hex;
    const r = parseInt(hex.slice(1, 3), 16);
    const g = parseInt(hex.slice(3, 5), 16);
    const b = parseInt(hex.slice(5, 7), 16);
    return `rgba(${r},${g},${b},${a})`;
}
