# Cursory — agent notes

Multiplayer cursor puzzles. Sign-in gates an HTML5-canvas room where every
logged-in player's cursor renders for every other player in real time, and
puzzles are solved by cursors cooperating — grabbing a block creates a
capped-force joint, so a heavy body needs two cursors pulling together and
offset pulls genuinely rotate it.

> **Physics: real rigid bodies via Aether.Physics2D (commit b12cec4).** The
> earlier hand-rolled "sum-of-springs" kinematics with switch tiles, gated
> doors and per-axis wall collision are gone. The current build is a 2-level
> spike (`LevelCount = 2`) validating engine feel; levels 3–14 (preserved in
> commit 541d39e) get re-ported onto the engine before their mechanics return.

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
  camelCase wire format stays in world pixels. The co-op mechanic lives in one
  relationship: a body's `FrictionJoint.MaxForce` (`StaticFriction × FrictionForceScale`)
  vs a single grab's `GrabMaxForce` — friction above one grab's ceiling needs two
  cursors. Tune feel via the "Feel knobs" consts in `RoomState`.
- **Block velocity is server-internal**: `BlockState.Vx/Vy` are `[JsonIgnore]`d —
  the client renders from `X/Y/Angle` only, so they stay off the wire.
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
