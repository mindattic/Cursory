# Cursory — agent notes

Multiplayer cursor puzzles. Sign-in gates an HTML5-canvas room where every
logged-in player's cursor renders for every other player in real time, and
puzzles are solved by cursors cooperating — grabbing a block creates a
capped-force joint, so a heavy body needs two cursors pulling together and
offset pulls genuinely rotate it.

> **Physics: real rigid bodies via Aether.Physics2D (commit b12cec4).** The
> earlier hand-rolled "sum-of-springs" kinematics with switch tiles and gated
> doors are gone. Blocks, walls, and now compound **shapes** are all engine
> bodies (`bodyByBlock`/`bodyByWall`/`bodyByShape`). `LevelCount = 3` (two block
> levels + the first shape level); the rest of commit 541d39e's fourteen get
> re-ported as the feel is locked. Switches/doors are still deferred.

## Repo shape

| Project           | Purpose                                                                                                                                                                                   |
| ----------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Cursory.Core`    | Domain + services. `UserAccount`/`UserRepository` (JSON file at `%APPDATA%\MindAttic\Cursory\users.json`), `AuthService` (BCrypt + lockout, modelled on StreetSamurai). `RoomState` carries the authoritative simulation, backed by an Aether.Physics2D `World` (gravity-free, top-down): each block is a dynamic body, a `FrictionJoint` to a static ground gives dry friction, and grabs are capped-force `FixedMouseJoint`s. `Step()` drives the joints + steps the engine; `Snapshot()` builds a defensive copy for broadcast. All engine access is serialised under `worldLock`. |
| `Cursory.Shared`  | Razor components shared with the host. Currently just `Home.razor` — the gated game page that opens the SignalR connection and imports `js/room.js`.                                       |
| `Cursory.Blazor`  | ASP.NET Core host. `Program.cs` wires cookie auth, antiforgery, rate-limited `/api/auth/login`, forwarded headers (for Azure), the SignalR hub, and the seeded users. `Hubs/RoomHub.cs` is write-only — clients send `Move`/`Grab`/`Release`/`Whistle`, state arrives back over the snapshot channel. `Services/GameLoopService.cs` is a `BackgroundService` that ticks at 30 Hz and broadcasts `WorldSnapshot` to `Clients.All`. |
| `Cursory.Tests`   | NUnit. Covers `AuthService` (seed idempotency, `SetAllPasswords`, lockout, weak password rejection, `IsLocalUrl` open-redirect filter), `RoomState` (grab snaps to nearest corner, single cursor moves a light block but can't budge a heavy one, two cursors break friction, opposing pulls cancel, offset-opposing pulls rotate the body, stale-cursor eviction), and the vote/level machinery (2/3 quorum, early-reject, level switch). |

## Multiplayer architecture

The server owns the simulation. The client sends only its cursor position (and
grab/release/whistle events) over SignalR at 30 Hz; the server runs a fixed-rate
tick on `RoomState` and broadcasts a `WorldSnapshot` to every connected client at
the same rate. **Never trust a client position for an object's authoritative
state** — clients send "my cursor is at (x, y)", and the physics tick decides
what that means for the block. This is the canonical anti-cheat shape and it's
also forgiving of latency because the spring force is continuous, not impulse.

Bandwidth maths: a snapshot is roughly 30 bytes per cursor + ~40 bytes per
block/door/switch. At 100 cursors × 30 ticks/s × ~3 KB/snapshot ≈ 90 KB/s/client
egress on a single node — fine for the App Service tier we'd target.

## Conventions to keep

- **Cookie auth claims used elsewhere**: `UserId`, `Username`, `Color`,
  `SecurityStamp`. Adding a new claim? Add it to the login Claim[] in
  `Program.cs` *and* to the `Home.razor` and `RoomHub` consumers.
- **JSON wire format**: ASP.NET Core's default `JsonSerializerDefaults.Web`
  applies — every C# `PascalCase` property serializes to `camelCase`. `room.js`
  reads `c.attachedBlockId`, not `c.AttachedBlockId`.
- **World coordinates everywhere**: `room.js` does the canvas/world transform
  *once* in `clientToWorld`. Every payload to the server is world coords;
  rendering and pan/zoom live on the client.
- **Engine units**: the Aether `World` runs in metres at `PixelsPerMeter = 100`
  (a 10 000-px world = 100 m), converted at the boundary by `ToM`/`ToPx`; the
  camelCase wire format stays in world pixels. Tune feel via the "Feel knobs"
  consts in `RoomState`.
- **Mass is the one legible dial** (`ForcePerMass`): a body's move-threshold is
  `Mass × ForcePerMass` (its `FrictionJoint.MaxForce`) and a cursor's reported
  pull is `force ÷ ForcePerMass`, so a body moves exactly when the pulls on it
  sum past its Mass. One grab tops out at `SingleGrabMaxMass` (= `GrabMaxForce ÷
  ForcePerMass`); heavier-than-that needs cooperating cursors. Inertia is scaled
  separately (`InertiaKgPerMass`) so heft is independent of the threshold.
  `BlockState.StaticFriction` is now **unused** (reserved). Mass is printed on
  each block.
- **Grabs anchor on the edge**: `TryAttach`/`TryAttachShape`/`TryAttachWall`
  project the click to the nearest perimeter point (`ProjectToEdge`; shapes pick
  the nearest piece first). Walls are grabbable — static, so the grab only tenses
  the tether. The anchor is sent as `CursorState.AnchorWorld*` (world space,
  recomputed each tick) so the client needs no body-type maths; `AnchorLocal*`,
  block `Vx/Vy`, and the shape's legacy dynamics fields are `[JsonIgnore]`d.
- **Tether leash**: a grabbing cursor is held within `MaxPullPx` of its anchor
  (`LeashAndReport`, server-authoritative; room.js mirrors it). The leash end is
  exactly where pull saturates, so `CursorState.PullMass` (force ÷ ForcePerMass,
  shown at the tether end) reads directly against the body's Mass.
- **Solid cursor**: the pointer is a disc of radius `CursorRadius`. Each frame
  `SetCursorPosition` first **sweeps** the path from the previous position and
  stops at the first wall surface crossed (`SweepCursorAgainstWalls` — slab
  segment/AABB), so even a fast jump can't tunnel a thin wall; then it disc-ejects
  from walls and shape pieces (`ResolveOutOfWalls`/`ResolveOutOfShapes`, inflated
  by the radius). Server-authoritative; room.js (`clampCursor`/`sweepWalls`)
  mirrors it for feel. You never collide with the shape you're holding, and raw
  click coords (not the resolved cursor) drive grab picking, so you can still grab
  an edge.
- **No extra RPCs**: grab/release/whistle/vote are one-shot client→server events;
  the only 30 Hz call is `Move`. All physics, collision, and leash resolution are
  server-side and broadcast in the single `Snapshot`, so every client sees the
  same world.
- **Password policy is strict; seeded users bypass it.** `SeedUser` writes the
  BCrypt hash directly and is idempotent. `CreateUser` enforces ≥ 8 chars,
  upper + lower + digit + special. The two seeded accounts (`gungreeneyes`,
  `gideonkain`) have weak passwords the operator chose verbatim.

## Build / test / run

```powershell
dotnet build  Cursory.slnx
dotnet test   Cursory.slnx
dotnet run --project Cursory.Blazor
```

`TreatWarningsAsErrors=true` and `WarningsNotAsErrors=CS1591` from
`Directory.Build.props` — a missing XML comment is a warning, anything else is
an error.

## Deploy

`.github/workflows/azure-deploy.yml` is wired but disabled until the Azure
App Service named `cursory` exists and the `AZURE_WEBAPP_PUBLISH_PROFILE`
secret is set. Once that's done, flip `MindAttic.Deploy/projects.json →
apps[].cursory.disabled` to `false`.
