# Fidelity summary (after batch E)

## Batch E (session `capture-20261019-221300`)

11/11 trials ran. **A mixed group walks at its slowest member's speed**: a knight (72 px/s alone) and a pikeman (40)
selected together walked 40.2 and 39.9 px/s; the engine's `slowest` rule passes (suite **9 of 30 = 30 %**). Nothing
else gave a usable measurement: a unit handed to kingdom 2 with `hmkingdom` is taken over by that kingdom's AI, which
sends it to attack our base, so the duels and the attack on the mill never happened; Feudalism (key D) did not start;
Ctrl+C selected another of our three castles, so the gathered tree was not the far one; the capture analysis found no
colour change. By the owner's decision of 2026-10-19 these are not repeated (STATUS).

## Day length (session `capture-20261019-195053`, ADR 0173)

The clock globe is a 24-hour clock (24 frames in `DAYNIGHT.BMP`) and stepped every 93.7 s, from 10:00 at the start to
18:00 when the game ended after 12 minutes (the Hard AI most likely beat the passive kingdom). **A day lasts 37.5
minutes** (the engine had 12) and a match starts at 10:00 (the engine's placeholder, now verified). The view's
brightness was spoiled by enemy units and a house built in it, so the night's share is still open. Suite: **8 of 30 =
26.7 %**.

## Walking speeds (sessions `capture-20261019-163231`, `-190321`, ADR 0172)

Both runs at the new place by our base, 30/30 trials, every walk clean (r² ≥ 0.99). **The original ignores SPEED**:
every foot class walks 40 px/s (pikeman SPEED 2 as fast as peasant SPEED 4), knights and crusaders 72, trolls 47,
ghosts 66.6, cows and bears 25.7. The speed follows the walk sheet's stride (`move_len`), by a rule not yet known. The
engine now gives the 18 measured classes their own speeds and the speed check passes: **7 of 30 = 23.3 %**.

Not measured: Arabian and Asian classes (MP2's own kingdom is European and can spawn only its own civilisation),
catapults, ballistas and caravans (spawning failed), ships and flyers.

## Unit speeds (session `capture-20261019-020223`, ADR 0171)

Windows 11 25H2, clean GOG v1.10 EN, 15/15 trials, run on a Russian keyboard layout (the harness switched the game to
US itself). **SPEED 4 walks 40.0 px/s (1.25 tiles/s)**: peasant, archer, priest and mage, both directions, 39.9–40.1
px/s. The engine now uses 10 px/s per SPEED point (was 12); that speed is proportional to SPEED is not measured yet,
so the suite's speed check stays pending (it needs two SPEED values). Still **6 of 30 = 20 %**.

The eleven other classes, all military, walked off to attack Nature's wild cows two tiles below the path in MP2's
middle before they were selected (the engine does the same, ADR 0104). The speed scenarios now run on the open rows
51–53 next to our own base, and the analysis rejects fits that are too short or run against the ordered direction.

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
pending counting as not passing. Today: **6 of 30 = 20 %** (secondary: 6 of 6 measured pass). The 5 rendering
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

Batch J (2026-10-21, `-Batch J`) answers the owner's questions of that day: the hit points per repair blow on a
lumber mill, a barracks and a castle (`repair-*`, counter-rate), and how close another kingdom's spy must come to
unmask a disguise at three fillings of the disguise meter (`spy-unmask*`, analysed by hand). Run 1
(`capture-20261021-203846`, 6/6 trials, 77/77 images) measured the meter: one point per 1.25 s, 100 in the spy's own
look, shown after its name. The repairs and the unmasking failed for reasons now fixed (STATUS log). Run 2 (`capture-20261021-212542`)
damaged all three buildings but found their hit points in no 4-byte field, and the spies failed on MP6; run 3 is
ready.

Still without a scenario before ADR 0113: XP gain, trade profit, technology magnitudes, repair, fire, capture, random maps (RE-10),
AI experiments (RE-07), and the "one level darker" terrain patches (UNK-32).

~~The operator should also send the `SPGAMETICK` / `MPGAMETICK` values of GOG's own `tzar.ini`, if it has them
(REQ-SIM-01 note).~~ It has neither (batch A section above).
