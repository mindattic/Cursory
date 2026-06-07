---
codex: 1
project: Cursory
code: CUR
layer: amendments
status: living
updated: 2026-06-07
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
