# 0188. Units have the original's body; terrain keeps the footprint; units coming the other way squeeze past

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** agent (maintainer), from capture batch K; product owner (fourth play-test: units stand on one another)
* **Related:** ledger WORLD-015; UNK-11; ORIG-02; ADRs 0024 (arrival slots), 0025 (movement), 0146 (crowded landings);
  `tests/fidelity/facts/manual.yaml` (`unit-spacing`)

## Context

Every unit had a 7 px collision radius (cavalry 10, siege 12), a placeholder (UNK-11). Walking or fighting, our units
came within 8 px of one another. At rest the arrival lattice put them 16 px apart, while their sprites are about 25 px
wide, so they looked stacked. The owner saw units standing on one another.

Batch K (`capture-20261022-110601`, scenario `unit-spacing`) looked at the original:

* **At rest** footmen stand on tile centres, one to a tile, 31–32 px apart.
* **A compact group keeps its formation:** nine footmen spawned two tiles apart arrive as the same 3 × 3 block.
* **Head on**, two columns of nine pass with their feet 27–35 px apart, and no two share a place.

IMAGES.INI gives nearly every unit a 32 × 32 object box: footmen, knights, cavalry, peasants, mages, animals. Chariots
and catapults have 64 × 64.

## Decision

* **The body comes from the data.** The importer gives every land unit (OBJECTS `teren` neither 0 nor 2) a
  `Collision.radius` of 7/16 of its object box: 14 px for 32 × 32, 28 px for 64 × 64.
  * Two bodies keep 28 px apart, as the head-on crowd did.
  * The arrival lattice (2 r × 9/8, ADR 0024) puts resting units one tile, 32 px, apart.
  * Combat reach, interaction reach and the arrival slots measure from the body, as before from the radius.
  * Ships and flyers keep their templates.
* **The terrain keeps the footprint.** A new `Collision.terrain` (`EntityTypeDefinition.TerrainRadius`) is the radius
  a unit slides along walls, trees and buildings with: the old placeholders, 7 px (cavalry 10, siege 12). The
  original moves units tile to tile and never clips a wall. A 28 px body sliding on the terrain jammed every gap
  between trees, and the AI's waves never arrived.
* **Squeezing past.** Two moving units going opposite ways keep only their terrain footprints apart, in the avoidance
  and in the overlap push. Opposing columns pass on a two-tile bridge instead of locking (ORIG-02).

## Alternatives considered

* **A tile occupancy model like the original's** (one unit per tile, moves tile to tile). It would be the faithful
  model, but it means a new movement system. Today's continuous movement with the original's body gives the measured
  distances.
* **A body of half the box (16 px).** The lattice would then space resting units 48 px apart, not 32.

## Consequences

* **The AI's economy is about 8 % slower** (crowds at nodes and drop-offs). Its first waves come later: within
  10 minutes on the tiny land maps (8 before) and about 25 minutes on Archipelagos (20 before). The AI tests'
  bounds follow.
* **Allies of different kingdoms still do not yield to each other in a crowd** (the overlap push treats only the same
  kingdom as friends). That is separate from the body.
* **Not measured yet:** a crowd around one target. Batch K's knight never appeared, because it was spawned where
  footmen stood.
* **The fifth play-test** (work queue) shows how the bodies feel.
