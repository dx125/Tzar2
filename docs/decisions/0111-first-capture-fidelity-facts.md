# 0111. First capture session: commit its facts, adopt the measured shadow factor and player-colour order, flag the original's tick settings

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (capture follow-up)
* **Related:** ADR 0030 (capture harness), ADR 0110 (harness fixes), TDD §13.3, REQ-SIM-01, REQ-NET-02, UNK-01a, UNK-03,
  UNK-07, UNK-10, UNK-12, UNK-30, UNK-39; [fidelity-summary.md](../mechanics/fidelity-summary.md)

## Context

The operator returned session `capture-20261018-024524` (Windows 11 25H2, clean GOG v1.10 EN, harness 45a07e6 plus a
local patch, 16/16 trials, 112/112 usable images, game surface RGB555). The first `t2 fidelity analyze` run found
three analysis problems, not capture problems:

* the scenarios now pass `-map MP2.wmp`, and the analysis looked for `MP2.wmp.wmp`, so no snapshot was placed on its
  map (terrain-tile alignment and the shadow fit depended on it);
* the shadow fit was swamped by pixels exactly one level darker in every channel (large soft patches over the whole
  terrain, about ten times as many as shadowed pixels; not shadows), and the per-pair score could not separate
  neighbouring factors that agree on most levels;
* the player-colour analysis needed the camera on each kingdom's castle, which the session never achieved
  (`hmkingdom` did not switch kingdoms), while the diplomacy dialog it captured shows every other kingdom's colour.

## Decision

1. **Analysis fixes** (`Tzar2.Tools.Cli/Fidelity/Analysis`): map names may carry `.wmp`; the shadow fit counts the
   one-level pixels apart (`faint_pixels`) and accepts a rule that reproduces the observed per-level table at ≥ 90 %
   of the levels and ≥ 20 points better than the runner-up; the player-colour analysis also reads the kingdom swatches
   (setup screen, diplomacy dialog: tag `swatches`, default geometry for the 1024 × 768 diplomacy dialog); the
   terrain analysis reports the base-tile offset per atlas and the transition variant against the cell's parity.
2. **Facts committed** to `tests/fidelity/facts/*.yaml` (numbers only, REQ-LEGAL-01).
3. **Shadow (UNK-03): ½ per 5-bit channel.** Two independent fits, both won by `c >> 1` (`floor(c*16/32)`):
   * `shadow-blend.yaml` (scenario shadow-blend, MP3/MP6, 12 snapshots): 93 of 96 channel levels seen, all 93
     reproduced (`table_agreement` 1; runner-up `floor(c*17/32)` 0.7527); 10,458 pairs fit, 542 fit only a double
     application.
   * `terrain-tiles.yaml` (scenario terrain-tiles, MP1/MP2/MP4, 27 snapshots): 87 of 96 levels seen, 80 reproduced
     (`table_agreement` 0.9195; runner-up 0.6782); 18,990 pairs fit, 491 doubly. The 7 disagreements are all in the
     dark and middle levels: red 5 → 1, 17 → 7; green 13 → 7; blue 2 → 0, 12 → 5, 13 → 7, 18 → 8, each
     exactly one level off `c >> 1`. These levels are rare on the snow, grass and summer maps of that scenario (few votes
     each), so sprite pixels over terrain can outvote the shadow there.

   The status stays VERIFIED: the bright half of every channel and the whole shadow-blend table agree. What would
   close the residue: a capture with shadows over dark and middle terrain (desert, dirt, water edges; the deliberate
   overlap scenario of the next run can provide it), and a fit restricted to pixels inside the shadow sprites'
   footprints, so sprite pixels no longer vote. As far as this capture shows, overlapping shadows are not darkened
   twice (542 / 491 double-application pairs against 10,458 / 18,990). The client's 50 % mask pass stays (it
   differs from `c >> 1` by at most one 5-bit level); the comments now say "measured".
4. **Player colours (UNK-01a): identity.** The setup and diplomacy swatches of kingdoms 1–8 are each nearest to
   `GAME.PAL` ramp N for kingdom N, and kingdom 1's castle carries ramp 1. The engine already maps owner N to ramp N;
   nothing changes. The sprite ramps of kingdoms 2–8 are confirmed by the next session.
5. **Colour depth (UNK-39):** the game always draws to an RGB555 surface; the screen expansion belongs to the display
   path. We keep bit replication.
6. **Lobby default:** the population limit default of 200 is VERIFIED (setup screen).
7. **Not resolved, re-capture:** unit speeds (UNK-12) and attack timing (UNK-07/08). The spawned units were never
   placed, so they never moved or fought. Terrain variant choice (UNK-10) is still open: the variant is not in the map
   word alone and is not a coordinate parity.
8. **Cheats, switches, ini keys** from the operator's notes go to
   [original-switches-and-cheats.md](../mechanics/original-switches-and-cheats.md). This records names only, plus
   behaviour where it was observed.
9. **Tick settings.** The Ready2Play launcher writes `SPGAMETICK=50` and `MPGAMETICK=200`. If these are tick lengths in
   ms, single-player agrees with REQ-SIM-01. Multiplayer would then step every 200 ms, while REQ-NET-02 uses 50 ms
   ticks. This is flagged in the TDD as an open question. Until a multiplayer capture shows otherwise, we read 200 ms as
   the original's turn length, not its sim step.

## Consequences

* No ruleset value changes. The golden hashes stay as pinned (the YAML change is a comment).
* The next session (ADR 0110 scenarios) should settle unit speeds, attack timing, the sprite ramps of kingdoms 2–8 and
  the `hmspawn` / `hmkingdom` semantics. The re-capture list is in [fidelity-summary.md](../mechanics/fidelity-summary.md).
