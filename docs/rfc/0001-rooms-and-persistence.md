---
codex: 1
project: Cursory
code: CUR
layer: rfc
status: planned
updated: 2026-06-07
---

# RFC 0001 — Multiple rooms & per-room persistence

## Problem
Cursory hosts exactly one shared `RoomState`, in-memory, as a DI singleton
([CUR-§4](../BIBLE.md#CUR-§4)). Two consequences block a real shared deployment: everyone who signs
in lands in the *same* room (no privacy, no parallel sessions), and a host restart wipes all puzzle
progress. The frontier items "multiple rooms / lobby" and "per-room state persistence"
([CUR-§7](../BIBLE.md#CUR-§7)) both hang off this.

## Options compared
1. **Keep one room, add persistence only.** Snapshot `RoomState` to disk/blob on level change and
   on a timer; reload on boot. Smallest change; still single-room.
2. **Room registry + per-room loop.** A `RoomRegistry` keyed by room id; `GameLoopService` ticks
   each live room; `RoomHub` joins a SignalR group per room. Each room owns its Aether `World`.
   Persistence per room id. Largest change; unlocks lobbies.
3. **Externalise state to a store (Redis/SQL) and make the loop stateless.** Maximum scale-out;
   far more machinery than a two-player co-op puzzle needs today.

## Decision
Phase toward **option 2**, taking **option 1's persistence** as the first independently-shippable
step. Defer option 3 until there is a scale reason (multi-node).

## What NOT to do
- Do **not** make the client authoritative for room membership or state to "simplify" rooms — that
  breaks [CUR-LAW-1](../BIBLE.md#CUR-LAW-1).
- Do **not** put more than one room's bodies in a single Aether `World` (the `worldLock`
  serialisation and friction-ground assumptions are per-room).
- Do **not** broadcast across rooms — fan-out must be per SignalR group, or bandwidth (already the
  scaling concern) blows up.

## Phased plan (with risk)
1. **Persistence of the single room** *(low risk)* — serialise/restore `RoomState` (levels, body
   poses, votes); guard with a round-trip test. Unblocks "survives restart".
2. **Room registry + per-room tick** *(medium risk)* — introduce room ids, group joins, and a
   per-room loop; biggest correctness risk is the `worldLock`/loop lifecycle per room.
3. **Lobby UI** *(low risk)* — create/join, on top of phases 1–2.

## Graduates into
- [CUR-§4](../BIBLE.md#CUR-§4) (architecture: room registry), [CUR-§7](../BIBLE.md#CUR-§7) (frontier
  → verified), and backlog items 2–3 in [USER_STORIES.md](../USER_STORIES.md).
