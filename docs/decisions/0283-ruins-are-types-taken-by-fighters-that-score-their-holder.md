# 0283. Ruins are types taken by fighters that score their holder

* **Status:** Accepted
* **Date:** 2026-11-08
* **Deciders:** maintainer (Claude), the product owner (asked for it in the editor series)
* **Related:** CAP-007, NAT-001, DIP-011, WORLD-003; ADR 0281, ADR 0275; ReTzar `src/gameplay.rs` (`capture_points`)

## Context

The original's maps hold ruins: statues, altars and wells, the editor's "special locations". ReTzar's reading of the
original (`capture_points`, 0x4b6760) describes how they work:

* Every second, a ruin held by a kingdom scores it its picture's `ruinpoints` (1 to 7; 0x443430 type 6).
* Fighters of one kingdom standing round it take it over, as fighters take a magic gateway.

The original has one class, `ruin`, whose `OBJECTS\RUIN.INI` draws nothing (`anim_none`); each object names its own
picture. ADR 0281 kept ruins as decorations: drawn and closing their cells, but nobody could take them or score from
them (CAP-007 missing).

## Decision

* **The importer** makes one entity type per `gr_` picture of the terrain sets, keyed `comm.<picture>` in lower case
  with underscores (`MapDecoration.RuinType`). Each type:
  * inherits `^Ruin` from `classic.yaml`: `ClaimOnTouch: { by: military }`, as the magic gateway, and `Indestructible`;
  * has a `Ruin { image, points }` trait;
  * has a `Building` footprint whose cells come from the picture's `.dat` grid (ADR 0281).
* **Scoring.** `VictorySystem` adds a held ruin's points to its holder's raw score every second. Each ruin keeps its own
  beat, counted from its entity index; the original counts from its coming into play.
* **The converter** turns a `ruin` object into the type of its picture, with its owner. A picture the ruleset lacks stays
  a decoration (ADR 0281).
* **The client** draws a ruin by its picture and names it by the picture's `detailedname`. The editor places ruins from
  the object palette's "other" section and leaves them out of the decoration palette.

## Alternatives considered

* **One ruin type with a per-object picture:** the closest to the original, but an entity's picture, footprint and
  points would become per-object state in snapshots and hashes. Twelve pictures make twelve types.
* **Keeping ruins as decorations with an owner array in the world:** triggers could not name them, and selection, fog
  and capture would each need their own path.

## Consequences

* A kingdom that holds the original campaigns' 18 ruins scores from them, as in the original. The selection panel, the
  fog, triggers and the claim rules treat ruins as the objects they are.
* The ruleset hash and the legacy data hash change; `legacy.referenceDataHash` is measured again after merging `main`.
