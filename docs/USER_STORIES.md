---
codex: 1
project: Cursory
code: CUR
layer: stories
status: living
updated: 2026-10-03
---

# Cursory — User Stories
> ✅ done (shipped & tested) · 🟡 partial · ⬜ planned. Every ✅ cites its verifying test.
> Test tokens are NUnit method names in `Cursory.Tests/`.

## Epic A — Membership & access
- **CUR-US-A1 ✅** As an operator, I can seed the two predetermined accounts so players can sign in
  without a signup flow. *Given the seed config, When the host boots, Then the accounts exist and
  authenticate, idempotently across restarts.* *(verified by `SeedUser_creates_account_on_first_call`,
  `SeedUser_is_idempotent_on_unchanged_config`.)*
- **CUR-US-A2 ✅** As an operator, I can rotate the seed password / normalise username case and have
  stored records migrate on the next boot. *(verified by `SeedUser_migrates_a_rotated_password`,
  `SeedUser_normalises_existing_username_case`, `SetAllPasswords_forces_every_account_and_is_idempotent`.)*
- **CUR-US-A3 ✅** As a player, I can sign in with my username case-insensitively, and a seeded
  weak password is accepted while a new account's must be strong. *(verified by
  `Authenticate_is_case_insensitive_on_username`, `Authenticate_succeeds_with_seeded_password_that_violates_policy`,
  `CreateUser_rejects_weak_password`, `CreateUser_accepts_strong_password`.)*
- **CUR-US-A4 ✅** As the system, I lock an account after repeated failures and never leak whether a
  username exists. *(verified by `Lockout_triggers_after_ten_failures_and_blocks_correct_password`,
  `Authenticate_returns_null_on_wrong_password`, `Authenticate_returns_null_on_unknown_user`.)*
- **CUR-US-A5 ✅** As the system, I reject open-redirect return URLs after login. *(verified by
  `IsLocalUrl_filters_open_redirect_attempts`.)*
- **CUR-US-A6 ✅** As the host, I refuse a blank users-file path up front instead of failing deep in
  the first save. *(verified by `Constructor_rejects_empty_path`.)*

## Epic B — Drag & cooperative physics
- **CUR-US-B1 ✅** As a player, I grab a body on its edge so where I grab decides its torque.
  *Given a click off-centre, When I grab, Then the anchor snaps to the nearest perimeter point (and
  clamps to the body if the click is outside).* *(verified by `Grab_anchors_at_nearest_edge`,
  `Grab_clamps_anchor_to_body`.)*
- **CUR-US-B2 ✅** As a player, I can drag a light block solo, but a heavy block won't budge for one
  cursor. *(verified by `Single_cursor_moves_light_block`, `Single_cursor_cannot_move_heavy_block`.)*
- **CUR-US-B3 ✅** As two players, we break a heavy block's friction by pulling together, and our
  pulls cancel when we pull opposite ways. *(verified by `Two_cursors_together_move_heavy_block`,
  `Opposing_cursors_cancel`.)*
- **CUR-US-B4 ✅** As two players on opposite corners, we form a couple that rotates the body to
  thread it through a gap. *(verified by `Offset_opposing_cursors_rotate_block`.)*
- **CUR-US-B5 ✅** As a player, my reported pull reads in mass-units against the body's printed Mass,
  capped at one grab's ceiling, and releasing stops driving the body. *(verified by
  `Grab_reports_pull_in_mass_units`, `Detach_removes_cursor_force`.)*
- **CUR-US-B6 ✅** As a player, my cursor is leashed within the max tether length of its anchor.
  *(verified by `Tethered_cursor_is_leashed_to_max_length`.)*
- **CUR-US-B7 ✅** As a player, I can grab and drag a compound shape on the edge of its nearest piece.
  *(verified by `Shape_grab_anchors_on_edge_and_light_shape_moves`.)*
- **CUR-US-B8 ✅** As a player, I can turn on the segmented tether and spin a body by orbiting/
  swinging behind it so the rope catches its corners. *(verified by
  `Segmented_tether_wrap_spins_the_body`, `Segmented_tether_catches_a_corner_behind_the_body`.)*
- **CUR-US-B9 ✅** As a player, I can toggle cursor-vs-wall collision: on, my cursor is nudged out of
  a wall; off, it passes through freely. *(verified by `Cursor_is_nudged_out_of_a_wall_when_collision_on`,
  `Cursor_passes_through_walls_when_collision_off`.)*

## Epic C — The room loop (levels, votes, robustness)
- **CUR-US-C1 ✅** As the room, I ship 14 engine-backed levels that each seed and step without
  throwing. *(verified by `LevelCount_is_fourteen`, `Every_level_seeds_and_steps`.)*
- **CUR-US-C2 ✅** As the team, every block level is solvable by two cooperating cursors, and the
  rotation/thread shape levels are at least provably movable toward the goal. *(verified by
  `Block_level_is_solvable_by_two_cursors`, `Shape_level_body_is_movable`.)*
- **CUR-US-C3 ✅** As a player, I can start a reset / level-switch vote that needs a 2/3 quorum;
  solo it resolves at once, a two-player room needs both, and it early-rejects when quorum is
  unreachable. *(verified by `Solo_room_reset_vote_passes_immediately`,
  `Two_voter_reset_needs_both_to_pass`, `Vote_rejected_when_quorum_unreachable`.)*
- **CUR-US-C4 ✅** As the room, a passed level-switch moves the current level and queues a geometry
  rebroadcast + level announcement; no-op and out-of-range targets are rejected. *(verified by
  `Level_switch_vote_moves_current_level`, `Level_vote_for_current_level_is_rejected`,
  `Out_of_range_level_vote_is_rejected`.)*
- **CUR-US-C5 ✅** As the system, static geometry rides its own channel, NaN input is dropped, and
  silent cursors are evicted. *(verified by `GeometryMessage_includes_default_level_label`,
  `NaN_input_is_dropped`, `EvictStaleCursors_drops_silent_cursors`.)*

## Epic D — Electronics (Level 14)
- **CUR-US-D1 ✅** As two players, we light the bulb by dragging wire ends onto terminals to form a
  closed series loop battery+ → resistor → bulb → battery−. *(verified by
  `Bulb_lights_on_complete_series_loop`.)*
- **CUR-US-D2 ✅** As the system, an open loop or a bypassed resistor keeps the bulb dark. *(verified
  by `Bulb_stays_dark_with_a_gap_in_the_loop`, `Bulb_stays_dark_when_resistor_is_bypassed`.)*

## Epic E — Realtime presence & feel (UI)
- **CUR-US-E1 🟡** As a player, I see every other player's cursor render and interpolate in real
  time, pan the world, and read the minimap. *Exercised by hand + the `GameLoopService`/`RoomHub`
  broadcast path; no automated browser assertion in `dotnet test`.*
- **CUR-US-E2 🟡** As a player, clicking empty space fires a whistle others see as a ripple and hear
  as a per-colour tone. *Server side (`RecordWhistle`, snapshot windowing) is covered indirectly;
  the client ripple + Web Audio tone are unverified by an automated gate.*
- **CUR-US-E3 🟡** As a player, the HUD shows a connection-status pill and SignalR auto-reconnects.
  *Hand-verified only; no automated browser gate.*

## Epic F — Deploy
- **CUR-US-F1 🟡** As an operator, every push to `main` deploys Cursory to an Azure App Service.
  *`.github/workflows/azure-deploy.yml` builds and publishes to the `cursory` App Service on every
  push (runs succeed, publish-profile secret set), but the App Service is stopped, so there is no
  running public instance. No automated test covers the deploy.*

## Priority backlog
Dependency-ordered toward a shippable shared instance:
1. 🟡 **CUR-US-F1** — start the stopped `cursory` App Service (unblocks a real shared URL).
2. ⬜ Per-room state persistence (so a restart doesn't wipe progress) — prerequisite for lobbies.
3. ⬜ Multiple rooms / a lobby (depends on persistence).
4. ⬜ Stand up an automated browser gate for Epic E so it can graduate from 🟡 → ✅.
5. 🟡 Adopt `MindAttic.Authentication` ([HOUSE-LAW-7]; deviation [CUR-LAW-9](BIBLE.md#CUR-LAW-9)).
6. ⬜ Mobile / touch input.
7. ⬜ Port switches + gated doors onto the engine (see [BIBLE §3](BIBLE.md#CUR-§3)).

## Tickets
> Permanent log of every change and fix — this repo's ticket system. Rows are append-only: never
> delete or reword one; only move its Status (open → closed / wont-do) and fill in Ref. Status is
> a word, not a glyph, so tickets don't count toward the story index. Keep this section last.

| ID | Date | Type | Status | Summary | Ref |
|---|---|---|---|---|---|
| CUR-T1 | 2026-10-12 | docs | closed | Start the permanent ticket log in USER_STORIES.md | — |
| CUR-T2 | 2026-10-14 | chore | open | NUnit tests for UserRepository (Cursory.Core/Services/UserRepository.cs): only the empty-path ctor guard is tested. Cover missing file loads as empty, Add/Update/Delete persist and round-trip through a fresh instance on the same temp path, Update of an unknown Id is a no-op, Delete of unknown Id leaves others, GetByUsername is case-insensitive, GetAll/GetById/GetByUsername return defensive clones (mutating the result does not change the cache), Count, Save creates a missing parent directory and leaves no stray .tmp files. It is the only user store, so a regression loses or corrupts accounts. | — |
| CUR-T3 | 2026-10-14 | chore | open | NUnit tests for AuthService password change and validators (Cursory.Core/Services/AuthService.cs): ChangePassword hashes the new password, rotates SecurityStamp, clears MustChangePassword, throws for unknown user or weak password; ChangePasswordWithVerification rejects empty or wrong current password. GetPasswordError per rule (empty, too short, too long, NUL char, missing upper/lower/digit/special), ValidateUsername (blank, too long, bad chars, 3-char minimum), ValidateDisplayName (blank, too long, NUL), CreateUser rejects invalid role and duplicate username (case-insensitive), trims username and sanitizes display name. SecurityStamp rotation is what logs out old sessions. | — |
| CUR-T4 | 2026-10-14 | chore | open | NUnit tests for AuthService sanitizers, lockout expiry and IsLocalUrl edge cases (AuthService.cs): SanitizeDisplayName strips tags, NUL and collapses whitespace, returns empty for blank; SanitizeForLog strips CR/LF/NUL and maps null to empty; a successful login clears the failed-attempt count; IsLockedOut is false below 10 failures and per-username. Extend IsLocalUrl cases: percent-encoded //, /%5Cevil, control chars, scheme-like colon in first segment (/javascript:x) vs colon after a slash (/a/b:c ok), colon or @ only in the query or fragment, # fragments. These guard login open-redirect and log injection. | — |
| CUR-T5 | 2026-10-14 | chore | open | NUnit tests for RoomState vote lifecycle (Cursory.Core/Services/RoomState.cs StartVote/CastVote/ResolveVoteIfReady/TimeoutVoteIfExpired): a second vote while one is active is rejected; an unregistered starter is rejected; latecomers who joined after the vote started are ignored; re-voting flips yes to no and vice versa; 2/3 quorum math for 3, 4 and 5 voters; a vote expires after RoomVote.TimeoutTicks Steps; CurrentVote is a defensive copy; a passed Reset vote sets ConsumeGeometryRebroadcast but ConsumeLevelAnnouncement stays 0, and both consume calls clear their flag. Votes are how the room changes levels, so wrong quorum or a stuck vote blocks play. | — |
| CUR-T6 | 2026-10-14 | chore | open | NUnit tests for RoomState goal evaluation in Step (RoomState.cs Step goals/shapeGoals loops) using AddBlock/AddGoal/AddShape/AddShapeGoal: a GoalZone is solved only when the target block centre is strictly inside, unsolved when the block leaves or the target id is missing; a ShapeGoal is solved only when every rotated piece centre is inside (origin inside but one piece outside is unsolved, rotation is applied), and a shape with no pieces is never solved. Solvability tests only check the happy path, so boundary and rotation bugs in win detection go unnoticed. | — |
| CUR-T7 | 2026-10-14 | chore | open | NUnit tests for RoomState wall grabs and wire handling (RoomState.cs TryAttachWall, TryAttachWireEnd, SnapWireEnd, DetachLocked): a wall grab anchors on the nearest wall edge, reports PullMass capped at SingleGrabMaxMass and keeps the anchor fixed; unknown wall/wire/block ids and NaN click coords return false; picking up a wire end unplugs it from its terminal; a released end snaps to the nearest terminal within WireSnapRadius and stays loose beyond it; the held end follows the cursor on Step; grabbing something new releases the previous grab. Level 14 and wall tethers depend on this beyond the three happy-path circuit tests. | — |
| CUR-T8 | 2026-10-14 | chore | open | NUnit tests for RoomState player lifecycle, cursor input and whistles (RoomState.cs): AddOrUpdatePlayer spawns deterministically per userId and an update refreshes name/color/connection without moving the cursor; SetCursorPosition returns false for unknown users and clamps to WorldGeometry bounds; cursor collision ejects the cursor from a rotated ShapeActor piece; RemovePlayer releases its grab so the body stops being driven; RecordWhistle ignores unknown users and NaN, clamps coords, caps the ring at WhistleRingCapacity and Step drops whistles older than WhistleBroadcastTicks; ResetPuzzles keeps cursors but clears all attachments; Snapshot returns copies that do not alias live state. | — |
| CUR-T9 | 2026-10-14 | chore | open | NUnit tests for the Cursory.Blazor server layer (Hubs/RoomHub.cs, Services/GameLoopService.cs), which no test project references yet: add a Cursory.Blazor reference and use fake HubCallerContext/IHubContext. RoomHub: OnConnectedAsync aborts without a UserId claim, defaults name Anon and color #7F77DD, sends Geometry to the caller; Move re-registers a cursor evicted while the tab was backgrounded; every method is a no-op without UserId; OnDisconnectedAsync removes the player. GameLoopService: skips broadcast when CursorCount is 0, sends Geometry and LevelLoaded before Snapshot after a level switch, and a throwing tick is logged without stopping the loop. | — |
| CUR-T10 | 2026-10-15 | chore | closed | Remove duplicate command copies (/quickload, /quicksave) now centralized at the user level (~/.claude/commands) | WF-T14 |
