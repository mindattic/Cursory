# Cursory 1.0.0

**Cooperative cursor puzzles in a shared room.** Sign in, see every other player's cursor in
real time on a 10 000 × 10 000 world, and solve physics puzzles together — drag heavy blocks
into goal zones, thread compound shapes through gaps, wire up circuits. No chat; your only
voice is a *whistle* when you click empty space.

This README is the build/run/tour guide. For the canonical architecture doc, the Laws, and
verified state, see [`docs/BIBLE.md`](docs/BIBLE.md) — this file describes *how to build and
run it*, the bible describes *how to think about it*.

## Table of contents

- [What it is](#what-it-is)
- [Solution layout](#solution-layout)
- [Architecture](#architecture)
- [How multiplayer works](#how-multiplayer-works)
- [The cooperative-drag.html prototype](#the-cooperative-draghtml-prototype)
- [Cypress end-to-end coverage](#cypress-end-to-end-coverage)
- [Seeded accounts](#seeded-accounts)
- [Build, test, run](#build-test-run)
- [Directory layout](#directory-layout)
- [Deploy](#deploy)
- [Canonical documentation](#canonical-documentation)
- [Roadmap](#roadmap)

## What it is

Cursory is an ASP.NET Core Blazor Server web app. Every signed-in player's mouse cursor
renders for every other connected player in real time, and a set of Portal-style puzzles are
solved by those cursors physically cooperating: a heavy body needs two cursors pulling
together, and *where* you grab a body decides how it turns. The server owns the whole
simulation — a client never asserts where an object is, only where its own cursor is.

It is **not** a chat app (there's no text channel — the whistle is the entire comms
vocabulary), **not** client-authoritative, and **not** multi-room yet (one shared in-memory
room). See [`docs/BIBLE.md#CUR-§3`](docs/BIBLE.md) ("what it is NOT") for the full list.

## Solution layout

`Cursory.slnx` (the [slnx](https://github.com/dotnet/sdk) solution format) wires up four
projects:

| Project | Kind | Role |
| --- | --- | --- |
| [`Cursory.Core`](Cursory.Core) | class library | Domain models and services. No ASP.NET dependency. `UserAccount`/`UserRepository` (JSON file store at `%APPDATA%\MindAttic\Cursory\users.json`), `AuthService` (BCrypt + lockout + security-stamp), `RoomState` — the authoritative simulation, backed by the [Aether.Physics2D](https://github.com/nkast/Aether.Physics2D) rigid-body engine (gravity-free, top-down). |
| [`Cursory.Shared`](Cursory.Shared) | Razor class library | Components shared with the host — currently just the gated `Home.razor` page (the room itself). References `Cursory.Core`. |
| [`Cursory.Blazor`](Cursory.Blazor) | ASP.NET Core Web SDK, entry point | Blazor Server host. Cookie auth, antiforgery, rate-limited `/api/auth/login`, `RoomHub` (SignalR), `GameLoopService` (30 Hz physics tick + snapshot broadcast). References both `Cursory.Core` and `Cursory.Shared`. |
| [`Cursory.Tests`](Cursory.Tests) | NUnit 4 | Covers `AuthService`, `RoomState` physics/grabs/eviction, vote/level machinery, circuit evaluation, and level solvability. References `Cursory.Core` only. |

Outside the solution, at the repo root:

- [`cooperative-drag.html`](cooperative-drag.html) — a standalone, dependency-free HTML/JS demo
  of the *original* cooperative-drag concept (see below).
- [`cypress/`](cypress) + [`cypress.config.js`](cypress.config.js) — browser end-to-end smoke
  tests against a running `Cursory.Blazor` instance.
- [`docs/`](docs) — the Codex documentation layer (bible, amendments, user stories, rfcs).
- [`tools/codex.ps1`](tools/codex.ps1) — the Codex digest/doctor tool.

## Architecture

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

(Reproduced from [`docs/BIBLE.md#CUR-§4`](docs/BIBLE.md); that's the canonical copy — if this
one drifts, the bible wins.)

### Cursory.Core

- `Models/GameState.cs` — the domain nouns: `CursorState`, `BlockState`, `ShapeActor` +
  `ShapePiece` (compound rigid bodies), `Wall`, `GoalZone`/`ShapeGoal`, `CircuitComponent`/
  `Terminal`/`Wire` (the electronics level), `Whistle`, `RoomVote`/`VoteKind`, `WorldLabel`,
  `WorldSnapshot`/`WorldGeometryMessage`, and legacy unwired types (`SwitchTile`, `Door`,
  `ShapeAttachment`) kept only so old level data still deserializes.
- `Models/UserAccount.cs` — username-based account: BCrypt `PasswordHash`, `SecurityStamp`,
  `Color`, `Role`.
- `Services/RoomState.cs` — the authoritative simulation. One Aether.Physics2D `World`
  (gravity-free, top-down). `Step()` drives each grab joint, steps the engine, syncs poses
  back, updates tethers/leashes, evaluates goals + the circuit, ages out whistles/votes.
  `Snapshot()`/`GeometryMessage()` build defensive copies for broadcast. All engine access is
  serialized under a `worldLock`. Seeds 14 levels (`SeedLevel1`..`SeedLevel14`).
- `Services/AuthService.cs` — BCrypt hashing (work factor 12), per-account lockout (10
  failures / 5 min), constant-time verify on missing users, security-stamp invalidation on
  password/role change, policy-bypassing idempotent `SeedUser`, `SetAllPasswords`, strict
  `CreateUser` policy, and the `IsLocalUrl` open-redirect filter.
- `Services/UserRepository.cs` — thread-safe JSON-file store with atomic
  temp-file-then-`File.Move` writes, defensive copies on read.
- `Services/CursoryServices.cs` — `AddCursoryCore` DI registration; resolves
  `Cursory:UsersPath` config or defaults to `%APPDATA%\MindAttic\Cursory\users.json`.
- Package references: `Aether.Physics2D`, `BCrypt.Net-Next`, plus the
  `Microsoft.Extensions.*.Abstractions` trio (DI, Configuration, Hosting) — no ASP.NET Core
  dependency, so this project can be tested and reused headlessly.

### Cursory.Shared

- `Components/Pages/Home.razor` — the gated room page: opens the SignalR connection to
  `/hubs/room` and imports `wwwroot/js/room.js` (which physically lives in `Cursory.Blazor`,
  the hosting project).
- Razor class library (`Microsoft.NET.Sdk.Razor`); references `Cursory.Core` and pulls in
  `Microsoft.AspNetCore.Components.Web`/`.Authorization`.

### Cursory.Blazor

- `Program.cs` — composition root: `AddCursoryCore`, `GameLoopService` as a hosted
  `BackgroundService`, Razor Components + interactive server render mode, SignalR (receive
  cap `8 KB`), cookie authentication (`Cursory.Auth` cookie, 30-day sliding expiration,
  `SecurityStamp` re-validated via `OnValidatePrincipal` on every request), per-IP fixed-window
  rate limiting on login (10/min), forwarded-headers handling for Azure's proxy, and the
  one-shot seed of the two demo accounts on startup.
- `Hubs/RoomHub.cs` — write-only SignalR hub mapped at `/hubs/room`: `Move`, `Grab`,
  `GrabShape`, `GrabWall`, `GrabWireEnd`, `Release`, `Whistle`, `StartResetVote`,
  `StartLevelVote`, `CastVote`, `SetCursorCollision`, `SetSegmentedTether`. Methods never
  return state — everything rides the broadcast snapshot.
- `Services/GameLoopService.cs` — the 30 Hz `BackgroundService`: steps `RoomState`, evicts
  stale cursors every 30 ticks, rebroadcasts geometry / announces a level on change, broadcasts
  the snapshot, idles when nobody is connected, logs slow ticks.
- `Components/` — `App.razor`, `Layout/MainLayout.razor`, `Pages/Login.razor`,
  `Pages/Error.razor`, `RedirectToLogin.razor`, `Routes.razor`.
- `wwwroot/js/room.js` — the client: canvas rendering, `clientToWorld` coordinate transform,
  pan/zoom, interpolation, minimap, whistle audio/visuals, HUD (level select, reset vote,
  connection-status pill).
- Two auth endpoints: `POST /api/auth/login` (antiforgery + rate limit + open-redirect guard,
  issues the cookie) and `POST /api/auth/logout`.
- `ASP.NET Core Web SDK` project; the only project with `Microsoft.NET.Sdk.Web` and the only
  entry point (`dotnet run --project Cursory.Blazor`).

### Cursory.Tests

NUnit 4 project referencing `Cursory.Core` only (via `InternalsVisibleTo`), so it exercises
the simulation and auth headlessly with no ASP.NET Core host required:

| File | Covers |
| --- | --- |
| `AuthServiceTests.cs` | Seed idempotency, `SetAllPasswords`, lockout, weak-password rejection, `IsLocalUrl` open-redirect filter. |
| `UserRepositoryTests.cs` | Empty-path rejection at construction. |
| `RoomStateTests.cs` | Edge-snap grab, clamp-to-body, pull-in-mass-units, single cursor moves a light block but not a heavy one, two cursors break friction, opposing pulls cancel, offset pulls rotate, detach removes force, shape edge-grab + drag, leash length, cursor-vs-wall nudge, segmented-tether wrap/corner-catch, NaN drop, stale-cursor eviction, geometry on its own channel, `LevelCount == 14`, every level seeds-and-steps. |
| `SolvabilityTests.cs` | Every block level is auto-solvable by two virtual cursors; rotation/thread shape levels are provably movable. |
| `VoteAndLevelTests.cs` | Solo quorum resolves at once, two-voter needs both, early-reject when quorum unreachable, level switch moves state + queues rebroadcast, no-op/out-of-range rejected. |
| `CircuitTests.cs` | Bulb lights on a complete series loop; dark on a gap; dark when the resistor is bypassed. |

## How multiplayer works

The server owns the simulation. Clients send only cursor position (and grab/release/whistle
events) at 30 Hz over SignalR. `GameLoopService` runs a fixed-rate physics tick on `RoomState`
and broadcasts an authoritative `WorldSnapshot` to every connected client. Clients render and
interpolate — they never tell the server where a body is.

Static world geometry (walls, labels) is delivered once on connect via `WorldGeometryMessage`,
not on every tick — see `CUR-LAW-6` in the bible.

**Cooperative drag.** Physics runs in Aether.Physics2D (Box2D, gravity-free, top-down). Each
grab is a `FixedMouseJoint` force-capped at a single-cursor ceiling; each block's `FrictionJoint`
to a static ground gives it dry friction. A single cursor can't break a heavy block free — two
cursors stack their pull past the friction threshold. Offset grabs produce real torque, so
cooperating cursors can rotate a body through a gap.

**Compound shapes.** `ShapeActor` bodies are built from multiple `ShapePiece` rectangles in
body-local space (e.g. an L-shape). Grabs on shapes drive full rigid-body torque via the
Aether solver.

**Circuit levels.** `CircuitComponent` (battery, resistor, bulb), `Terminal`, and `Wire` records
model breadboard-style wiring. A cursor grabs a wire end and drags it onto a terminal; the
evaluator lights the bulb when a closed series loop is formed.

**Whistle.** Click empty space → server records a `Whistle` and ships it on the next snapshot.
Clients render a coloured ripple and play a Web Audio tone keyed by each player's colour.

**Pan & minimap.** The viewport is a single HTML5 canvas. Drag empty space to pan over the
10 000 × 10 000 world; a minimap in the corner shows all cursors and the viewport rect.

**Voting.** Any player can propose a level reset or level switch; the action fires when YES
votes reach a 2/3 majority of the connected roster. Vote times out after 15 s (450 ticks at
30 Hz).

## The cooperative-drag.html prototype

[`cooperative-drag.html`](cooperative-drag.html) is a standalone, dependency-free HTML page
(inline CSS + vanilla JS, one `<canvas>`, no build step — open it directly in a browser) that
demonstrates the *original* cooperative-drag mechanic Cursory was built around, before the
engine port:

- A single draggable block with a hand-rolled **sum-of-springs** kinematic model: each
  attached cursor contributes a spring force toward its anchor point (`SPRING_K`), the forces
  sum, and the block only starts moving once the summed force magnitude exceeds a tunable
  **friction threshold** (a slider in the UI).
- Click the block to attach a cursor at that point; release and click again to attach another
  cursor while the first stays put. Re-grab an existing cursor by clicking near its head.
  A live HUD prints the net force magnitude and whether the block is "Moving" or "Below
  threshold" (block renders green vs. gray to match).

This is the same mechanic Cursory launched with, and it's a good five-minute way to feel *why*
two cursors are needed to move a heavy block without spinning up the full app. It is **not**
wired to the current game — the shipped app replaced this hand-rolled spring model with real
Aether.Physics2D rigid bodies, `FrictionJoint`s, and `FixedMouseJoint` grabs (see
[`CUR-A1`](docs/AMENDMENTS.md#CUR-A1)). The file is not part of the `.slnx` solution and isn't
built, tested, or served by `Cursory.Blazor`; it's a standalone reference kept at the repo root.

## Cypress end-to-end coverage

[`cypress/`](cypress) drives a *running* `Cursory.Blazor` instance in a real browser —
this is the only automated coverage of the SignalR/UI path end to end (see `docs/BIBLE.md`
§6, marked 🟡 since it isn't wired into `dotnet test`).

| Spec | Covers |
| --- | --- |
| [`cypress/e2e/login.cy.js`](cypress/e2e/login.cy.js) | Seeded user (`gungreeneyes`) signs in and lands on the room, with `#room-canvas` visible and the HUD showing the username. Wrong password is rejected back to `/login?error=invalid`. Unauthenticated `/` redirects to `/login`. |
| [`cypress/e2e/level-select.cy.js`](cypress/e2e/level-select.cy.js) | The level `<select>` exposes all 14 levels; the Reset button and connection-status pill (`Live`) render; selecting a different level triggers an immediate switch under solo quorum (`ceil(2/3 × 1) = 1`). |

Configuration ([`cypress.config.js`](cypress.config.js)): `baseUrl` defaults to
`http://localhost:5238` (override with `CYPRESS_BASE_URL`), 1600×900 viewport, `chromeWebSecurity`
disabled, generous timeouts (15 s command, 60 s page load) to tolerate the SignalR handshake.

```powershell
npm install            # installs Cypress (see package.json)
dotnet run --project Cursory.Blazor   # in one terminal — app must already be running
npm test               # headless: cypress run
npm run test:open      # interactive runner: cypress open
```

## Seeded accounts

Seeded idempotently on first run. The seed bypasses the strict password policy;
`SetAllPasswords` keeps every account on the current operator-chosen password:

| Username | Password | Colour |
| --- | --- | --- |
| `gungreeneyes` | `800085` | `#D85A30` |
| `gideonkain` | `800085` | `#378ADD` |

New accounts created via the API enforce the strict policy (≥ 8 chars, upper + lower + digit +
special). There is no self-service signup flow — accounts are operator-seeded only.

## Build, test, run

```powershell
dotnet build Cursory.slnx
dotnet test  Cursory.slnx
dotnet run --project Cursory.Blazor
# https://localhost:7238  (or http://localhost:5238)
```

To see cooperative play locally, open a second browser window (or incognito) and sign in as
the other seeded user — both cursors appear in the same room.

`TreatWarningsAsErrors=true` is set in [`Directory.Build.props`](Directory.Build.props);
missing XML doc comments are downgraded to warnings (`CS1591`) — that's the only warning class
allowed to survive a build. `Nullable`/`ImplicitUsings` are enabled solution-wide;
[`global.json`](global.json) pins the SDK to `rollForward: latestMajor`.

## Directory layout

```
Cursory/
├─ Cursory.slnx                  solution file (Core, Shared, Blazor, Tests)
├─ Directory.Build.props         shared MSBuild settings (nullable, warnings-as-errors, ...)
├─ global.json                   .NET SDK pin
├─ NuGet.config
├─ cooperative-drag.html         standalone sum-of-springs prototype (see above)
├─ cypress.config.js             Cypress e2e config
├─ package.json                  Cypress-only npm harness ("cursory-e2e")
├─ Cursory.Core/                 domain models + services (no ASP.NET dependency)
│  ├─ Models/                    GameState.cs, UserAccount.cs
│  └─ Services/                  RoomState.cs, AuthService.cs, UserRepository.cs, CursoryServices.cs
├─ Cursory.Shared/                Razor component library
│  └─ Components/Pages/Home.razor
├─ Cursory.Blazor/                ASP.NET Core Blazor Server host (entry point)
│  ├─ Program.cs
│  ├─ Hubs/RoomHub.cs
│  ├─ Services/GameLoopService.cs
│  ├─ Components/                App.razor, Layout/, Pages/Login.razor, Pages/Error.razor, ...
│  └─ wwwroot/js/room.js          client renderer
├─ Cursory.Tests/                 NUnit 4 suite (AuthServiceTests, RoomStateTests, ...)
├─ cypress/
│  ├─ e2e/                        login.cy.js, level-select.cy.js
│  └─ support/e2e.js
├─ docs/                          Codex documentation layer (see below)
│  ├─ BIBLE.md, AMENDMENTS.md, USER_STORIES.md, BIBLE.digest.md (generated)
│  └─ rfc/
├─ tools/
│  ├─ codex.ps1                   Codex digest/doctor tool
│  └─ build-readme.ps1            thin wrapper → shared README→HTML engine
└─ .github/workflows/azure-deploy.yml
```

## Deploy

Targeted at Azure App Service (`cursory`). The workflow at
[`.github/workflows/azure-deploy.yml`](.github/workflows/azure-deploy.yml) fires on push to
`main`: restore → publish `Cursory.Blazor` → upload artifact → deploy to the `cursory` slot at
**https://cursory.azurewebsites.net**.

To turn the deploy on (one-time setup):

1. Provision an Azure App Service named `cursory` (.NET 10 runtime, Linux or Windows).
2. Download the publish profile from the App Service in the Azure portal.
3. Add the GitHub secret `AZURE_WEBAPP_PUBLISH_PROFILE` to `mindattic/Cursory`.
4. Push to `main`.
5. Flip `MindAttic.Deploy/projects.json → apps[].cursory.disabled` to `false`.

## Canonical documentation

This repo has adopted the MindAttic "Codex" documentation standard — canon lives in `docs/`,
layered, each fact owned by exactly one layer:

- **[`docs/BIBLE.md`](docs/BIBLE.md)** (L0) — what Cursory IS / is NOT, architecture canon, the
  Laws (`CUR-LAW-1`..`CUR-LAW-9`), verified state, active frontier, quality bar, glossary.
- **[`docs/AMENDMENTS.md`](docs/AMENDMENTS.md)** (L1) — append-only change log; an amendment
  wins over the bible. Currently one entry, `CUR-A1`: Aether.Physics2D rigid bodies replaced the
  original sum-of-springs model (see [above](#the-cooperative-draghtml-prototype)); switches and
  gated doors were retired in the process.
- **[`docs/USER_STORIES.md`](docs/USER_STORIES.md)** (L2) — `CUR-US-<Epic><n>` stories; every ✅
  cites its verifying NUnit test.
- **[`docs/rfc/`](docs/rfc)** — design notes that graduate into the bible + stories.
- **[`docs/BIBLE.digest.md`](docs/BIBLE.digest.md)** — generated by `tools/codex.ps1 digest`;
  never hand-edited; injected as session-start context by a Claude Code hook.

## Roadmap

- [x] Cooperative-drag puzzle — heavy block + goal zone
- [x] Pannable 10 000 × 10 000 world with minimap
- [x] Whistle on click, per-colour Web Audio tone
- [x] 14 engine-backed levels (gap-heaves, corridors, two-pad locks)
- [x] Compound rigid body (L-shape) with rotation — thread-the-needle level
- [x] Dotted pull-line tether with segmented wrap
- [x] Connection-status pill + SignalR auto-reconnect
- [x] Stale-cursor eviction
- [x] Static geometry delivered once on connect (not at 30 Hz)
- [x] Room voting — reset or level switch (2/3 quorum, 15 s timeout)
- [x] Circuit levels — wires, terminals, battery/resistor/bulb
- [x] Azure App Service workflow + `MindAttic.Deploy` entry (idle until App Service exists)
- [ ] Multiple rooms / lobby
- [ ] Per-room state persistence (currently in-memory)
- [ ] Mobile (touch) input
