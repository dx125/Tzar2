# 0070. Run single-player matches in a Game-layer session with a sim thread and session-built game frames

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream D)
* **Related:** TDD §7.2–§7.4, §8.5, §13.6, §15, §16, §25 M3; REQ-ARCH-03; DEC-02; ADR 0009, 0016–0019, 0039

## Context

M3 stream D makes the first playable skirmish. TDD §7.3 puts the simulation on its own thread with a triple-buffered
render snapshot, and §7.4 sends every intent through a scheduler as commands. Other M3 streams work at the same time on
the sim's gameplay (A), the legacy AI (B) and the random map generator (C); this stream may only consume what is on
`main` and must keep its integration points thin.

The sim's `RenderSnapshot` (M2) carries position, type, owner, facing, health and a walking flag. The client also needs
the activity (attack swing, chopping, mining, building), loads, construction and production progress, stockpiles,
corpses, fog and placement legality. There is no visibility system and no victory rule in the sim yet.

## Decision

1. **`Tzar2.Game.Session.GameSession`** owns a match: `MatchSetup` (ruleset name, `IMapSource`, `PlayerSetup` per slot,
   seed, lobby settings) → `Simulation`. `Start()` runs ticks on a background thread at the classic tick length
   (`SimulationConfig.TickLengthMilliseconds`) times a game speed (25 … 300 %, the original's `+`/`-`), with pause and a
   bounded catch-up. `Step(n)` runs ticks synchronously for tests and headless runs.
2. **Local scheduler.** The main thread `Submit`s command batches; the sim thread appends them at the next tick stamped
   with the local slot (`CommandList.AppendStamped`, as the relay does in ADR 0009). Input delay is zero; the network
   scheduler (M6) replaces this class without changing callers.
3. **AI hook.** `IPlayerController.Think(World, slot, CommandList)` runs on the sim thread before each tick for every
   computer slot, reading the world and emitting commands like a client. `PlaceholderAi` (gather, houses, fields,
   barracks, training, a move-advance on the nearest enemy building) keeps the skirmish playable until the legacy AI
   (stream B) implements the interface. It is clearly marked as a placeholder.
4. **Game frames.** After each tick the session fills a `GameFrame` on the sim thread (`FrameBuilder`): the
   render-snapshot fields plus activity, swing time, load, construction/production progress, rally point, resource
   left, corpses, stockpiles and population, the local player's fog layer and a buildable-tile layer computed with the
   sim's new public `PlacementQueries` (only when the map's passability version changes). Frames are exchanged through a
   lock-free triple buffer (`FrameExchange`, the scheme of `RenderSnapshotExchange`). The client reads nothing else
   from the sim (REQ-ARCH-03); building frames from `World` on the sim thread is the Game layer's job (§7.2), so the
   sim's render snapshot does not have to grow while stream A changes the gameplay components.
5. **Events** of every tick are appended to a bounded list the main thread drains (`DrainEvents`), so no sound or
   message is lost when the triple buffer skips frames.
6. **Placeholders, each marked in code:** `FogOfWar` derives the local player's visibility from own entities' sight
   radii every 4 ticks (explored stays explored; no ghosts) until the sim has visibility; a kingdom without units and
   buildings is defeated and the local player wins when all others are, until the sim has victory conditions.
7. **`IMapSource`** abstracts where maps come from. `SkirmishTestMap` generates a deterministic grassland with lakes,
   forests, gold and stone and a start base per kingdom from ruleset keys only (tests, `--map-size <tiles>`).
   `RandomMapSource` wraps the random map generator of stream C (ADR 0060; type, terrain set, size, resources from the
   skirmish setup) and is the default; maps that carry terrain-set corners are drawn from them directly. The legacy map
   converter (M4) plugs in as a further source.
8. **Interpolation** (`FrameInterpolator`) blends the previous and the newest frame by the time since the newest
   arrived; jumps over two tiles snap.

## Alternatives considered

* **Extend `RenderSnapshot` in the sim now:** the right long-term home for some fields, but it would collide with
  stream A's gameplay changes; the frame builder is one class to move when the snapshot grows.
* **Let the client read `World` directly:** violates REQ-ARCH-03 and races the sim thread.
* **Run the sim on the main thread in `_Process`:** simpler, but couples frame rate and tick rate and is not §7.3.
* **No placeholder AI:** a skirmish without an opponent is not playable; the hook makes the replacement trivial.

## Consequences

* The legacy AI and sim visibility/victory replace `PlaceholderAi`, `FogOfWar` and the victory rule without touching
  the client.
* Frames copy the fog and buildable layers per tick (O(map tiles), skipped when unchanged for the buildable layer); on
  512² maps that is ~0.5 MB per tick, acceptable on desktop; revisit with dirty rectangles if profiling says so.
* `GameSession` is the place for save/load and replays later (it already owns the command stream).
