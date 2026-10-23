# 0201. Grow a random map's forests the way the original does

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** maintainer; the coordinator for the product owner (2026-10-22: adopt the original's forest algorithm)
* **Related:** WORLD-013, ADR 0060 (the random map generator), ADR 0190 (rules read from the original's code by ReTzar)

## Context

ADR 0060 built Tzar2's own random map generator. Its forests came from a value-noise field and a cover share by map
resources: 10 %, 15 % or 21 % of the eligible land, plus 0.6 % strays. The original reads
`TERRAIN.INI [randomtreeset]` instead, one row per map side with eight numbers, the same in all four terrain sets.
Their meaning was unknown (WORLD-013 was `partial`).

ReTzar reads the original's generator (0x4b2ab0 for the trees). It gives the numbers a meaning:

* **The field.** A 512 × 512 midpoint-displacement ("plasma") field of 0-255, sampled at the map's tiles. The third
  and fourth numbers are its roughness and divisor: a new point is the mean of its ends plus a random offset of up to
  ± extent × roughness / divisor.
* **The band.** The first two numbers bound the forest band (1-255).
* **The divisors.** Inside the band, a tree grows with about 1 / (100 / divisor): the sixth number below the eighth
  (the dense threshold) and the seventh above it. That gives about 1 % sparse and about 40 % (30 % on the two largest
  sides) thick.
* **Strays.** Outside the band, a stray needs a draw of 1 in 10,000 (the fifth number) first.
* **Weighting.** The chance is weighted by how many tree pictures may grow on the tile: a draw in 0..n·k must be at
  most n.
* **Where.** Only tiles whose ring of tiles two steps away is all land.

## Decision

1. `Forests` (Tzar2.Content.MapGen) holds the facts:
   * `[randomtreeset]` by map side, restated as numbers (REQ-LEGAL-01);
   * the plasma field;
   * the chance of a tree.

   The code and its random numbers are Tzar2's own (DEC-09: ReTzar is read, never copied). One fact of ReTzar's reading
   is kept as read: a field value below 0 ends at 255.
2. `MapBuilder.PlaceForests` uses it in place of the noise and cover share:
   * Kingdoms' starting forests, start areas and the validator stay as ADR 0060 has them.
   * The map resources level no longer changes the forests, as in the original.
3. `TerrainProfile.TreePictures` restates how many tree pictures grow on each set's tree terrains:
   * grass and summer: 29;
   * desert: 28 (its `[treename]` spells "Deciduous" otherwise, so that kind does not count);
   * winter: 17.

## Alternatives considered

* **Keep Tzar2's own density and only scale it to the data:** the eight numbers only make sense through the field and
  the chance they drive, so scaling would still be a guess.
* **Port ReTzar's generator whole:** against DEC-09 (GPL-3.0). It would also replace ADR 0060's terrain and start
  placement, which WORLD-013 does not ask for.

## Consequences

* **Different maps.** Every random map changes.
  * Forests are tighter: about a fifth of the land instead of about a third. They are thicker on Tiny and Real World,
    where the dense threshold is lower.
  * Trees per map, Continental, seeds 1-3, before → after: Medium 1531 → 1001, Large 3653 → 2820, Huge 6646 → 4075,
    Enormous 15214 → 10674, Real World 27239 → 24772, Tiny 391 → 405.
* **Hashes.** The map-generator golden hashes are re-pinned (`MapGenGoldenHashTests`). The sim, gameplay and AI bench
  hashes do not use random maps and are unchanged. `AiNavalTests` samples the landed soldiers every second instead of
  every ten: on the new Archipelagos map they still land, but they fall within seconds.
* **Exactness.** The forests have the original's shape and density, not its exact trees: the random numbers are not the
  original's.
* **Revisit** if the owner's play-tests find the maps too open, or if a capture measures the original's tree counts.
