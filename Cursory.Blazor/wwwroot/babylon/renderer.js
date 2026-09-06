// Babylon.js world renderer — draws the solid game world (grid, walls, blocks, shapes, goals,
// switches, doors, circuit) as flat, unlit ground-plane meshes with a top-down orthographic
// camera. Cursors, tethers, whistles, labels, and mass numbers are NOT drawn here — see
// shared/room-core.js's drawOverlay, which draws those on a separate transparent #room-overlay 2D
// canvas stacked on top of this one so they always line up with whatever this camera is framing.
//
// Coordinate convention: world (x, y) [Y grows downward, matching the wire format and the
// canvas2d renderer] maps to Babylon (x, 0, y) — world Y becomes Babylon's depth axis (Z), height
// (Babylon Y) is always 0 since every game object is flat. The camera sits above (+Y) looking
// straight down with upVector (0,0,-1) so increasing world Y (Z) reads as "down the screen" and
// increasing world X reads as "right", matching canvas2d. Body spin uses `rotation.y` (Babylon's
// vertical axis, i.e. the "spin as seen from above" axis) at ROTATION_SIGN * angle.
//
// NOTE: Babylon's left-handed convention for this upVector/rotation combination was derived, not
// executed against a running WebGL context — when manually verifying this renderer in a browser
// (per the plan's verification step), check that dragging right/down moves the block right/down
// on screen and that a two-cursor offset pull spins a shape the same visual direction as the
// canvas2d renderer. If X or spin reads mirrored/reversed, flip ROTATION_SIGN below or negate the
// X terms in toScene()/camera setup accordingly.
const ROTATION_SIGN = 1;

// NOTE: window.BABYLON is read lazily inside createWorldRenderer(), not at module top level. This
// module is imported (and its top-level code run) the instant room.js's dynamic import resolves —
// well before room.js's start() has awaited the CDN <script> tag actually defining window.BABYLON.
// Capturing `const BABYLON = window.BABYLON` here would permanently freeze it at undefined.

export function createWorldRenderer({ canvas, state }) {
    const BABYLON = window.BABYLON;

    function toScene(x, y) { return new BABYLON.Vector3(x, 0, y); }
    function hexToColor3(hex) { return BABYLON.Color3.FromHexString(hex || '#888888'); }

    function makeFlatRect(scene, name, w, h, colorHex, opacity = 1) {
        const mesh = BABYLON.MeshBuilder.CreateGround(name, { width: Math.max(0.01, w), height: Math.max(0.01, h) }, scene);
        const mat = new BABYLON.StandardMaterial(name + '-mat', scene);
        mat.disableLighting = true;
        // The mirrored orthoLeft/orthoRight above (needed to match the other engines' X axis)
        // flips which winding direction faces the camera, so the default backface culling would
        // hide every flat mesh. Render both sides — these are unlit flat shapes, so there's no
        // lighting-direction cost to paying for it.
        mat.backFaceCulling = false;
        mat.emissiveColor = hexToColor3(colorHex);
        mat.alpha = opacity;
        mesh.material = mat;
        return mesh;
    }

    function makeFlatDisc(scene, name, radius, colorHex, opacity = 1) {
        const mesh = BABYLON.MeshBuilder.CreateDisc(name, { radius: Math.max(0.01, radius), tessellation: 24 }, scene);
        mesh.rotation.x = Math.PI / 2; // CreateDisc faces +Z by default; lay it flat like the grounds.
        const mat = new BABYLON.StandardMaterial(name + '-mat', scene);
        mat.disableLighting = true;
        // The mirrored orthoLeft/orthoRight above (needed to match the other engines' X axis)
        // flips which winding direction faces the camera, so the default backface culling would
        // hide every flat mesh. Render both sides — these are unlit flat shapes, so there's no
        // lighting-direction cost to paying for it.
        mat.backFaceCulling = false;
        mat.emissiveColor = hexToColor3(colorHex);
        mat.alpha = opacity;
        mesh.material = mat;
        return mesh;
    }

    // Create-on-first-sight / update-every-tick / dispose-when-missing, keyed by server id.
    function syncById(map, items, factory, updater) {
        const seen = new Set();
        for (const item of items || []) {
            seen.add(item.id);
            let obj = map.get(item.id);
            if (!obj) { obj = factory(item); map.set(item.id, obj); }
            updater(obj, item);
        }
        for (const [id, obj] of map) {
            if (!seen.has(id)) { obj.dispose(); map.delete(id); }
        }
    }

    const engine = new BABYLON.Engine(canvas, true, { preserveDrawingBuffer: true, stencil: true });
    const scene = new BABYLON.Scene(engine);
    scene.clearColor = new BABYLON.Color4(0x0c / 255, 0x0c / 255, 0x0c / 255, 1);

    const camera = new BABYLON.FreeCamera('cam', new BABYLON.Vector3(0, 100, 0), scene);
    camera.mode = BABYLON.Camera.ORTHOGRAPHIC_CAMERA;
    camera.minZ = 0.1;
    camera.maxZ = 2000;
    camera.upVector = new BABYLON.Vector3(0, 0, -1);

    let gridMesh = null;
    let gridKey = '';
    let wallRoot = null;
    let wallsRef = null;

    const blockMeshes = new Map();
    const shapeGroups = new Map();
    const goalMeshes = new Map();
    const shapeGoalMeshes = new Map();
    const switchMeshes = new Map();
    const doorMeshes = new Map();
    const componentMeshes = new Map();
    const terminalMeshes = new Map();
    const wireLines = new Map();

    function ensureGrid(worldW, worldH) {
        const key = worldW + 'x' + worldH;
        if (gridKey === key) return;
        gridKey = key;
        if (gridMesh) gridMesh.dispose();
        const step = 200;
        const lines = [];
        for (let x = 0; x <= worldW; x += step) lines.push([toScene(x, 0), toScene(x, worldH)]);
        for (let y = 0; y <= worldH; y += step) lines.push([toScene(0, y), toScene(worldW, y)]);
        lines.push([toScene(0, 0), toScene(worldW, 0), toScene(worldW, worldH), toScene(0, worldH), toScene(0, 0)]);
        gridMesh = BABYLON.MeshBuilder.CreateLineSystem('grid', { lines }, scene);
        gridMesh.color = new BABYLON.Color3(1, 1, 1);
        gridMesh.alpha = 0.04; // matches the faint grid opacity in canvas2d/three
    }

    function ensureWalls(walls) {
        if (wallsRef === walls) return;
        wallsRef = walls;
        if (wallRoot) wallRoot.dispose();
        wallRoot = new BABYLON.TransformNode('walls', scene);
        (walls || []).forEach((w, i) => {
            const mesh = makeFlatRect(scene, 'wall' + i, w.w, w.h, '#2a2a2a');
            mesh.position = toScene(w.x, w.y);
            mesh.parent = wallRoot;
        });
    }

    function updateBlocks(blocks) {
        syncById(blockMeshes, blocks,
            (b) => makeFlatRect(scene, 'block-' + b.id, b.w, b.h, b.color || '#3a3a3a'),
            (mesh, b) => {
                mesh.position = toScene(b.x, b.y);
                mesh.rotation.y = ROTATION_SIGN * (b.angle || 0);
                mesh.material.emissiveColor = hexToColor3(b.color || '#3a3a3a');
            });
    }

    function updateShapes(shapes) {
        syncById(shapeGroups, shapes,
            (s) => {
                const root = new BABYLON.TransformNode('shape-' + s.id, scene);
                const color = s.color || '#D85A30';
                (s.pieces || []).forEach((p, i) => {
                    const mesh = makeFlatRect(scene, 'shape-' + s.id + '-p' + i, p.halfW * 2, p.halfH * 2, color);
                    mesh.position = new BABYLON.Vector3(p.localX, 0, -p.localY);
                    mesh.parent = root;
                });
                const dot = makeFlatDisc(scene, 'shape-' + s.id + '-dot', 4, '#ffffff', 0.4);
                dot.parent = root;
                return root;
            },
            (root, s) => {
                root.position = toScene(s.x, s.y);
                root.rotation.y = ROTATION_SIGN * (s.angle || 0);
            });
    }

    function updateGoals(goals) {
        syncById(goalMeshes, goals,
            (g) => makeFlatRect(scene, 'goal-' + g.id, g.w, g.h, '#7F77DD', 0.15),
            (mesh, g) => {
                mesh.position = toScene(g.x, g.y);
                mesh.material.emissiveColor = hexToColor3(g.isSolved ? '#1D9E75' : '#7F77DD');
                mesh.material.alpha = g.isSolved ? 0.35 : 0.15;
            });
    }

    function updateShapeGoals(shapeGoals) {
        syncById(shapeGoalMeshes, shapeGoals,
            (g) => makeFlatRect(scene, 'shapegoal-' + g.id, g.w, g.h, '#D85A30', 0.1),
            (mesh, g) => {
                mesh.position = toScene(g.x, g.y);
                mesh.material.emissiveColor = hexToColor3(g.isSolved ? '#1D9E75' : '#D85A30');
                mesh.material.alpha = g.isSolved ? 0.35 : 0.1;
            });
    }

    // Switches/doors always ride empty arrays in the current build (server-side placeholders —
    // see CUR-A1); these stay simple/defensive rather than pixel-matching the retired sum-of-
    // springs art, since there is no live data to verify against today.
    function updateSwitches(switches) {
        syncById(switchMeshes, switches,
            (sw) => {
                const r = Math.min(sw.w, sw.h) / 2 - 8;
                const root = new BABYLON.TransformNode('switch-' + sw.id, scene);
                const ring = makeFlatDisc(scene, 'switch-' + sw.id + '-ring', r, sw.color, 0.5);
                ring.parent = root;
                const fill = makeFlatDisc(scene, 'switch-' + sw.id + '-fill', Math.max(0.01, r - 4), sw.color, 0.35);
                fill.parent = root;
                fill.setEnabled(false);
                root.metadata = { ring, fill };
                return root;
            },
            (root, sw) => {
                root.position = toScene(sw.x, sw.y);
                root.metadata.ring.material.alpha = sw.isActive ? 1 : 0.5;
                root.metadata.fill.setEnabled(!!sw.isActive);
            });
    }

    function updateDoors(doors) {
        syncById(doorMeshes, doors,
            (d) => makeFlatRect(scene, 'door-' + d.id, d.w, d.h, '#5A2E22'),
            (mesh, d) => {
                mesh.position = toScene(d.x, d.y);
                mesh.material.emissiveColor = hexToColor3(d.isOpen ? '#1D9E75' : '#5A2E22');
                mesh.material.alpha = d.isOpen ? 0.15 : 1;
            });
    }

    // Circuit: simplified relative to canvas2d (no bulb glow gradient, no resistor zigzag texture)
    // — the functionally important lit/unlit colour state and wire routing are preserved.
    function updateCircuit(state) {
        const snap = state.snapshot;
        syncById(componentMeshes, snap.components,
            (c) => c.kind === 'bulb'
                ? makeFlatDisc(scene, 'comp-' + c.id, Math.min(c.w, c.h) / 2, '#3A3A40')
                : makeFlatRect(scene, 'comp-' + c.id, c.w, c.h, c.kind === 'resistor' ? '#5A4A2A' : '#2F3A2F'),
            (mesh, c) => {
                mesh.position = toScene(c.x, c.y);
                if (c.kind === 'bulb') mesh.material.emissiveColor = hexToColor3(c.lit ? '#FFE06A' : '#3A3A40');
            });

        syncById(terminalMeshes, snap.terminals,
            (t) => makeFlatDisc(scene, 'term-' + t.id, 11, '#DCDCDC'),
            (mesh, t) => {
                let near = false;
                if (state.attachedWireId) {
                    const d = Math.hypot(state.mouseWorld.x - t.x, state.mouseWorld.y - t.y);
                    near = d < 90;
                }
                mesh.position = toScene(t.x, t.y);
                mesh.scaling.set(near ? 16 / 11 : 1, 1, near ? 16 / 11 : 1);
                mesh.material.emissiveColor = hexToColor3(t.polarity === 'pos' ? '#E0564F' : t.polarity === 'neg' ? '#5B8DEF' : '#DCDCDC');
            });

        syncById(wireLines, snap.wires,
            (w) => {
                const points = [toScene(w.ax, w.ay), toScene(w.bx, w.by)];
                const line = BABYLON.MeshBuilder.CreateLines('wire-' + w.id, { points, updatable: true }, scene);
                line.color = hexToColor3(w.color || '#caa472');
                return line;
            },
            (line, w) => {
                let ax = w.ax, ay = w.ay, bx = w.bx, by = w.by;
                if (state.attachedWireId === w.id) {
                    if (state.attachedWireEnd === 0) { ax = state.mouseWorld.x; ay = state.mouseWorld.y; }
                    else { bx = state.mouseWorld.x; by = state.mouseWorld.y; }
                }
                BABYLON.MeshBuilder.CreateLines('wire-' + w.id, { points: [toScene(ax, ay), toScene(bx, by)], instance: line });
            });
    }

    function renderWorld(state) {
        ensureGrid(state.world.width, state.world.height);
        ensureWalls(state.geometry.walls);
        updateBlocks(state.snapshot.blocks);
        updateShapes(state.snapshot.shapes);
        updateGoals(state.snapshot.goals);
        updateShapeGoals(state.snapshot.shapeGoals);
        updateSwitches(state.snapshot.switches);
        updateDoors(state.snapshot.doors);
        updateCircuit(state);

        const z = state.cam.z;
        const halfW = (canvas.width / 2) / z, halfH = (canvas.height / 2) / z;
        // Swapped (not -halfW/halfW): with upVector (0,0,-1) this camera's apparent X axis comes
        // out mirrored relative to Three's/canvas2d's (measured empirically — increasing world X
        // rendered further LEFT on screen as the camera panned). Swapping left/right mirrors it
        // back so increasing world X reads right, matching every other engine.
        camera.orthoLeft = halfW; camera.orthoRight = -halfW;
        camera.orthoTop = halfH; camera.orthoBottom = -halfH;
        camera.position.set(state.cam.x, 100, state.cam.y);
        camera.setTarget(new BABYLON.Vector3(state.cam.x, 0, state.cam.y));

        scene.render();
    }

    // engine.resize() (no args) re-derives the backbuffer size from canvas.clientWidth/Height
    // itself, ignoring devicePixelRatio — which would silently overwrite the DPR-aware
    // canvas.width/height room-core.js already set, desyncing this camera's frustum from the
    // overlay canvas's size. setSize() takes the exact pixel dimensions room-core computed instead.
    function resize(w, h) {
        engine.setSize(w, h);
    }

    function dispose() {
        scene.dispose();
        engine.dispose();
    }

    return { renderWorld, resize, dispose };
}
