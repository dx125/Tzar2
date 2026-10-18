# 0045. Append the M3 commands and stores; rally with default action or move-advance; re-pin the goldens

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream A)
* **Related:** TDD §8.6, §8.7, §9.3, §10 GAME-05, §21, §22.2, ADRs 0016, 0019, 0037, 0039, 0040-0044

## Context

M3 stream A adds research, upgrades, repair, capture, dragged walls and gates (GAME-03..06), rally variants (§9.3:
Ctrl plain, Alt with default action, Ctrl + Alt with move-advance) and a dozen pieces of state that snapshots must
carry and validate (ADR 0019).

## Decision

* **Commands** (wire values appended): `Research` 12, `Upgrade` 13, `Repair` 14, `Capture` 15, `BuildWall` 16,
  `SetGate` 17; `Build` Arg1 = rotation; `SetRally` Arg1 = `RallyMode` (0 move, 1 default action on Target, 2
  move-advance); `Delete` honours Shift for protected types. Rejections `AlreadyResearched` 12, `ShiftDeleteRequired` 13, `GateBusy` 14.
  Events `ResearchCompleted` 16, `UpgradeCompleted` 17, `Captured` 18, `Repaired` 19, `Decayed` 20 (fields),
  `FireStarted` 21, `BurnedDown` 22, `FireExtinguished` 23.
* **Rally:** a unit leaving with mode 1 harvests the rally target if it is a node it can gather, helps build it if it
  is an own site, attacks it if it is an enemy, else moves; mode 2 moves with move-advance.
* **State** (stores appended in this order): `game.upgrades`, `game.capture`, `game.gate`, `game.burning`,
  `game.neglect`, `game.mana`, `game.lifetime`, `game.repair`; `ResourceStock.RegenMs`, `Construction.CarryBasisPoints/Paid`,
  `Production.RallyTarget/RallyMode/Paid`, `GateState.Desired/LastChangeTick`, `Footprint.Rotated`; `PlayerState.Civilization/Technologies`; the map's bridge
  layer. Every value is range-checked on restore and each store requires the matching trait; the modifier tables are
  rebuilt. Snapshot format 5, hashed layout 4, ruleset hash version 4.
* **Systems:** step 10 `StructureSystem` (gates, fire, mana), step 12 `CaptureSystem`; step 17 removes expired
  lifetimes before recycling slots.
* **Building damage scale** (owner decision on this PR, UNK-08): `gameplay.resistanceScale` goes from 100 to 1000, so
  a hit on a building does `damage × 1000 / (1000 + RESISTANCE)` (castle ×0.5, barracks ×0.67, house ×0.95). With
  100, a castle (RESISTANCE 1000) took 1 damage per footman hit — two footmen needed about 80 minutes (found by the
  client's autoplay). Still UNVERIFIED until the damage grid capture. Capture difficulty does not use the scale: it
  uses RESISTANCE itself (after Fanaticism/Buddhism) plus the EMIT influence (ADR 0044). A scenario test has ten
  footmen destroy an enemy barracks in bounded time.
* **Goldens and benchmark.** The gameplay bot now also researches its castle's technologies one after another and
  (after the review) attacks enemy houses with soldiers (fire, ruins), captures houses, repairs damaged buildings, builds
  a turned gate, drags a wall and bridges a river beside its base, so the cross-OS golden and hash-stream CI cover
  those systems.
  Golden values re-pinned (reasons in the tests): gameplay ruleset `BF28E77D177AB6AB`, stream `C872CDC357B5AEEA`,
  final `2D2D3341E01243F0`; CI gameplay bench `760986D3F5B6E96C`; synthetic movement `CCD85F529731EEC1` /
  `618722F6DFB2231D` (layout only, behaviour unchanged).

Measured (Apple M5, macOS arm64, Release, .NET 10, shared machine with other agents running, ±30%):

| Scenario | Result | Budget |
|---|---|---|
| gameplay, 8 kingdoms (nightly gate), 4 / 1 workers | 4,146 entities at the end; p99 5.80 / 4.34 ms, max 20.5 / 7.8 ms | p99 ≤ 12 ms |

## Consequences

* The AI stream (B) and the client (D) emit and read these commands and events; the codec is unchanged (fixed fields).
