// Cursory room client — Canvas2D entry point. All engine variants (canvas2d/three/babylon) share
// shared/room-core.js for networking, input, camera, picking, HUD, and the overlay draws; this
// file only supplies the "world renderer" that draws blocks/walls/shapes/goals/switches/doors/
// circuit for this engine.
import { start as coreStart, stop as coreStop } from '../shared/room-core.js';
import { createWorldRenderer } from './renderer.js';

export async function start(opts) {
    return coreStart(opts, createWorldRenderer);
}

export async function stop() {
    return coreStop();
}
