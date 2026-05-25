# Cursory

**Cooperative cursor puzzles in a shared room.** Sign in, see every other player's
cursor in real time on a 10 000 × 10 000 world, and solve Portal-style puzzles
together — drag a heavy block into a goal zone, hold a door open for the player
who carries the key, hover on a tile so another player's path opens. No chat;
your only voice is a *whistle* every time you click.

## Architecture

| Project           | Role                                                                                |
| ----------------- | ----------------------------------------------------------------------------------- |
| `Cursory.Core`    | Domain models, `UserAccount`/`UserRepository` (JSON file store), `AuthService` (BCrypt + lockout + security-stamp), `RoomState` with sum-of-springs physics. |
| `Cursory.Shared`  | Razor components rendered by the host — currently just the gated `Home` page (the room). |
| `Cursory.Blazor`  | ASP.NET Core Blazor Server host. Cookie auth, antiforgery, rate-limited `/api/auth/login`, `RoomHub` (SignalR), `GameLoopService` (30 Hz physics tick + snapshot broadcast). |

Mirrors the StreetSamurai membership pattern: cookie auth, BCrypt, per-account
lockout, security-stamp revalidation, antiforgery on every form, per-IP rate
limit on login. Adapted to **username**-based accounts because the two seeded
players (`gungreeneyes`, `gideonkain`) are usernames, not emails.

## How the multiplayer works

The server owns the simulation. Clients send only their cursor position (and
grab/release/whistle events) at 30 Hz over SignalR. `GameLoopService` runs a
fixed-rate physics tick on `RoomState`, then broadcasts an authoritative
`WorldSnapshot` to every connected client at the same rate. Clients render and
interpolate — they never tell the server "the block is at (x, y)". At ~30
bytes/cursor/tick this comfortably carries ~100 cursors per node before
bandwidth becomes a concern.

**Cooperative drag (the first puzzle).** Each cursor attached to a block stores
a grab anchor in the block's local coordinate space. Per tick, the server sums
`k * (cursor_world − anchor_world)` over every attached cursor. If the magnitude
of the sum exceeds the block's static-friction threshold, the block accelerates
along the net vector. Two cursors pulling opposite directions cancel; two
pulling the same direction stack. A single cursor against a heavy block can't
break friction — you need a partner.

**Whistle.** Click empty space → server records a `Whistle` and ships it on the
next snapshot. Clients render a coloured ripple at the world point and play a
Web Audio tone keyed by colour, so every player has a recognisably distinct
note. There is deliberately no chat.

**Pan & camera.** The viewport is a single HTML5 canvas. Drag empty space to
pan over the 10 000 × 10 000 world; a minimap in the corner shows every cursor
and the current viewport rect. Custom canvas pan rather than a third-party
panzoom library — keeps the cursor/world coordinate math in one place.

## Seeded accounts

The host seeds two accounts on first run (`%APPDATA%\MindAttic\Cursory\users.json`).
Both passwords are stored bcrypt-hashed; the seed bypasses the password policy
because the operator chose them verbatim:

| Username       | Password         | Colour    |
| -------------- | ---------------- | --------- |
| `gungreeneyes` | `Happygirl1005`  | `#D85A30` |
| `gideonkain`   | `Happygirl1005`  | `#378ADD` |

New accounts created via the API have to satisfy the strict policy (≥8 chars,
upper + lower + digit + special).

## Run locally

```powershell
dotnet run --project Cursory.Blazor
# https://localhost:7238 (or http://localhost:5238)
```

Sign in as either seeded user. To see the multiplayer half, open the URL in
a second browser (or incognito window) and sign in as the other user — both
cursors will appear in the same room.

## Deploy

Targeted at Azure App Service. No infra is provisioned yet; once it is, the
deploy will plug into `MindAttic.Deploy/projects.json → apps[]` next to the
other Blazor entries (the workflow file lives in this repo at
`.github/workflows/azure-deploy.yml`, mirroring StreetSamurai).

## Roadmap

- [x] Cooperative-drag puzzle (one block, one goal zone)
- [x] Pannable 10 000 × 10 000 world with minimap
- [x] Whistle on click, per-colour tone
- [x] Switch tiles, gated doors, wall collision, maze puzzle
- [x] Compound rigid body with rotation — L-shape thread-the-needle level (Puzzle E)
- [x] Dotted "pull line" anchor → cursor (length scales with force)
- [x] Connection-status pill in the HUD; SignalR auto-reconnect
- [x] Stale-cursor eviction (ghost cleanup after silent disconnects)
- [x] Static geometry off the tick stream (walls + labels delivered once on connect)
- [x] Azure App Service workflow + `MindAttic.Deploy/projects.json` entry (idle until App Service exists)
- [ ] Multiple rooms / lobby
- [ ] Per-room state persistence (currently in-memory)
- [ ] Mobile (touch) input

## Deploy

`.github/workflows/azure-deploy.yml` mirrors StreetSamurai's two-stage build/publish/deploy.
On push to `main`, it restores + publishes `Cursory.Blazor`, uploads the artifact, and lands
it on the `cursory` App Service slot at **https://cursory.azurewebsites.net**.

To turn the deploy on (one-time):

1. **Provision** an Azure App Service named `cursory` (Windows or Linux, .NET 10 runtime).
2. **Download** the publish profile from the App Service → *Get publish profile* in the
   Azure portal.
3. **Add** the GitHub secret `AZURE_WEBAPP_PUBLISH_PROFILE` in `mindattic/Cursory` →
   *Settings* → *Secrets and variables* → *Actions*. Paste the entire XML.
4. **Push** to `main`. The workflow runs and deploys.
5. **Flip** `MindAttic.Deploy/projects.json → apps[].cursory.disabled` to `false` so the
   shared CLI surface knows the app is live.

Once steps 1–4 are done, every push to `main` re-deploys. Share the URL plus one of the
seeded credentials with a teammate and you can both log in to the same instance.
