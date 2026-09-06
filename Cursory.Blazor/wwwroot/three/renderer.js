// Three.js world renderer — draws the solid game world (grid, walls, blocks, shapes, goals,
// switches, doors, circuit) as flat, unlit meshes with a top-down orthographic camera. Cursors,
// tethers, whistles, labels, and mass numbers are NOT drawn here — see shared/room-core.js's
// drawOverlay, which draws those on a separate transparent #room-overlay 2D canvas stacked on top
// of this one so they always line up with whatever this camera is currently framing.
//
// Coordinate convention: this file maps world (x, y) [Y grows downward, matching the wire format
// and the canvas2d renderer] to scene (x, -y, 0) [Y-up, as Three.js expects], and negates angles
// (sceneRotationZ = -worldAngle) so a body's rotation reads the same visual direction — CW for a
// positive angle — as it always has in the canvas2d renderer. See toScene()/toSceneAngle() below;
// every mesh position/rotation in this file goes through them so the convention only needs to be
// right once.

// NOTE: window.THREE is read lazily inside createWorldRenderer(), not at module top level. This
// module is imported (and its top-level code run) the instant room.js's dynamic import resolves —
// well before room.js's start() has awaited the CDN <script> tag actually defining window.THREE.
// Capturing `const THREE = window.THREE` here would permanently freeze it at undefined.

function toScene(x, y) { return [x, -y, 0]; }
function toSceneAngle(a) { return -(a || 0); }
function hexToInt(hex) { return hex ? parseInt(hex.replace('#', ''), 16) : 0x888888; }

export function createWorldRenderer({ canvas, state }) {
    const THREE = window.THREE;

    // Shared unit geometry: every flat rect mesh is a 1x1 plane scaled to its actual size, so we
    // don't allocate a new BufferGeometry per block/shape piece/goal/etc.
    const unitPlane = new THREE.PlaneGeometry(1, 1);

    function makeFlatRect(colorHex, opacity = 1) {
        const mat = new THREE.MeshBasicMaterial({ color: colorHex, transparent: opacity < 1, opacity, side: THREE.DoubleSide });
        const mesh = new THREE.Mesh(unitPlane, mat);
        const edges = new THREE.LineSegments(
            new THREE.EdgesGeometry(unitPlane),
            new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.25 }));
        mesh.add(edges);
        return mesh;
    }

    function disposeObject(obj) {
        obj.traverse((o) => {
            if (o.geometry && o.geometry !== unitPlane) o.geometry.dispose();
            if (o.material) (Array.isArray(o.material) ? o.material : [o.material]).forEach((m) => m.dispose());
        });
    }

    // Create-on-first-sight / update-every-tick / dispose-when-missing, keyed by server id — the
    // standard technique for mapping a per-tick snapshot array onto persistent engine objects.
    function syncById(scene, map, items, factory, updater) {
        const seen = new Set();
        for (const item of items || []) {
            seen.add(item.id);
            let obj = map.get(item.id);
            if (!obj) { obj = factory(item); map.set(item.id, obj); scene.add(obj); }
            updater(obj, item);
        }
        for (const [id, obj] of map) {
            if (!seen.has(id)) { scene.remove(obj); disposeObject(obj); map.delete(id); }
        }
    }

    const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
    renderer.setClearColor(0x0c0c0c, 1);
    renderer.setPixelRatio(1); // canvas.width/height are already device-pixel sized by room-core's resize()

    const scene = new THREE.Scene();
    const camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 2000);
    camera.up.set(0, 1, 0);

    let gridGroup = null;
    let gridKey = '';
    let wallGroup = null;
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
        if (gridGroup) { scene.remove(gridGroup); disposeObject(gridGroup); }
        gridGroup = new THREE.Group();
        const step = 200;
        const pts = [];
        for (let x = 0; x <= worldW; x += step) { pts.push(x, 0, 0, x, -worldH, 0); }
        for (let y = 0; y <= worldH; y += step) { pts.push(0, -y, 0, worldW, -y, 0); }
        const geom = new THREE.BufferGeometry();
        geom.setAttribute('position', new THREE.Float32BufferAttribute(pts, 3));
        gridGroup.add(new THREE.LineSegments(geom, new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.04 })));
        // World boundary.
        const b = [0, 0, 0, worldW, 0, 0, worldW, -worldH, 0, 0, -worldH, 0, 0, 0, 0];
        const bgeom = new THREE.BufferGeometry();
        bgeom.setAttribute('position', new THREE.Float32BufferAttribute(b, 3));
        gridGroup.add(new THREE.Line(bgeom, new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.2 })));
        scene.add(gridGroup);
    }

    function ensureWalls(walls) {
        if (wallsRef === walls) return;
        wallsRef = walls;
        if (wallGroup) { scene.remove(wallGroup); disposeObject(wallGroup); }
        wallGroup = new THREE.Group();
        for (const w of walls || []) {
            const mesh = makeFlatRect(0x2a2a2a);
            mesh.scale.set(w.w, w.h, 1);
            mesh.position.set(...toScene(w.x, w.y));
            wallGroup.add(mesh);
        }
        scene.add(wallGroup);
    }

    function updateBlocks(blocks) {
        syncById(scene, blockMeshes, blocks,
            (b) => makeFlatRect(hexToInt(b.color || '#3a3a3a')),
            (mesh, b) => {
                mesh.scale.set(b.w, b.h, 1);
                mesh.position.set(...toScene(b.x, b.y));
                mesh.rotation.z = toSceneAngle(b.angle);
                mesh.material.color.setHex(hexToInt(b.color || '#3a3a3a'));
            });
    }

    function updateShapes(shapes) {
        syncById(scene, shapeGroups, shapes,
            (s) => {
                const group = new THREE.Group();
                const color = hexToInt(s.color || '#D85A30');
                for (const p of s.pieces || []) {
                    const mesh = makeFlatRect(color);
                    mesh.scale.set(p.halfW * 2, p.halfH * 2, 1);
                    mesh.position.set(p.localX, -p.localY, 0);
                    group.add(mesh);
                }
                // Centre dot, matching the canvas2d renderer's rotation-visibility cue.
                const dot = new THREE.Mesh(new THREE.CircleGeometry(4, 12), new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.4 }));
                group.add(dot);
                return group;
            },
            (group, s) => {
                group.position.set(...toScene(s.x, s.y));
                group.rotation.z = toSceneAngle(s.angle);
            });
    }

    function updateGoals(goals) {
        syncById(scene, goalMeshes, goals,
            () => makeFlatRect(0x7f77dd, 0.15),
            (mesh, g) => {
                mesh.scale.set(g.w, g.h, 1);
                mesh.position.set(...toScene(g.x, g.y));
                mesh.material.color.setHex(g.isSolved ? 0x1d9e75 : 0x7f77dd);
                mesh.material.opacity = g.isSolved ? 0.35 : 0.15;
            });
    }

    function updateShapeGoals(shapeGoals) {
        syncById(scene, shapeGoalMeshes, shapeGoals,
            () => makeFlatRect(0xd85a30, 0.1),
            (mesh, g) => {
                mesh.scale.set(g.w, g.h, 1);
                mesh.position.set(...toScene(g.x, g.y));
                mesh.material.color.setHex(g.isSolved ? 0x1d9e75 : 0xd85a30);
                mesh.material.opacity = g.isSolved ? 0.35 : 0.1;
            });
    }

    // Switches/doors always ride empty arrays in the current build (server-side placeholders —
    // see CUR-A1); these stay simple/defensive rather than pixel-matching the retired sum-of-
    // springs art, since there is no live data to verify against today.
    function updateSwitches(switches) {
        syncById(scene, switchMeshes, switches,
            (sw) => {
                const r = Math.min(sw.w, sw.h) / 2 - 8;
                const ring = new THREE.Mesh(new THREE.RingGeometry(Math.max(0.1, r - 3), r, 24),
                    new THREE.MeshBasicMaterial({ color: hexToInt(sw.color), transparent: true, opacity: 0.5, side: THREE.DoubleSide }));
                const fill = new THREE.Mesh(new THREE.CircleGeometry(Math.max(0.1, r - 4), 24),
                    new THREE.MeshBasicMaterial({ color: hexToInt(sw.color), transparent: true, opacity: 0.35, side: THREE.DoubleSide }));
                fill.visible = false;
                const group = new THREE.Group();
                group.add(ring); group.add(fill);
                group.userData = { ring, fill };
                return group;
            },
            (group, sw) => {
                group.position.set(...toScene(sw.x, sw.y));
                group.userData.ring.material.opacity = sw.isActive ? 1 : 0.5;
                group.userData.fill.visible = !!sw.isActive;
            });
    }

    function updateDoors(doors) {
        syncById(scene, doorMeshes, doors,
            () => makeFlatRect(0x5a2e22),
            (mesh, d) => {
                mesh.scale.set(d.w, d.h, 1);
                mesh.position.set(...toScene(d.x, d.y));
                mesh.material.color.setHex(d.isOpen ? 0x1d9e75 : 0x5a2e22);
                mesh.material.opacity = d.isOpen ? 0.15 : 1;
                mesh.material.transparent = true;
            });
    }

    // Circuit: simplified relative to canvas2d (no bulb glow gradient, no resistor zigzag texture)
    // — the functionally important lit/unlit colour state and wire routing are preserved.
    function updateCircuit(state) {
        const snap = state.snapshot;
        syncById(scene, componentMeshes, snap.components,
            (c) => {
                const colorFor = (kind) => kind === 'bulb' ? 0x3a3a40 : kind === 'resistor' ? 0x5a4a2a : 0x2f3a2f;
                const mesh = c.kind === 'bulb'
                    ? new THREE.Mesh(new THREE.CircleGeometry(1, 24), new THREE.MeshBasicMaterial({ color: colorFor(c.kind) }))
                    : makeFlatRect(colorFor(c.kind));
                return mesh;
            },
            (mesh, c) => {
                mesh.position.set(...toScene(c.x, c.y));
                if (c.kind === 'bulb') {
                    mesh.scale.set(Math.min(c.w, c.h) / 2, Math.min(c.w, c.h) / 2, 1);
                    mesh.material.color.setHex(c.lit ? 0xffe06a : 0x3a3a40);
                } else {
                    mesh.scale.set(c.w, c.h, 1);
                }
            });

        syncById(scene, terminalMeshes, snap.terminals,
            (t) => new THREE.Mesh(new THREE.CircleGeometry(1, 16), new THREE.MeshBasicMaterial({ color: 0xdcdcdc })),
            (mesh, t) => {
                let near = false;
                if (state.attachedWireId) {
                    const d = Math.hypot(state.mouseWorld.x - t.x, state.mouseWorld.y - t.y);
                    near = d < 90;
                }
                mesh.position.set(...toScene(t.x, t.y));
                mesh.scale.set(near ? 16 : 11, near ? 16 : 11, 1);
                mesh.material.color.setHex(t.polarity === 'pos' ? 0xe0564f : t.polarity === 'neg' ? 0x5b8def : 0xdcdcdc);
            });

        syncById(scene, wireLines, snap.wires,
            (w) => {
                const geom = new THREE.BufferGeometry();
                geom.setAttribute('position', new THREE.Float32BufferAttribute(new Float32Array(6), 3));
                return new THREE.Line(geom, new THREE.LineBasicMaterial({ color: hexToInt(w.color || '#caa472') }));
            },
            (line, w) => {
                let ax = w.ax, ay = w.ay, bx = w.bx, by = w.by;
                if (state.attachedWireId === w.id) {
                    if (state.attachedWireEnd === 0) { ax = state.mouseWorld.x; ay = state.mouseWorld.y; }
                    else { bx = state.mouseWorld.x; by = state.mouseWorld.y; }
                }
                const [sax, say] = toScene(ax, ay), [sbx, sby] = toScene(bx, by);
                const pos = line.geometry.getAttribute('position');
                pos.setXYZ(0, sax, say, 0.01);
                pos.setXYZ(1, sbx, sby, 0.01);
                pos.needsUpdate = true;
            });
    }

    function renderWorld(state, now) {
        ensureGrid(state.world.width, state.world.height);
        ensureWalls(state.geometry.walls);
        updateBlocks(state.snapshot.blocks);
        updateShapes(state.snapshot.shapes);
        updateGoals(state.snapshot.goals);
        updateShapeGoals(state.snapshot.shapeGoals);
        updateSwitches(state.snapshot.switches);
        updateDoors(state.snapshot.doors);
        updateCircuit(state);

        const w = canvas.width, h = canvas.height, z = state.cam.z;
        const halfW = (w / 2) / z, halfH = (h / 2) / z;
        camera.left = -halfW; camera.right = halfW; camera.top = halfH; camera.bottom = -halfH;
        camera.position.set(state.cam.x, -state.cam.y, 100);
        camera.lookAt(state.cam.x, -state.cam.y, 0);
        camera.updateProjectionMatrix();

        renderer.render(scene, camera);
    }

    function resize(w, h) {
        renderer.setSize(w, h, false);
    }

    function dispose() {
        disposeObject(scene);
        renderer.dispose();
    }

    return { renderWorld, resize, dispose };
}
