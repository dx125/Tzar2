# Fidelity summary (after batch B of the second session)

## Batch B (session `capture-20261018-222649`, ADR 0170)

Windows 11 25H2, clean GOG v1.10 EN, 19/19 trials. The suite passes **6 of 30 = 20 %** (6 of 6 measured): carry
capacity is new. A peasant carries 10 wood, stone or gold per trip and **50 food from a cow**; the engine now lets a
node set its own load. The trips (walk included: wood 6.9 s, stone 9.8 s, gold 5.4 s, cow 10.6 s) bound the gather
times but do not separate them from the walk; that needs unit speeds and `gather-wood-far` (batch E).

Unit speeds failed: the scenario's selection box reached the window's edge-scroll zone and the camera moved away from
the unit. The scenarios are fixed (select by a click; a test rejects mouse positions near the edge) and `unit-speed`
is to be re-captured; batch B's speed facts are not committed.

## Batch A (session `capture-20261018-155421`, ADR 0169)

Windows 11 25H2, clean GOG v1.10 EN, 12/12 trials, 82/82 usable images. The suite now passes **5 of 30 = 16.7 %**
(secondary: 5 of 5 measured): attack cycle, damage (two pairs), population limit, production time and construction
time. The last two needed an engine change: the TIME column is **1.25 s per unit** (peasant TIME 8: 9.99 s; lumber
mill TIME 30: 36.85 s for one builder, read from the selection panel's health bar). Rendering: player colours of all
eight kingdoms on the UI ramp (9 of 9 swatches); shadow overlap and the sprite ramps are still inconclusive.

What it took on the operator's PC, beyond the harness fixes of ADR 0110: `hmnopop` before every `hmspawn` (spawning
obeys the house limit), the DirectDraw device count as `DirectDrawEnumerateA` + 1, and typing on the US layout
whatever the operator's layout is (capture harness PR #60). `hmkingdom N` changes the owner of the selected unit; new
spawns stay with kingdom 1. GOG's own `tzar.ini` has no `SPGAMETICK` / `MPGAMETICK` (the GOG file is byte-identical to
the reference); `Tzar.exe` reads both keys, defaults unknown.

## First session

Session `capture-20261018-024524`: Windows 11 25H2, clean GOG v1.10 EN, 16/16 trials, 112/112 usable images.
Facts are in `tests/fidelity/facts/` and decisions in ADR 0111. A placeholder counts as VERIFIED only when a
measurement replaced it.

## Placeholder registers

| Area | Open placeholders | VERIFIED now | % | Targeted by this session but not resolved |
|---|---|---|---|---|
| M2 gameplay ([m2-placeholders.md](m2-placeholders.md)) | 40 | 1 (population limit default 200) | 2.5 % | SPEED → px/s, group speed (UNK-12), time between hits, hit frame, minimum damage (UNK-07/08): the units never moved or fought |
| M3 gameplay ([m3-placeholders.md](m3-placeholders.md)) | 87 | 0 | 0 % | none in scope; cheat names now known (UNK-30), amounts and effects not measured; day length not captured |
| Random maps ([mapgen-placeholders.md](mapgen-placeholders.md)) | 32 | 0 | 0 % | none in scope (needs random maps generated in the original) |
| Legacy AI ([ai-placeholders.md](ai-placeholders.md)) | 42 | 0 | 0 % | none in scope (needs script experiments) |

## Rendering unknowns (formats register)

| Item | Status | % |
|---|---|---|
| UNK-01a slot → ramp, UI colours of kingdoms 1–8 | VERIFIED: identity | 50 % of UNK-01a |
| UNK-01a slot → ramp, sprite ramps | kingdom 1 VERIFIED; kingdoms 2–8 need the next session | |
| UNK-03 shadow factor | VERIFIED: ½ per 5-bit channel (`c >> 1`); shadow-blend fit 93/93 levels, terrain-tiles fit 80/87 (7 dark/middle levels each one level off, few votes; ADR 0111) | 50 % of UNK-03 |
| UNK-03 overlap | consistent with "no double darkening" (542 vs 10,458 pairs); needs a deliberate overlap capture | |
| UNK-39 RGB555 → screen | resolved for the rebuild: RGB555 surface, replication kept | 100 % |
| UNK-10 terrain variant choice | facts collected (34 of 39 snapshots aligned); rule still open | 0 % |

Overall, 3 of the 6 rendering items above are verified (50 %). For the four placeholder registers it is 1 of 201
(0.5 %): this session targeted rendering, and its gameplay trials failed.

## The fidelity suite (ADR 0112)

`t2 fidelity report` compares every committed fact with the engine; `Tzar2.Fidelity.Tests` fails on a measured
check that disagrees. The catalogue is the whole TDD §22.3 scope (30 checks); the **M3 gate is passing / all 30**,
pending counting as not passing. Today: **1 of 30 = 3.3 %** (secondary: 1 of 1 measured passes). The 5 rendering
checks are §22.4 / S5 and reported apart: 3 of 5 pass. Batches A–D can measure at most 9 of the 30 (gate ≤ 30 %);
the other 21 need scenarios that do not exist yet (combat outcomes over 100 trials, research, XP, capture, trade,
loans, spells and the rest, each listed in the report with what would measure it).

## What still needs a capture run

The second session runs four batches (`run-capture.ps1 -Batch A|B|C|D`, ADR 0112):

| Batch | Time | Suite checks it can settle | Placeholder rows it addresses |
|---|---|---|---|
| A | ~21 min | player-colour sprites, shadow overlap, attack cycle, damage, production time, construction time | m2: time between hits, minimum damage / damage rule (pairs without bonus), construction rate (one builder); m3: cheat semantics of `hmspawn` / `hmkingdom` (smoke); UNK-01a, UNK-03 |
| B | ~23 min | speed per point (15 classes), load per trip | m2: SPEED → px/s, carry capacity, gather time (as trip time, walk included); m3: cow milk / milking time (cow trial) |
| C | ~16 min | speed per point (15 more classes) | m2: SPEED → px/s; m3: armored chariot and caravan speed |
| D | ~15 min | day length, night share | m3: day length, night hours |

Batches E–I and the editor session (ADR 0113) cover the remaining §22.3 checks except AI script semantics, which
stays pending by owner decision. Expected gate if every trial works: E 50 %, F 70 %, G 83 %, H 87 %, editor session
90 %; trade and gambling need one follow-up after I's discovery run.

Still without a scenario before ADR 0113: XP gain, trade profit, technology magnitudes, repair, fire, capture, random maps (RE-10),
AI experiments (RE-07), and the "one level darker" terrain patches (UNK-32).

~~The operator should also send the `SPGAMETICK` / `MPGAMETICK` values of GOG's own `tzar.ini`, if it has them
(REQ-SIM-01 note).~~ It has neither (batch A section above).
