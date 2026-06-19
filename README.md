# Cursory 1.0.0

**Cooperative cursor puzzles in a shared room.** Sign in, see every other player's
cursor in real time on a 10 000 × 10 000 world, and solve physics puzzles together —
drag heavy blocks into goal zones, thread compound shapes through gaps, wire up circuits.
No chat; your only voice is a *whistle* when you click empty space.

## Projects

| Project          | Role |
| ---------------- | ---- |
| `Cursory.Core`   | Domain models, `UserAccount`/`UserRepository` (JSON file store at `%APPDATA%\MindAttic\Cursory\users.json`), `AuthService` (BCrypt + lockout + security-stamp), `RoomState` backed by the Aether.Physics2D rigid-body engine (gravity-free, top-down). |
| `Cursory.Shared` | Razor component library shared with the host — currently just the gated `Home` page (the room). |
| `Cursory.Blazor` | ASP.NET Core Blazor Server host. Cookie auth, antiforgery, rate-limited `/api/auth/login`, `RoomHub` (SignalR), `GameLoopService` (30 Hz physics tick + snapshot broadcast). |
| `Cursory.Tests`  | NUnit test suite covering `AuthService`, `RoomState` physics/grabs/eviction, vote/level machinery, circuit evaluation, and level solvability. |

## How multiplayer works

The server owns the simulation. Clients send only cursor position (and grab/release/whistle
events) at 30 Hz over SignalR. `GameLoopService` runs a fixed-rate physics tick on `RoomState`
and broadcasts an authoritative `WorldSnapshot` to every connected client. Clients render and
interpolate — they never tell the server where a body is.

Static world geometry (walls, labels) is delivered once on connect via `WorldGeometryMessage`,
not on every tick.

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

**Voting.** Any player can propose a level reset or level switch; the action fires when YES votes
reach a 2/3 majority of the connected roster. Vote times out after 15 s (450 ticks at 30 Hz).

## Seeded accounts

Seeded idempotently on first run. The seed bypasses the strict password policy; `SetAllPasswords`
keeps every account on the current operator-chosen password:

| Username       | Password | Colour    |
| -------------- | -------- | --------- |
| `gungreeneyes` | `800085` | `#D85A30` |
| `gideonkain`   | `800085` | `#378ADD` |

New accounts created via the API enforce the strict policy (≥ 8 chars, upper + lower + digit +
special).

## Build, test, run

```powershell
dotnet build Cursory.slnx
dotnet test  Cursory.slnx
dotnet run --project Cursory.Blazor
# https://localhost:7238  (or http://localhost:5238)
```

To see cooperative play locally, open a second browser window (or incognito) and sign in as the
other seeded user — both cursors appear in the same room.

`TreatWarningsAsErrors=true` is set in `Directory.Build.props`; missing XML doc comments are
downgraded to warnings (`CS1591`).

## Deploy

Targeted at Azure App Service (`cursory`). The workflow at `.github/workflows/azure-deploy.yml`
fires on push to `main`: restore → publish `Cursory.Blazor` → upload artifact → deploy to the
`cursory` slot at **https://cursory.azurewebsites.net**.

To turn the deploy on (one-time setup):

1. Provision an Azure App Service named `cursory` (.NET 10 runtime, Linux or Windows).
2. Download the publish profile from the App Service in the Azure portal.
3. Add the GitHub secret `AZURE_WEBAPP_PUBLISH_PROFILE` to `mindattic/Cursory`.
4. Push to `main`.
5. Flip `MindAttic.Deploy/projects.json → apps[].cursory.disabled` to `false`.

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
