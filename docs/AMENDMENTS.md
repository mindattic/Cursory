---
codex: 1
project: Cursory
code: CUR
layer: amendments
status: living
updated: 2026-09-04
---

# Cursory — Amendments (append-only; an amendment wins over the bible)
> Never rewrite an amendment; supersede it with a new one. Beyond ~25, fold into the bible and
> start a new epoch (note the git tag). History stays in git.

## CUR-A1 — Real rigid bodies replace sum-of-springs; switches/doors retired (supersedes —) {#CUR-A1}
**What changed.** The original physics — a hand-rolled "sum-of-springs" kinematic model with
`SwitchTile` pressure pads and `Door`s gated on them — was replaced by real
[Aether.Physics2D](BIBLE.md#CUR-§4) rigid bodies (engine commit `b12cec4`). Blocks
(`bodyByBlock`), walls (`bodyByWall`), and compound shapes (`bodyByShape`) are all engine bodies;
top-down dry friction is a `FrictionJoint` to a static ground; a grab is a capped-force
`FixedMouseJoint`. All 14 levels (`SeedLevel1`..`SeedLevel14`) are engine-backed and tuned for two
players.

**Why.** Cooperative drag, fulcrum pivots, and torque "fall straight out of the solver" instead of
needing bespoke force math, and the model is latency-forgiving (continuous soft constraint, not
impulse).

**Migration / consequences.**
- `SwitchTile`, `Door`, and `ShapeAttachment` survive as record types only so old level data still
  deserializes; they are **not wired into the engine**. `WorldSnapshot.Switches`/`.Doors` ride
  empty lists — the wire contract is unchanged so the client needed no rewrite.
- The old switch/door levels were **re-themed as cooperative geometry** (gap-heaves, corridors,
  two-pad locks). Re-porting switches + gated doors onto the engine is [frontier](BIBLE.md#CUR-§7).
- `BlockState.StaticFriction` and the legacy `ShapeActor` dynamics fields (`Vx`/`Vy`/`AngVel`/
  `MomentOfInertia`/`StaticFriction`/`RotationalFriction`) are now **reserved / `[JsonIgnore]`d**;
  the move-threshold derives from `Mass` alone ([CUR-LAW-2](BIBLE.md#CUR-LAW-2)).
- The repo `README.md` still describes the old sum-of-springs model in places; `CLAUDE.md` and this
  bible are authoritative where they disagree. (README is a build/run guide, not canon.)

## CUR-A2 — Three parallel client renderers replace the single Canvas2D room.js (supersedes —) {#CUR-A2}
**What changed.** `Cursory.Blazor/wwwroot/js/room.js` (Canvas2D-only) is gone. In its place:
`wwwroot/shared/room-core.js` holds every renderer-agnostic concern — the SignalR connection and
all hub calls/handlers, input (pointer/wheel/keyboard), the pan/zoom camera model, grab picking,
whistle audio, HUD wiring (vote/level/banner/status/pause/toggles), and the bespoke vector/text
overlay (world-label signposts, cursors, tether/attach lines, whistle ripples, block/shape mass
numbers, the minimap). Three engine folders — `wwwroot/canvas2d/`, `wwwroot/three/`,
`wwwroot/babylon/` — each hold a thin `room.js` entry point plus a `renderer.js` "world adapter"
that draws only the solid game world (grid, walls, blocks, shapes, goals, switches, doors,
circuit): canvas2d draws it with plain 2D calls on the same canvas the overlay uses; three/babylon
draw it as flat unlit meshes via a top-down orthographic camera on `#room-canvas`, with the shared
overlay drawn on a second transparent `#room-overlay` canvas stacked on top so cursors/tethers/
labels always line up with whichever engine is rendering the world underneath.

`Cursory.Shared/Components/Pages/Home.razor` moved from `@page "/"` to `@page "/room/{Engine}"`
(`Engine` ∈ `canvas2d`/`three`/`babylon`), picking the matching `{Engine}/room.js` and, for the
WebGL engines, a CDN `<script>` for `three.js`/`babylon.js`. A new
`Cursory.Shared/Components/Pages/EnginePicker.razor` took over `@page "/"` as a landing page
listing all three routes.

**Why.** The user asked for "the same game, different engine" — Three.js and Babylon.js
alongside the original Canvas2D — without duplicating the ~60% of `room.js` that has nothing to do
with drawing (networking, input, camera, picking, HUD, audio).

**Migration / consequences.**
- No backend change: `Cursory.Core`/`RoomHub`/`GameLoopService`/the wire contract
  (`Geometry`/`Snapshot`/`LevelLoaded`, world-pixel units, camelCase) are untouched — confirmed by
  `Cursory.Tests` staying green throughout. [CUR-LAW-1] through [CUR-LAW-9] are unaffected.
- Three.js's classic UMD build no longer exists upstream (r150+ ships ES modules only), so its
  CDN `<script>` is `type="module"`, assigning the namespace to `window.THREE`. Babylon.js's
  classic UMD bundle isn't idempotent to Blazor Server's prerender-then-hydrate double-render of
  `<script>` tags, so its loader is a plain script with a synchronous re-entry guard
  (`window.__cursoryBabylonLoading`) instead. Both `three/renderer.js` and `babylon/renderer.js`
  read `window.THREE`/`window.BABYLON` lazily *inside* `createWorldRenderer()`, not at module
  top level — the dynamic import of `{engine}/room.js` resolves before the CDN script does.
- Real-browser input on the Babylon route required switching `room-core.js`'s listeners from
  `mousedown`/`mousemove`/`mouseup`/`mouseenter`/`mouseleave` to the Pointer Events equivalents:
  Babylon's `Engine` calls `preventDefault()` on its own internal `pointerdown` handling, which
  per spec suppresses the browser's compatibility `mousedown` event entirely — with plain mouse
  listeners, no one could click or grab anything on `/room/babylon` in any browser, not just a
  test harness.
- Babylon's orthographic camera came out mirrored on the X axis relative to Three's/canvas2d's
  for the `upVector`/`setTarget` combination in `babylon/renderer.js` (verified empirically:
  increasing world X rendered further left as the camera panned) — fixed by swapping
  `camera.orthoLeft`/`orthoRight` rather than negating world coordinates. That mirror also flips
  which triangle winding faces the camera, so every flat mesh's material sets
  `backFaceCulling = false`.
- Switches/doors ride empty arrays in the current build regardless of engine (see CUR-A1); the
  three/babylon world adapters render them defensively but have no live data to verify against.
  The Three.js/Babylon.js circuit rendering (Level 14) is simplified relative to canvas2d's (no
  bulb glow gradient, no resistor zigzag texture) — the lit/unlit colour state and wire routing
  that the puzzle actually depends on are preserved.
- Cypress (`cypress/`, `cypress.config.js`, the `package.json` harness) was tried as the automated
  browser gate for the new routes — updated to log in, land on the picker, and navigate into
  `/room/canvas2d` before asserting on room HUD elements — but was removed outright at the user's
  request as not a useful tool in this context. Along the way it surfaced two pre-existing, unrelated
  bugs in what had been an unverified (never CI-wired) test harness: a stale seeded-password
  fixture, and a Blazor Server page-lifecycle race that could clear the login form's username
  field shortly after `/login` loads. Neither was fixed upstream since the harness exposing them
  is now gone; an automated end-to-end gate for Epic E remains open ([CUR-§7]).
