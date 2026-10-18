# Fidelity summary (after the first capture session)

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
check that disagrees. Today: 14 checks, 4 measured, 4 passing (pass rate 100 %, coverage 29 %). Mapgen and ai have
no capturable checks yet.

## What still needs a capture run

The second session runs four batches (`run-capture.ps1 -Batch A|B|C|D`, ADR 0112):

| Batch | Time | Suite checks it can settle | Placeholder rows it addresses |
|---|---|---|---|
| A | ~21 min | player-colour sprites, shadow overlap, attack cycle, damage, production time, construction time | m2: time between hits, minimum damage / damage rule (pairs without bonus), construction rate (one builder); m3: cheat semantics of `hmspawn` / `hmkingdom` (smoke); UNK-01a, UNK-03 |
| B | ~23 min | speed per point (15 classes), load per trip | m2: SPEED → px/s, carry capacity, gather time (as trip time, walk included); m3: cow milk / milking time (cow trial) |
| C | ~16 min | speed per point (15 more classes) | m2: SPEED → px/s; m3: armored chariot and caravan speed |
| D | ~15 min | day length, night share | m3: day length, night hours |

Still without a scenario: XP gain, trade profit, technology magnitudes, repair, fire, capture, random maps (RE-10),
AI experiments (RE-07), and the "one level darker" terrain patches (UNK-32).

The operator should also send the `SPGAMETICK` / `MPGAMETICK` values of GOG's own `tzar.ini`, if it has them (REQ-SIM-01 note).
