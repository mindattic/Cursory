// Cursory room client — Three.js entry point. All engine variants (canvas2d/three/babylon) share
// shared/room-core.js for networking, input, camera, picking, HUD, and the overlay draws; this
// file only supplies the "world renderer" that draws blocks/walls/shapes/goals/switches/doors/
// circuit as Three.js meshes.
import { start as coreStart, stop as coreStop } from '../shared/room-core.js';
import { createWorldRenderer } from './renderer.js';

// Home.razor emits the three.js CDN <script> tag before this module's dynamic import runs, so
// window.THREE should already exist — but Blazor's enhanced (script-less) navigation between
// routes can occasionally race that load. Wait briefly rather than fail outright.
async function waitForGlobal(name, timeoutMs = 5000) {
    const start = performance.now();
    while (!window[name]) {
        if (performance.now() - start > timeoutMs) return false;
        await new Promise((r) => setTimeout(r, 30));
    }
    return true;
}

export async function start(opts) {
    if (!(await waitForGlobal('THREE'))) { console.error('[cursory] three.js not loaded'); return; }
    return coreStart(opts, createWorldRenderer);
}

export async function stop() {
    return coreStop();
}
