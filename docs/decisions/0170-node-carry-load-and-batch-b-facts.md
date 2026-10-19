# 0170. A resource node may set the load carried from it (cows: 50); batch B's facts; unit speeds re-captured

* **Status:** Accepted
* **Date:** 2026-10-19
* **Deciders:** maintainer (Claude), product owner
* **Related:** ADR 0042 (economy, cows), ADR 0112 (fidelity suite), ADR 0169, TDD §22.3, UNK-12, UNK-17;
  [m2-placeholders.md](../mechanics/m2-placeholders.md), [m3-placeholders.md](../mechanics/m3-placeholders.md),
  [fidelity-summary.md](../mechanics/fidelity-summary.md)

## Context

Batch B of the second session, `capture-20261018-222649` (Windows 11 25H2, clean GOG v1.10 EN, harness `dfc2685`,
19/19 trials), measured gathering and unit speeds.

**Gathering** (stockpile sampled from memory; the step is the load, the time between steps one trip, walk included):

| Node | Resource | Deliveries | Load | Trip (median) |
|---|---|---|---|---|
| tree | wood | 8 | 10 every time | 6.9 s |
| rock pile A | stone | 5 | 10 every time | 9.8 s |
| rock pile B | gold | 12 | 10 every time | 5.4 s |
| cow | food | 5 | **50** four times, 10 once | 10.6 s |

The engine gave every harvester one capacity, 10, for every resource. Wood, stone and gold agree; a cow does not.
The cow's milking time placeholder (1.5 s per unit, ADR 0042) would make a 50-unit load take 75 s, while the
original's whole trip, walk included, took 10.6 s.

**Unit speeds** were unusable: 1 of 15 classes gave a clean track. The scenario selected the spawned unit with a box
dragged to (1015, 760), which is inside the game's edge-scroll zone, so the camera moved away before the measured walk
and the analysis followed wandering cows instead.

## Decision

1. **`ResourceNode.carry`** (`ResourceNodeDefinition.CarryLoad`, hashed into the ruleset): the units a harvester carries
   per trip from this node, replacing the harvester's capacity before technology effects; 0 keeps the harvester's.
   `GameplayCommands.CarryCapacity` takes the node the worker is assigned to; snapshot validation accepts the largest
   node load. Classic rules: cows `carry: 50` (VERIFIED). Wood, stone and gold keep the harvester's 10 (VERIFIED).
2. **Cows' milking time** becomes 200 ms per unit (UNVERIFIED): only bounded by the measured 10.6 s trip; the trip is
   separated into walk and gathering once unit speeds and `gather-wood-far` (batch E) are measured. The other gather
   times (1,000 ms per unit) are left as they are although batch B's trips suggest they are shorter: a placeholder is
   replaced by a measurement, not by an estimate (m2 register).
3. **The suite's carry check** compares each measured load with the engine's load from that kind of node
   (`EngineModel.PeasantLoadFrom`).
4. **Unit-speed scenarios** (`50-unit-speed`, `51-unit-speed-more`) select the spawned unit with a click on it; the
   scenario invariant test now rejects any click, move or drag closer than 16 px to the window edge. Batch B's speed
   facts are **not committed**; `unit-speed` is re-captured.
5. Batch B's gather facts are committed. Suite: **6 of 30 (20 %)**, 6 of 6 measured.

## Alternatives considered

* **Carry capacity per resource type on the harvester** (food 50): fields also give food, and nothing says a peasant
  brings 50 from a field; the cow is what was measured, so the node carries the rule.
* **Keep the cow at 10 and treat 50 as an analysis artefact:** four of five deliveries stepped the food counter by
  exactly 50 with one peasant working; nothing else produced food on that map.

## Consequences

* Milking is five times more productive per trip than before; with the placeholder milking time a full trip is about
  10 s, close to the original.
* Golden hashes: the gameplay ruleset hash changes (the new field); its hash stream and the bench hashes do not (no cows
  in those scenarios).
* Open: the field load, the milking time, the gather times (batch E), and the first delivery of 10 from the cow (the cow
  may have been short of milk, or the peasant carried something else).
