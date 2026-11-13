# 0292. Campaign arrows, a prefab library, fair starts and maps built from one half

* **Status:** Accepted
* **Date:** 2026-11-13
* **Deciders:** maintainer (Claude), at the owner's request (the rest of the editor review's section 3, 2026-11-13)
* **Related:** TDD §18.2, §18.3; ADR 0133, ADR 0155, ADR 0233, ADR 0290, ADR 0291; STATUS item 26.9

## Context

The second batch of the editor review's section 3 proposals:

* **The campaign graph only showed transitions.** A map maker wrote the Player Win and Player Lose actions by hand to
  make one map lead to another.
* **Prefabs were files without a library.** A selection could be saved as a prefab and inserted, but nothing listed
  them, and the editor had no ready-made groups.
* **Multiplayer maps had no fairness check.** Nothing compared the starts: how far each walks to gold, stone and wood,
  and whether the starts can reach one another. A symmetric map had to be drawn twice by hand.

## Decision

* **Arrows in the campaign graph** (`CampaignTransitions`). An arrow dragged from one map to another asks whether the
  victory or the defeat leads there.
  * A victory arrow sends the map's own wins that end it without a next map to the target. A map with no win gets the
    goal wizard's "destroy the enemies" goal (ADR 0290) leading there.
  * A defeat arrow sends the map's losses alike. A map with no loss gets a rule that loses into the target when the
    player's kingdom is defeated.
  * The selected map's page lists where it leads. Each transition can be sent to another map, or cut (a victory or a
    defeat then ends the map).
  * Every change is ordinary actions and rules of the map, one step of that map's undo history.
* **A prefab library** (`PrefabLibrary`, the Prefabs dock).
  * Ready-made groups for a civilization: a village, a walled yard with a gate, a watch post, a war band, a mining camp.
  * They are laid out by the editor's own placement rules on a scratch map, from the ruleset's traits: housing, the
    starting worker, walls, gates, towers, soldiers that a building trains, gold. Type names are never used, and walls,
    gates and towers common to every civilization count as its own.
  * The player's prefabs are the clipboard's format in the user folder `prefabs`; the dock lists them, saves the
    selection as one and deletes them.
  * A prefab goes to the kingdom chosen in the dock (Nature's objects stay Nature's) and is placed with one click. An
    object that does not fit there is left out.
* **Fair starts** (`FairStarts`, Map → Check fair starts). It reports how far each start walks by land to the nearest
  gold, stone and wood, and which other starts it reaches. These are findings:
  * a walk half again as long as the nearest kingdom's, and at least five tiles longer;
  * a resource a start never reaches;
  * starts that cannot reach one another.

  Walking is the eight neighbours over tiles that are not all water, not cliffs and not under a structure. Gates and
  bridges let units through.
* **A map from one half** (`MapMirror`, three entries of the Map menu). The left half is mirrored onto the right, the top
  half onto the bottom, or the left half is turned half round onto the right.
  * The half's terrain (painted corners, heights, cliffs), decorations and objects are copied; what stood on the other
    half goes first.
  * Kingdoms 1 and 2, 3 and 4, 5 and 6, 7 and 8 change places in the copy, starting locations included.
  * It is one undoable step.

## Alternatives considered

* **Arrows that make a Load Map rule:** a Load Map needs a condition the arrow cannot know; victory and defeat are what
  maps end with.
* **Ready-made prefabs as data files by type name:** they would break for another ruleset or a fork's civilizations;
  traits do not.
* **Fairness by straight-line distance:** walls, water and cliffs make the walk what matters.

## Consequences

* A campaign's flow can be drawn in the graph; the rules it makes stay editable.
* A map maker starts from ready villages, yards and camps, and keeps their own groups in a library.
* A multiplayer map can be checked for like starts and built symmetric in one step.
* The building's pictures are not mirrored (the original draws them one way), so a mirrored map is symmetric in where
  things stand, not in how they look.
