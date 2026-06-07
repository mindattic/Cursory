---
codex: 1
project: Cursory
code: CUR
layer: bible
status: living
updated: 2026-06-07
---

# Cursory — Project Bible
> Single source of truth for what Cursory IS, is NOT, and the rules that keep it coherent.
> README says how to build/run; this says how to think about the system.

Status legend: ✅ done (verified by a test/build) · 🟡 partial · ⬜ planned · 🗑️ cut · `living`.

## 1. The one sentence {#CUR-§1}
Cursory is a Blazor Server multiplayer web app where every signed-in player's cursor renders
for every other player in real time on a shared 10 000 × 10 000 world, and Portal-style puzzles
are solved by cursors physically cooperating — a heavy body needs two cursors pulling together,
and where you grab decides how it turns.

## 2. The product promise {#CUR-§2}
- **Wordless co-op.** No chat; your only voice is a *whistle* on click (a coloured ripple + a
  per-colour Web Audio tone). Cooperation is expressed through the physics, not text.
- **Server-authoritative, latency-forgiving.** The server owns one [`RoomState`](#CUR-§4)
  simulation. Clients send only their cursor position + grab/release/whistle/vote events at
  30 Hz; the server runs a fixed-rate physics tick and broadcasts an authoritative
  `WorldSnapshot`. Because grab force is a continuous soft constraint, not an impulse, latency
  degrades feel gently instead of breaking determinism.
- **Mass is the one legible dial.** A body's move-threshold is `Mass × ForcePerMass`; a cursor's
  reported pull is `force ÷ ForcePerMass`. So a body moves exactly when the pulls on it sum past
  its printed Mass — a number you can read and reason about mid-puzzle.
- **A teaching curve.** 14 engine-backed levels, tuned for two players, walk from "drag one light
  block" through cooperative heaves, corner-threading shapes, to a series-circuit electronics
  puzzle.

## 3. What it is NOT {#CUR-§3}
- **NOT client-authoritative.** A client never tells the server where a block is — it sends "my
  cursor is at (x,y)" and the physics tick decides what that means. Never trust a client position
  for an object's authoritative state. (verified by the `RoomStateTests` mechanics suite.)
- **NOT a chat/social app.** There is deliberately no text channel. The whistle is the entire
  comms vocabulary.
- **NOT a hand-rolled physics toy anymore.** The original "sum-of-springs" kinematics with switch
  tiles and gated doors are gone; blocks, walls, and compound shapes are real
  [Aether.Physics2D](#CUR-§4) rigid bodies. `SwitchTile`/`Door` models survive only as
  unwired record types; `BlockState.StaticFriction` and the legacy `ShapeActor` dynamics fields
  are reserved/`[JsonIgnore]`d. See [CUR-A1](AMENDMENTS.md#CUR-A1).
- **NOT multi-room (yet).** One shared room, in-memory only. Lobbies and per-room persistence are
  [frontier](#CUR-§7).
- **NOT self-service signup.** There is no public registration; accounts are operator-seeded.
  `CreateUser` exists and enforces a strict policy, but is not exposed as a signup flow.

## 4. Architecture canon {#CUR-§4}

```
                 browser (HTML5 canvas)
                 wwwroot/js/room.js  ── clientToWorld, pan/zoom, render+interp
                        │   ▲
            Move/Grab/  │   │  Geometry (once) + Snapshot (30 Hz) + LevelLoaded
            Release/    │   │
            Whistle/    ▼   │
            Vote   ┌─────────────────────────────────────────────┐
                   │ Cursory.Blazor (ASP.NET Core Blazor Server)  │
                   │  Program.cs  cookie auth · antiforgery ·      │
                   │              rate-limited /api/auth/login ·   │
                   │              forwarded headers · seed users   │
                   │  Hubs/RoomHub.cs        write-only SignalR     │
                   │  Services/GameLoopService.cs  30 Hz tick loop  │
                   │  Cursory.Shared/…/Home.razor  gated room page  │
                   └───────────────┬─────────────────────────────┘
                                   │ owns
                   ┌───────────────▼─────────────────────────────┐
                   │ Cursory.Core                                 │
                   │  RoomState  ── Aether.Physics2D World         │
                   │     (gravity-free, top-down, worldLock)       │
                   │  AuthService · UserRepository (JSON file)     │
                   └───────────────────────────────────────────────┘
```

### 4.1 Projects
- `Cursory.Core/Cursory.Core.csproj` — domain models + services. No ASP.NET dependency.
- `Cursory.Shared/Cursory.Shared.csproj` — Razor components rendered by the host
  (`Cursory.Shared/Components/Pages/Home.razor`, the gated room page).
- `Cursory.Blazor/Cursory.Blazor.csproj` — the ASP.NET Core Blazor Server host (entry point).
- `Cursory.Tests/Cursory.Tests.csproj` — NUnit test project.
- Solution: `Cursory.slnx`. Shared build config: `Directory.Build.props`.

### 4.2 Domain model (the NOUNS) — `Cursory.Core/Models/GameState.cs`
- **`CursorState`** — one connected player's live state: world `X`/`Y`, `Color`, what it's
  `AttachedBlockId`/`AttachedShapeId`/`AttachedWallId`/`AttachedWireId`, the grab `AnchorLocal*`
  (server-internal) / `AnchorWorld*` (wire), `PullMass`, and the `TetherPivots` polyline.
- **`BlockState`** — an axis-aligned draggable rigid body: `X`/`Y`/`W`/`H`/`Angle`/`Mass`/`Color`.
  `StaticFriction` is reserved (unused). `Vx`/`Vy` are server-internal (`[JsonIgnore]`).
- **`ShapeActor`** + **`ShapePiece`** — a compound rigid body built from local-frame box pieces
  (an L is two pieces); rotates under offset pulls. Legacy dynamics fields are `[JsonIgnore]`d.
- **`Wall`** — a static rectangle (real engine body; bodies collide, cursors optionally collide).
- **`GoalZone`** / **`ShapeGoal`** — a target rectangle; solved when the block centre / every
  shape piece centre is inside it.
- **`CircuitComponent`** / **`Terminal`** / **`Wire`** — the Level 14 electronics: a battery,
  resistor, and bulb wired by dragging wire ends onto terminals.
- **`Whistle`** — a click ping (color + position + tick), short-lived in a ring buffer.
- **`RoomVote`** + **`VoteKind`** — a pending reset / level-switch vote with a 2/3 quorum.
- **`WorldLabel`** — a world signpost titling each puzzle area.
- **`WorldSnapshot`** / **`WorldGeometryMessage`** — the per-tick broadcast vs. the once-per-connect
  static geometry.
- **`SwitchTile`** / **`Door`** / **`ShapeAttachment`** — 🗑️ unwired legacy record types kept only
  so old level data deserializes (see [CUR-A1](AMENDMENTS.md#CUR-A1)).
- **`UserAccount`** + **`UserRoles`** (`Cursory.Core/Models/UserAccount.cs`) — username-based
  account, BCrypt `PasswordHash`, `SecurityStamp`, `Color`, `Role`.
- **`WorldGeometry`** — compile-time constants: `Width`/`Height` = 10 000.

### 4.3 Key services (the VERBS)
- **`RoomState`** (`Cursory.Core/Services/RoomState.cs`) — the authoritative simulation, backed by
  one Aether.Physics2D `World` (gravity-free, top-down). `Step()` drives each grab joint to its
  cursor, steps the engine, syncs poses back, updates tethers/leashes, evaluates goals + the
  circuit, and ages out whistles/votes. `Snapshot()` / `GeometryMessage()` build defensive copies.
  `TryAttach`/`TryAttachShape`/`TryAttachWall`/`TryAttachWireEnd`/`Detach` manage grabs. All engine
  access is serialised under `worldLock`. Seeds `SeedLevel1`..`SeedLevel14` (`LevelCount = 14`).
- **`AuthService`** (`Cursory.Core/Services/AuthService.cs`) — BCrypt hashing (work factor 12),
  per-account lockout (10 failures / 5 min), constant-time verify on missing users,
  security-stamp invalidation on password/role change, `SeedUser` (policy-bypassing, idempotent,
  case+password migrating), `SetAllPasswords`, strict `CreateUser` policy, and the `IsLocalUrl`
  open-redirect filter.
- **`UserRepository`** (`Cursory.Core/Services/UserRepository.cs`) — thread-safe JSON-file store
  with atomic temp-file-then-`File.Move` writes; defensive-copies on every read; rejects an empty
  path at construction.
- **`GameLoopService`** (`Cursory.Blazor/Services/GameLoopService.cs`) — the 30 Hz `BackgroundService`:
  `room.Step()`, evict stale cursors (every 30 ticks), rebroadcast geometry / announce a level on
  change, broadcast the `Snapshot`; idles when no one is connected; logs slow ticks.
- **`RoomHub`** (`Cursory.Blazor/Hubs/RoomHub.cs`) — the write-only SignalR hub: `Move`, `Grab`,
  `GrabShape`, `GrabWall`, `GrabWireEnd`, `Release`, `Whistle`, `StartResetVote`, `StartLevelVote`,
  `CastVote`, `SetCursorCollision`, `SetSegmentedTether`. Methods never return state.
- **`CursoryServices.AddCursoryCore`** — the DI registration (one front door's half of
  [HOUSE-LAW-6](#CUR-§5)); resolves `Cursory:UsersPath` or defaults to `%APPDATA%\MindAttic\Cursory\users.json`.

## 5. The Laws {#CUR-§5}

Cursory **inherits the org-wide house rules** in
[`../../MindAttic.HouseRules.md`](../../MindAttic.HouseRules.md) (`HOUSE-LAW-1`..`HOUSE-LAW-9`).
Of note here: [HOUSE-LAW-8] (done is verified, not asserted) governs every ✅ in
[USER_STORIES.md](USER_STORIES.md) and [§6](#CUR-§6); [HOUSE-LAW-1] (whole-number versioning);
[HOUSE-LAW-9] (`psst` only on explicit request).

> Cursory does **not** currently adopt `MindAttic.Authentication` ([HOUSE-LAW-7]) — it ships a
> bespoke username/cookie `AuthService` modelled on StreetSamurai, with a JSON file store rather
> than SQL. This is a conscious deviation for a two-seeded-account spike; recorded as
> [CUR-LAW-9](#CUR-LAW-9) and a migration candidate in [CUR-§7](#CUR-§7).

Project-specific laws (the conventions that keep *this* codebase coherent):

### CUR-LAW-1 — The server owns the simulation {#CUR-LAW-1}
A client sends only its cursor position + one-shot grab/release/whistle/vote events. It never
asserts an object's authoritative position. All physics, collision, and leash resolution are
server-side and ride the single broadcast `Snapshot`, so every client sees the same world.

### CUR-LAW-2 — Mass is the one legible dial {#CUR-LAW-2}
A body's move-threshold is `Mass × ForcePerMass` (its `FrictionJoint.MaxForce`); a cursor's
reported pull is `force ÷ ForcePerMass`. A body moves exactly when the pulls on it sum past its
Mass. One grab tops out at `SingleGrabMaxMass` (= `GrabMaxForce ÷ ForcePerMass`); heavier needs
cooperating cursors. Inertia (`InertiaKgPerMass`) is decoupled so heft ≠ threshold.

### CUR-LAW-3 — World coordinates on the wire; metres in the engine {#CUR-LAW-3}
Every payload to/from the server is in world pixels. `room.js` does the canvas↔world transform
*once* (`clientToWorld`). The Aether `World` runs in metres at `PixelsPerMeter = 100`, converted
only at the engine boundary (`ToM`/`ToPx`). The wire format never carries metres.

### CUR-LAW-4 — camelCase wire format (Web JSON defaults) {#CUR-LAW-4}
ASP.NET Core's `JsonSerializerDefaults.Web` applies: every C# `PascalCase` property serializes to
`camelCase`. `room.js` reads `c.attachedBlockId`, not `c.AttachedBlockId`. Internal-only fields
(`Vx`/`Vy`, `AnchorLocal*`, legacy `ShapeActor` dynamics) are `[JsonIgnore]`d to stay off the wire.

### CUR-LAW-5 — Cookie auth claims are a fixed contract {#CUR-LAW-5}
The login `Claim[]` carries `UserId`, `Username`, `Color`, `SecurityStamp` (plus name/role).
Adding a claim means updating the `Program.cs` `Claim[]` *and* every consumer (`RoomHub`,
`Home.razor`). `SecurityStamp` is re-validated on every request via `OnValidatePrincipal`.

### CUR-LAW-6 — Static geometry rides its own channel {#CUR-LAW-6}
Walls + labels never change at runtime, so they ship once per connection (and on level
switch/reset) via the `Geometry` message, never on the 30 Hz `Snapshot`. A level change sets the
geometry-rebroadcast + level-announcement flags the loop reads-and-clears.

### CUR-LAW-7 — Grabs land on the edge {#CUR-LAW-7}
`TryAttach*` projects the click to the nearest perimeter point (`ProjectToEdge`; shapes pick the
nearest piece first). You grab the rim, not the interior — an edge/corner grab is what gives a
body real torque. Raw click coords (not the collision-resolved cursor) drive grab picking.

### CUR-LAW-8 — Seeded users bypass the policy; everyone else doesn't {#CUR-LAW-8}
`SeedUser` writes the BCrypt hash directly, is idempotent, and migrates case/password on config
change. `CreateUser` enforces ≥ 8 chars with upper + lower + digit + special. The two seeded
accounts (`gungreeneyes`, `gideonkain`) use an operator-chosen password verbatim. The operator
directive `SetAllPasswords("800085")` runs idempotently at boot. (Implements [HOUSE-LAW-3] —
no secret is hard-coded as a credential store; the seed password is an operator-chosen demo value.)

### CUR-LAW-9 — Bespoke auth is a recorded deviation from HOUSE-LAW-7 {#CUR-LAW-9}
Until/unless Cursory adopts `MindAttic.Authentication`, the bespoke username/cookie `AuthService`
+ JSON `UserRepository` is the sanctioned scheme. Any new auth surface mirrors this pattern (BCrypt,
lockout, security-stamp revalidation, antiforgery, per-IP login rate limit) rather than inventing
a third way. Migration is tracked in [CUR-§7](#CUR-§7).

## 6. Verified state {#CUR-§6}
Build/test evidence (recorded 2026-06-07): see the build/test run in the implementation report and
[USER_STORIES.md](USER_STORIES.md) for the per-story test citations.

- ✅ **Build**: `dotnet build Cursory.slnx` succeeds with `TreatWarningsAsErrors=true`
  (only `CS1591` missing-doc warnings are non-fatal). *(See §8.)*
- ✅ **Auth**: seed idempotency, case/password migration, `SetAllPasswords`, lockout after 10
  failures, weak-password rejection, `IsLocalUrl` open-redirect filter — `AuthServiceTests`.
- ✅ **Repository**: empty-path rejection — `UserRepositoryTests`.
- ✅ **Physics mechanics**: edge-snap grab, clamp-to-body, pull-in-mass-units, single cursor moves
  a light block but not a heavy one, two cursors break friction, opposing pulls cancel, offset
  pulls rotate, detach removes force, shape edge-grab + drag, leash length, cursor-vs-wall nudge
  (on/off), segmented-tether wrap/corner-catch, NaN drop, stale-cursor eviction, geometry on its
  own channel, `LevelCount == 14`, every level seeds-and-steps — `RoomStateTests`.
- ✅ **Solvability**: every block level is auto-solvable by two virtual cursors; rotation/thread
  shape levels are provably movable — `SolvabilityTests`.
- ✅ **Votes / levels**: solo quorum resolves at once, two-voter needs both, early-reject when
  quorum unreachable, level switch moves state + queues rebroadcast, no-op/out-of-range rejected —
  `VoteAndLevelTests`.
- ✅ **Circuit**: bulb lights on a complete series loop; dark on a gap; dark when the resistor is
  bypassed — `CircuitTests`.
- 🟡 **Realtime UI / SignalR end-to-end**: exercised only by hand + `cypress/` (login,
  level-select). No automated browser run is wired into `dotnet test`; the live multiplayer feel
  (render/interp, pan/minimap, whistle audio) is unverified by an automated gate.
- ⬜ **Deploy**: `.github/workflows/azure-deploy.yml` is wired but idle — no `cursory` App Service
  / publish-profile secret yet.

## 7. Active frontier {#CUR-§7}
- ⬜ Multiple rooms / a lobby (today: one shared in-memory room).
- ⬜ Per-room state persistence (today: in-memory, lost on restart).
- ⬜ Mobile / touch input.
- ⬜ Port switches + gated doors onto the engine (the re-themed levels stand in for them today).
- 🟡 Adopt `MindAttic.Authentication` ([HOUSE-LAW-7]) — replacing the bespoke `AuthService` +
  JSON store with the SQL-backed shared library. Recorded deviation: [CUR-LAW-9](#CUR-LAW-9).
- 🟡 Turn the Azure deploy on (provision App Service, set the publish-profile secret, flip
  `MindAttic.Deploy/projects.json → apps[].cursory.disabled`).
- See [docs/rfc/](rfc/) for design notes graduating into this bible + the stories.

## 8. Quality bar {#CUR-§8}
Definition of done for a feature (refines [HOUSE-LAW-8]):
1. **Clean build** of `Cursory.slnx` with `TreatWarningsAsErrors=true` (`Directory.Build.props`).
   A missing XML doc comment (`CS1591`) is the only allowed warning; anything else fails the build.
2. **Green `dotnet test Cursory.slnx`** (NUnit). New physics behaviour adds a `RoomStateTests` /
   `SolvabilityTests` case asserting the *mechanic* (not a tuned engine constant); new auth/vote/
   circuit behaviour adds to the matching fixture.
3. **Server-authoritative** ([CUR-LAW-1]): no new client-asserted state; new mutations go through
   `RoomState` under `worldLock` and ride the `Snapshot`.
4. **Wire discipline** ([CUR-LAW-3], [CUR-LAW-4]): world-pixel payloads, camelCase, internal-only
   fields `[JsonIgnore]`d.
5. A ✅ in [USER_STORIES.md](USER_STORIES.md) names its verifying test; otherwise it is 🟡/⬜.

## 9. Glossary {#CUR-§9}
- **World** — the 10 000 × 10 000 px shared coordinate space (`WorldGeometry`).
- **Snapshot** — the authoritative per-tick broadcast of all changing state (`WorldSnapshot`).
- **Geometry message** — the once-per-connection static walls + labels (`WorldGeometryMessage`).
- **Tick** — one 30 Hz simulation step (`RoomState.Step`, driven by `GameLoopService`).
- **Mass** — a body's printed weight; the move-threshold in mass-units ([CUR-LAW-2]).
- **Pull (PullMass)** — a single cursor's tether force in mass-units, capped at `SingleGrabMaxMass`.
- **Grab / tether / leash** — a `FixedMouseJoint` from a body edge anchor to the cursor; the cursor
  is held within `MaxPullPx` (the leash) of the anchor.
- **Segmented tether** — the optional rope mode that catches a body's corners and spins it.
- **Whistle** — a click ping: a coloured ripple + a per-colour Web Audio tone; the only comms.
- **Vote** — a reset or level-switch needing a 2/3 quorum of the player roster at vote-start.
- **Seeded account** — an operator-created account (`gungreeneyes`, `gideonkain`) that bypasses
  the password policy.
- **PixelsPerMeter** — 100; the world-pixel ↔ engine-metre scale ([CUR-LAW-3]).
