# Project status

The living status of the rebuild: where each milestone stands against its exit criteria, what is being worked on,
what comes next, and a short log of decisions and events. Read it before starting work; **update it in the same PR
whenever a milestone criterion, the work queue or the decision log changes.** How to build, test and contribute:
[DEVELOPMENT.md](DEVELOPMENT.md). The specification: [TDD.md](TDD.md). Why things are the way they are: the ADRs,
indexed in [decisions/README.md](decisions/README.md).

* **Last updated:** 2026-10-21 (fourth play-test)
* **Maintainer:** Claude (AI), working with the product owner, who also operates the Windows PC with the original
  game for captures and local-only tests. The implementation agent that built M0–M6 is no longer available (handover
  2026-10-18; see the log below).
* **`main`:** green on CI. Open PRs: none. Open issues: [#62](https://github.com/dx125/Tzar2/issues/62) (flaky
  `RelayTests`).

## Milestones

Exit criteria from [TDD §25](TDD.md#25-milestones-and-exit-criteria). "Reported" means the previous agent stated it
and it has not been re-run since the handover.

| Milestone | State | Exit criteria and evidence |
|---|---|---|
| M0 Foundations | **Done** | One ADR per spike: S2 (0007), S4 (0008), S3 (0009), S1 (0010), S6 (0013; S5 mobile export is PX); S1 renderer met on Windows (ADR 0010); CI green; no original files tracked (CI job `legal`). |
| M1 Importer & viewer | **Done** (reported) | All 961 RLE sheets and the other formats of the reference build import (ADR 0013, `t2 import`); golden sprite comparisons and the import of the reference build are local-only tests (`TZAR2_ORIGIN`, `TZAR2_GOLDENS`). |
| M2 Sim core & movement | **Done** | Determinism matrix green on CI; ORIG-01/02/03 tests; B3/B4 within budget (ADR 0026). |
| M3 Classic gameplay & skirmish | **In progress** | Skirmish vs. Hard AI on all six map types (PR #36). Hotkey parity checklist complete by its own rules ([hotkey-parity.md](mechanics/hotkey-parity.md): 12 bindings blocked on features). The fidelity gate moved to M8 (ADR 0174). Three play-tests by the owner are done and their findings fixed (2026-10-20/21). Open before M3 closes: the ledger of the original's rules ([rules/](rules/README.md)) and its missing rules (work queue 1), and a fourth play-test. |
| M4 Triggers & campaigns | **In progress** | Converter: all 33 original maps and both campaigns convert (reported). Original campaigns completable: main campaign **14 of 19 measured** (2026-10-20, editor acceptance test on the clean GOG install, scripted solver, ADR 0134; not completed: `alex1`, `bigisland`, `v_02`, `vsaa`, `rajba`), tutorial 7 of 7 (reported). ≥ 90 % of the community corpus converts: **no corpus yet**. Open format unknowns UNK-70…80 need the TzarEdit diff session. |
| M5 Editor | **Code complete**, acceptance open | Parity checklist 100 of 104 done, 4 blocked ([editor-parity.md](mechanics/editor-parity.md)); §18.6 acceptance: `acc-original` verified locally 2026-10-18 (reported), `acc-fx` still needs a local run with the FX campaigns and a community campaign (`TZAR2_FX_CAMPAIGNS`, `TZAR2_CORPUS`), ADR 0156. |
| M6 Multiplayer & replays | **In progress** | Relay/lobby, lockstep sessions, drop policy, reconnect, desync reports, replays, MP saves and the MP UI merged (ADRs 0160–0168). 500-match soak with 0 desyncs: 168 matches, 0 desyncs (reported); nightly soak job runs. |
| M7 Windows beta | Not started | Installer, signing, crash reporting, min-spec tuning, closed beta. |
| M8 Modding, polish, release | Not started | Also the **fidelity suite ≥ 80 %** (moved from M3, ADR 0174): **9 of 30 (30 %)** today, see below. |
| PX Platform expansion | Post-1.0 (DEC-14) | |

## Fidelity gate (M8 since ADR 0174)

The suite (`t2 fidelity report`, ADR 0112) has 30 checks from TDD §22.3; the gate is passing / all, pending counts as
not passing, so 24 must pass. Since ADR 0174 it is an M8 criterion, and no more unit-by-unit measurements are made. AI script semantics stays pending by owner decision, so 24 of the other 29 must pass.
Each capture batch is a ~15–25 min unattended run on the operator's PC (`tools/capture/run-capture.ps1 -Batch X`).

| Batch | Measures | State |
|---|---|---|
| A | cheats, player colours, combat pairs, shadow overlap, training and construction time | **Done**: analysed and merged (PR #61, ADR 0169). Passing: attack cycle, damage, population limit, training time, construction time. |
| B | 15 unit speeds; gathering wood, stone, cows | **Gathering done** (ADR 0170): carry capacity passes (10 wood/stone/gold, 50 from a cow). **Unit speeds done** (ADRs 0171, 0172): after two failed places, 11 classes measured by our base; the original ignores SPEED (foot 40 px/s, mounted 72). Horse archer, horse samurai, ninja and monk did not spawn (not European). |
| C | 15 more unit speeds (chariot, caravan) | **Done** (ADR 0172): troll 47, ghost 67, cow and bear 26, dwarf, ork, skeleton 40; speed check passes (18 classes). Heavy cavalry, jihad warrior, janissary, chariot (not European), catapults, ballista and caravan did not spawn. |
| D | day length, night share (no input) | **Day length done** (ADR 0173): 37.5 min, read from the clock globe; a match starts at 10:00. The game ended after 12 min (the Hard AI), before the night: **night share open**, needs a 35-min run the AI does not end. |
| E | group speed, research, damage bonuses, capture, far gathering | **Done, one result** (2026-10-19): a mixed group walks at its slowest member's speed (passes). The rest failed: units given to kingdom 2 are taken over by its AI and leave the duel; Feudalism never started; Ctrl+C picked another castle, so the far tree was not the one gathered; the capture analysis saw no colour change. Not repeated: owner decision below. |
| F–I | fire, repair, healing, fields, experience; lightning, mana, loans, score; 12 × 8 duels; trade and gambling | deferred (owner decision below) |

Facts live in `tests/fidelity/facts/` (numbers only; captures stay on the operator's PC). A measured fact that disagrees
with the engine fails `Tzar2.Fidelity.Tests`, so a fact and the engine fix that makes it pass go in the same PR.

## Work queue

In order. **Maintainer** = code and analysis; **Operator** = runs on the Windows PC with the original game.

Items 1 to 3 are the owner's order of 2026-10-21 (log below), done one after the other; item 4 comes from the fourth
play-test.

1. **Maintainer:** the ledger's first fill and the data account are done (log below). Next: bring the hotkey
   checklist up to date (several rows still say "blocked" on features that exist), and keep the ledger current with
   every change.
2. **Maintainer:** the rest of the command card, on the card model of ADR 0182 (`Tzar2.Game.Ui.CommandCard`, done).
   The Magic Workshop's crafting, the holders' list of units inside, the kingdom's card with its lists and AI
   assistance are done (log below). Left: the spies' impersonate, uncloak and reveal-self (MAG-012, MAG-013), which
   need the sim first. `attack_type` is on no card of the original, so it is left
   out.
3. **Maintainer:** close the ledger's missing and partial rules (`t2 rules list missing partial`), in the owner's
   order. The ones a source settles come first: waypoints (ORD-005), move-sneak (ORD-004), Landing Tactics +50 %
   (TECH-019), one farmer per field (ECO-021), siege weapons hurting their own side (WAR-013), the attack's hit frame
   from the data (WAR-014). Then the screens: diplomacy, tribute, inventory, save and load.
4. **Maintainer:** units never stand on one another (WORLD-015).
   * What the probes of 2026-10-21 measured:
     * At rest, units keep 16 px apart.
     * While walking or fighting, units come as close as 8 px, with a collision diameter of 14 px and sprites about
       25 px wide.
     * The push apart is soft and capped per tick.
   * What did not work: treating friends walking the same way as hard obstacles jams opposing columns on a narrow
     bridge (ORIG-02). A radius of 12 px alone changes nothing in a crowd and shifts the AI's goldens.
   * The data: the original's units have a 25 × 25 selection box and a 32 × 32 object box, a whole tile.
   * Plan:
     * a body size from the data;
     * hard contact with rules for who gives way: idle units step aside, units coming the other way squeeze past,
       fighting units hold;
     * free places around an attack target instead of a crowd;
     * ORIG-02's jams at bridges and gates must not come back.
5. **Operator:** a fifth play-test (`tools/play/play.ps1`) once item 4 is merged.
6. **Maintainer (deferred to M8):** a night-share scenario (UNK-32, ADR 0173): 35 minutes or more of a night in a
   view no unit walks through, in a match the Hard AI does not end. Then **Operator:** run it.
7. **Maintainer:** walking speed for every class from its walk sheet's stride (`move_len`; ADRs 0172, 0174) instead of
   more speed scenarios; why catapults, ballistas and caravans did not spawn can wait for M8.
8. **Maintainer (deferred to M8):** before batches F and H, move or guard the scenarios that leave soldiers idle in
   MP2's middle next to Nature's cows (experience, combat outcomes; ADR 0171). **Operator:** batches F–I.
9. **Operator:** TzarEdit diff session (maps `base`, `a1`–`e1` for UNK-70…80); 20 random maps from TzarEdit (RE-10);
   the diagonal-rock passability check on campaign map `rajba` (blocks a campaign mission).
10. **Maintainer:** decode UNK-70…80, compare random maps with our generator; make the five main-campaign missions the
    solver does not complete pass (`alex1`, `bigisland`, `v_02`, `vsaa`, `rajba`; all time out, `rajba` on the
    diagonal rocks) to reach 19/19.
11. **Maintainer:** M5 `acc-fx` and the M4 corpus criterion: run locally with the FX Gold campaigns kept in `Origin/`
    (Russian translations, never committed) and find a community map corpus.
12. **Maintainer:** issue #62 (flaky `RelayTests`); M6 soak to 500 matches.
13. M7 planning: installer, code signing, crash reporting, min-spec hardware.
14. **Maintainer (deferred to M8, owner decision 2026-10-21):** the original's side panels (LAYOUT.INI `LAYOUT1024L`
    and `R`: the HUD down the left or right side) as a setting; the card, the tooltips and the selection view already
    place themselves by the layout.

## Decision and event log

Newest first. Technical decisions have ADRs (index: [decisions/README.md](decisions/README.md)); this log also keeps
the process decisions that have no ADR.

* **2026-10-21 — The kingdom's card and AI assistance (ADR 0183; work queue item 2).**
  * With nothing selected, the card is now the kingdom's (MENU.INI `ownership`): next idle peasant, quests, the lists
    and, where the match offers it, AI assistance (`ownership_ai`). Before, it was empty: the ledger said the next-idle
    and quest buttons worked, but only their keys and hotkeys did.
  * AI assistance: the human's standby AI runs the economy, the war or both when asked (`SetAiAssist`, two player
    flags), using the AI's existing economy and war halves. The button in force is lit.
  * It is the lobby's "AI Assist" option, as in the original, and is always on in single-player skirmishes.
  * What each half does in the original is unverified; a capture with assistance on would settle it.
  * The kingdom's lists, which the original fills at run time: its heroes, its spies (and ninjas: the units that see
    the invisible) and the items its units carry. An entry selects the unit (an item: its carrier) and shows it.
  * The selection view also shows LEVEL for the player's own units that gain experience (HUD-016).

* **2026-10-21 — A holder's units inside: the list, Emerge one, and Unload at a place (work queue item 2).**
  * A selected castle, outpost, ship or carpet of the player shows INSIDE (how many units of how many) and the
    portraits of the units inside, with their health, in the info area. A thin bar under its own health shows their
    average health (HUD-021, from the guide).
  * A click on a portrait picks that unit, and a second click lets it go. Emerge then waits for a click on the place,
    and only that unit comes out there (ORD-015, manual). The simulation's Unload takes the chosen unit as its target.
  * Without a pick, Emerge lets everyone out next to the building at once, as ORDERS.INI gives emerge no place.
  * A ship's or carpet's Unload now waits for a click on the drop-off place, as the manual says (ORD-014). It used to
    unload at once at the nearest shore.
  * Emerge and Unload are greyed out when nobody is inside.
  * Kept out: picking one unit out of a ship, which docks first. That would change the docking component and the
    golden hashes.
  * The manual says a right click gives the place; ours is a left click, as for every other order that waits for a
    place.

* **2026-10-21 — The magic workshop makes items from the command card (work queue item 2).**
  * Its Build Item order opens the original's D_BUILD panel: the kind of item (bow, book, shield, sword, potion), the
    gold to spend (more gold, a stronger item), BUILD and CANCEL. While an item is being made the order is greyed out
    and the workshop shows its progress.
  * Patch 1.05: spell books cost four times more gold per point of power and take twice as long. Two new combat
    settings, `craftBookCostPercent` and `craftBookTimePercent`, carry it; the ruleset hash is re-pinned.
  * Which picture shows which kind of item, and the power shown in the dialog, are ours (⚠ UNVERIFIED).
  * Ledger: ITEM-008 and ITEM-009 are tested.

* **2026-10-21 — Every technology bonus checked (the owner's request after the fourth play-test).**
  * **The tests:**
    * `TechnologyEffectTests` runs every technology of the classic ruleset that has effects. It checks that each
      effect changes its targets, only for the kingdom that researched it.
    * A row per technology checks that it reaches the right units or buildings: swords and not bows for Temper Iron,
      the trees for the Saw, the markets for trade bonuses...
    * Tests of the mechanics that had none: Medicine's range, the profit bonus of Trade Taxes and Banking,
      Concentration's mana, and Fanaticism making capture slower.
    * A local test on the original's data (`OriginTechnologyTests`) checks that every one of its 87 technologies does
      something: an effect, units or buildings it allows, an order it gates, or a mechanic of the simulation. It also
      checks that every effect reaches something.
  * **Found and fixed:**
    * Banking targeted the castle, but the profit bonus applies to markets, so it did nothing. It now raises the
      profit at markets (the game's own text: "increases trade profit").
    * Gunpowder had no effect. It now raises the cannon towers' attack (a placeholder; TECH-013).
    * The ruleset hash is re-pinned.
  * **Left as they are:**
    * Tournaments does nothing yet (TECH-029). Jewelry gates the market's item orders, which are missing (TECH-030).
    * Piracy and Boarding Ram are named by the manual and the texts but are not in the original's data (TECH-031,
      unclear).
    * Fanaticism's +20 % of a house's RESISTANCE 40 barely changes the damage it takes (under 1 % with the placeholder
      scale, UNK-08); it does make capture slower.
    * The gathering technologies speed up gathering; the help says they raise the load carried (TECH-006, TECH-010,
      unclear).
* **2026-10-21 — Fourth play-test by the owner: six findings and a check.**
  * **Wood went to the mill of the first trip** (ECO-013). A worker kept its drop-off from trip to trip, so after a
    nearer lumber mill went up it still walked to the old one. Every load now goes to the drop-off nearest to where it
    was gathered. The gameplay and AI goldens and the two benches are re-pinned.
  * **A bridge hid the units on it** (WORLD-014). Bridges are now drawn on the ground layer, like fields.
  * **The bridge seemed repaired at once** (BLD-021, BLD-022).
    * Repairing peasants were shown standing: the frame had no activity for repairing. They now show the building
      animation and its sound.
    * The log of the session shows five peasants mended the bridge in 22 s (four repairers work 2.5 times as fast as
      one; UNK-18). Whether the original is slower is a question for the owner.
  * **Esc opens the game menu** when there is nothing left to cancel or deselect (SHELL-028, the owner's request).
  * **The build menu's categories had no shortcuts** (CTRL-029, HUD-034).
    * MENU.INI marks the build menu `capturekeys`: the original's categories have no letters, and the letters of their
      buildings work from the build menu. Tzar2 ignored the mark; the card model now honours it.
    * A category's hint lists the letters of its buildings.
  * **Units stand on one another** (WORLD-015). The collision radius is 7 px, half a sprite's width, and the push apart
    is soft and capped. It becomes work-queue item 4.
  * **The owner's check, that every bonus of buildings and technologies applies**, becomes work-queue item 5.
* **2026-10-21 — The core stays free of engines and of the original; UI logic moves into models (ADR 0182).**
  * **Owner's question:** a new game on the same core, with new balance, units and civilizations, perhaps on a 3D
    engine.
  * **Answer:**
    * The core is ready: it uses no engine, the rules are data, and civilizations are strings.
    * About a thousand lines of game UI logic lived in the Godot client.
    * A few paths name the original's classes.
    * The legacy AI language knows only three civilizations.
    * The frame is shaped for 2D sprites.
  * **Done now:** the command card is an engine-free model, `Tzar2.Game.Ui.CommandCard`, tested by `CommandCardTests`.
    The client only draws it.
  * **Deferred until a new game starts:** splitting the legacy importer into its own assembly, a trait for the
    kingdom's seat, AI civilizations as data, the frame's full heading, and the choice of host engine.
  * The work queue's ADR item is done.
* **2026-10-21 — Every data file of the original is accounted for (ADR 0181, decision 5).**
  * [rules/data-usage.yaml](rules/data-usage.yaml) covers the INI files and unit tables of the reference build, 32
    files or groups:
    * 4 are read whole and 7 not at all;
    * of the names in the others, 142 are used, 4 restated by hand, 18 not needed, and 48 are to do, each pointing at a
      rule.
  * `t2 rules check` checks on CI that every used name is quoted in the code. With the install
    (`--origin` or `TZAR2_ORIGIN`; `RulesLedgerTests` runs locally), every section, key and column must be accounted
    for.
  * Found and added to the ledger:
    * the attack's hit frame (`actionrow`, WAR-014; Tzar2 hits at half the swing);
    * walking strides (`move_len`, WORLD-012);
    * animations of capture, meat, murder, prayer and casting, hero portraits and creation sounds (PRES-005..007);
    * tree positions (PRES-009);
    * the minimap's terrain colours (HUD-030);
    * working peasants named by job (HUD-032);
    * the strength of random items (`RES.INI`, ITEM-015);
    * menu hints (`HELP.INI`, SHELL-026) and quit confirmations (SHELL-027);
    * the credits screen (SHELL-020; CREDITS only printed a path);
    * the player's AI assistance (none, economy, war, full; AI-004) and the kingdom's lists of heroes, spies and items
      (HUD-033).
* **2026-10-21 — The ledger's first fill: 309 rules in 21 chapters.**
  * **Sources:**
    * the manual and the strategy guide;
    * the patch notes (`TZAR110.TXT`; TDD §5.1's patch-note tests never existed, the ledger now carries each line);
    * the in-game help (`HELP/ITEMS.TXT`, 407 topics), the order descriptions of `STRINGS.INI`, `ORDERS.INI`;
    * the three play-tests.
  * **Status:**

    | Tested | Implemented | Partial | Missing | Deviation | Unclear |
    |---|---|---|---|---|---|
    | 131 | 83 | 39 | 33 | 7 | 16 |

    Existing tests now name 131 rules. A status of tested now needs a test that checks the rule itself, not the client's
    flag.
  * **Found:**
    * The simulation ignores the Shift and Alt of an order, so there are no waypoints and no move-sneak; the client's
      tests only checked the flag.
    * No diplomacy, tribute, inventory, save, load or help screens; chat only in multiplayer.
    * The selection view lacks the kingdom, the level and the units inside.
    * The help gives facts that differ from Tzar2's placeholders:
      * Landing Tactics +50 %;
      * one peasant per field;
      * siege weapons hurt their own side;
      * the gathering technologies raise the load, not the speed;
      * ship cannon levels fire faster, not harder.
    * Unhandled orders on the original's cards: online help, impersonate, reveal, uncloak, the souls' kill and berserk,
      Build Magic Item and the Asian market's item trade.
  * The two old checklists (hotkeys, editor) stay as they are.
* **2026-10-21 — A ledger of the original's rules (ADR 0181).** About 40 findings of three play-tests: about 8 are
  in the manual, about 10 in the original's data files nobody read, about 12 known only from playing the original, the
  rest plain defects. The owner asked for an automatic check. Each rule of the original now sits in
  [rules/](rules/README.md) with its source and status, and tests name the rules they check with
  `[Trait("Rule", "ID")]`. `t2 rules check` runs in CI. It fails on an unknown id, on a `tested` rule without a test,
  and on a stale report. The first chapters hold the play-tests' 49 rules, 20 of them tested by tests that existed.
  Owner decisions:
  * the side panels wait for M8;
  * the order of work is the ledger, the data accounting, the command card, then the engine/content split ADR.
* **2026-10-21 — Third play-test: the HUD.**
  * As in the owner's screenshot of the original castle panel, each order button shows how many of its item the
    selected buildings have queued, and the hourglass TIME stat shows the progress of the work in hand. The queue strip
    is gone.
  * An upgrade being made keeps its button with its count (it was hidden, its progress shown apart).
  * Long names ("Watch Tower") and the health line wrap between words beside the stats instead of running under them.
  * Card tooltips: the original's was one line ("Create Knight - 'K' ( Gold:70 Food:120 ) Requires: Stable, Plate
    Armor"). The owner preferred the box, with the hotkey added. It shows:
    * the name with the hotkey on a key cap;
    * the price with the original's resource icons, short ones in red, and the time;
    * what the order does (`orders_add`);
    * why it is greyed out, in the original's words ("Requires: ...").
  * Tooltip behaviour follows common practice:
    * 150 ms before it shows, and at once when moving to the next button;
    * it sits above the card, never over a button, inside the screen;
    * it follows the control the pointer is really over every frame, so a new selection, an emptied slot, a dialog or
      leaving the button takes it away.
* **2026-10-20 — Third play-test: the sim's rules.** ADR 0180, from the owner's knowledge of the original:
  * any builder helps on a site, whatever its civilization;
  * a queue takes any number of any items, one entry per item, and a right click on an order button cancels one of it;
  * loads appear at once when their work is done, while milk still fills the pail;
  * after a node falls, the worker takes the next one it can reach on the way to the drop-off;
  * units appear around the building on free tiles of the area with a way out;
  * idle peasants find work after 10 s.

  Delay and radius are placeholders. The goldens and bench hashes are re-pinned, the hashed layout is version 8, and
  `MultiplayerTests.A_save_with_everyones_consent…` once timed out under the full parallel run but passes alone.
* **2026-10-20 — Third play-test by the owner: what a right click does, and how it shows.** ADR 0179: each selected unit
  does its default action on what is clicked. Peasants capture enemy buildings (manual ch. 5 § 2.3), units board own
  holders that take them, pick up items, and follow an own unit (a herd gathers round the clicked cow); harvesters gather
  only resources they can. The pointer shows the selection's strongest action from the same rules (the hand only to
  capture or pick up; it showed over everything). Every original pointer clicks with its IMAGES.INI `cs_*` hotspot (UNK-40
  resolved for them). A move plays the original's red cross (`anims\cursor`) where the ground was clicked; an order on an
  object blinks its marks. The pause holds the sounds that are playing and starts no work sounds (a chopping sound kept
  playing). The rest of the play-test is work-queue item 1.
* **2026-10-20 — The AI gathers and marches to reachable ground.** The second play-test's log had 270 unreachable
  goals: the AI's gather point (7 tiles from the town towards the map centre) and its waves' move-advance goals (a
  building's centre) often lay in a building, a forest or water. Both now snap to the nearest walkable tile of the
  town's land (ai-placeholders E10/E12 unchanged otherwise); the AI golden is re-pinned.
* **2026-10-20 — The tavern and trade dialogs.** The tavern's gambling (`D_GAMBLE`: the resource, the stake, the dice
  rolling and the original's win or lose picture, the proportions 1:1 to 1:10) and exchange (`D_TRADE1`: ten for one)
  and a trader's route (`D_TRADE`: the goods each way, the load per trip, then a click on the far market; the near one
  is the trader's nearest own market) open in the card's place on the original's panels; what each cell does is ours.
  The original's tavern menus for mercenaries and heroes are empty in MENU.INI: Tzar2 lists the period's offers there
  (the frame now carries them) with the original's training icons, the count, the level and the price. A submenu opens
  when any of its orders works (the loans' menu was greyed out); the tavern's offers show unaffordable too. The odds
  pictures say 1:1, 1:2, 1:5 and 1:10, while the placeholder odds are 2, 3, 5 and 10 (UNK-28; left to the captures).
* **2026-10-20 — Second play-test by the owner; its findings fixed.** On the original map "There is not enough room for
  two of us" every kingdom owns European, Arabian and Asian castles and peasants, but the client loaded the sprites of the
  kingdoms' civilizations only: the others were invisible though selectable. Now the sprites of every civilization on the
  map are loaded too. The production queue shows, as in the original, what is being made with its progress under the
  icon and how many are left on it (no more "71 % (+4)" among the stats). The placement ghost wears the local
  kingdom's colours (it lacked the player-colour overlay). A selection of many units shows one icon per type with the
  count and the average health. The pointer is the original's default cursor `s_def`, a golden spearhead (the owner's
  screenshot of the original; the hand is for taking things), or the system's (SETTINGS → Pointer). The pause froze nothing: the session publishes frames only after ticks, so the client
  now reads the session's pause itself; animations and rain stop. Guard: a click on an own unit or building follows and
  defends it (the guards watch for enemies while walking, keep guarding after a fight and come back), on an enemy
  attacks it, on open ground holds it with a move-advance; before, only a moving unit could be guarded, and the first
  fight ended the order.
* **2026-10-20 — Units touch what they work on; arrows, shells and flames are drawn.** Before the second play-test
  the owner added four findings. Soldiers hit buildings and peasants unloaded a tile away: the interaction reach
  (32 px from the footprint, plus the unit's radius) let them stop as soon as they came near; now a unit still walking
  must touch the structure (12 px), the full reach stays for one that stopped (blocked by others). The selection mark of
  units is the square of the original's selection box (IMAGES.INI `selection`), like the structures' corners, instead
  of an ellipse. Burning buildings show the original's `fire_big` / `fire_small` flames, drawn additively as the
  original's effects are painted on black. Projectiles are in the frame and drawn with the original's pictures
  (arrows and ballista bolts turned along the flight, catapult stones and cannon balls with their shadows, rockets,
  magic stars, flame and freeze bolts) on an arc; stones, shells, rockets and the bolts leave the original's explosions.
  The pairing of projectile kinds and pictures, the flame spots and the arcs are ours (UNVERIFIED).
* **2026-10-20 — Spells, items and the other card orders the sim already had.** The 21 spell orders, raise dead and
  the ninjas' cloak and murder cast at once on the caster (self spells) or on the clicked unit or ground with the
  original's spell cursor; loans and their repayment in the tavern; bribery; the cathedral's crusade; picking up, using
  and dropping items; mine, dig, chop and fishing harvest. The frame carries casters' mana (the selection panel shows
  it beside the original's mana icon). The tooltip of a spell shows its mana cost.
* **2026-10-20 — Single player as in the original, with its maps.** SINGLE PLAYER opens the original's menu
  (`S_SINGLEPLAYER`: CAMPAIGN, CUSTOM MAP, RANDOM MAP, LOAD GAME, BACK). CUSTOM MAP lists the package's original maps
  (`legacy/maps/*.wmp`, converted with ADR 0120's converter behind the loading screen the first time) with a picture
  and the description (`S_SINGLECHOOSEMAP`); the map's setup (`S_SINGLESETUP`) keeps the map's own kingdom,
  stockpiles, population limit and fog unless the player changes them, and sets the computers' difficulty. The
  computers play the original `random.ais`, not the map's own AI script names (not resolved yet). RANDOM MAP moved to
  its own art (`S_SINGLERND`) and gained No Fog and Explore; LOAD GAME loads the last quick save of a campaign mission.
* **2026-10-20 — The AI weighs threats; units on a move-advance answer fire (ADR 0178).** Two causes of "the AI
  ignores towers": a unit on a move-advance did not fight back when hit from beyond its sight (a stone tower hits at
  13 tiles, a footman sees 6), and the military executor never looked at towers or compared strengths. Now a
  move-advance answers fire and resumes its way, and a unit may chase what can hit it. The AI rates every armed object
  by √(health × attack rate) and, by difficulty: answers enemy towers, buildings and sites at its town (Normal and Hard
  only with enough power, else the army grows first), launches waves only with enough power for the target's defence,
  retreats from lost fights, focuses on soldiers and towers before the nearest object, aims at the least defended
  building (Hard), and keeps gatherers away from tower reach. Easy weighs nothing. The tactics are Tzar2's own and
  tunable (ai-placeholders T1–T7); the AI and gameplay hashes are re-pinned.
* **2026-10-20 — Wide screens and SETTINGS (ADR 0177).** The world fills the window: the logical screen is the window
  over the interface scale (Auto: the largest whole number that leaves at least 1024 × 720), and the original HUD
  bitmap is cut into anchored pieces with its vine and marble repeated between them; the original's 1024 × 768
  letterboxed picture stays as the Authentic preset. SETTINGS in the main menu (display preset, interface scale,
  window mode and size, edge and drag scrolling, scroll speed, volumes, game speed, day/night, weather) saves and
  applies at once; menus re-centre and the game rebuilds its HUD when the window changes. The original's side layouts
  and a smooth fractional scale are left for later.
* **2026-10-20 — The original campaigns are in the campaign screen.** The import keeps the install's campaigns and
  maps (`Campaign/*.wcm`, `Maps/*.wmp`) in the package under `legacy/`; the client converts the campaigns when the
  screen first opens (the tutorial's 7 missions and the main campaign's 19: 0.4 s) with the converter of ADR 0120,
  shared with the CLI (`LegacyPlayables`). The screen is laid out on the original art's panels: the missions (paged),
  the briefing, the campaigns, the description with progress and RESTART, and BACK and PLAY in the bar's button slots.
  The reference and synthetic package hashes are re-pinned.
* **2026-10-20 — A match starts in under a second, behind the original's wait screen.** The 13-second freeze was the
  terrain, not the map (28 ms) or the sprites (0.4 s): the composer looked up a terrain's bitmap for every pixel and
  redrew whole chunks for each of the water's 515 animation frames. Now bitmaps are looked up per tile, later frames
  redraw only the tiles that move, and chunks are composed in parallel: 375 ms on the operator's PC. Skirmishes and
  campaign missions show one of the original's wait screens (`WAITSCR`) while they are built; the log has the timings
  of every match load.
* **2026-10-20 — Peasants at work look and behave right.** Milking has its own animation and a peasant carrying a
  load uses the original's carrying sprites (logs, gold, stone, field food, a pail of milk, meat). A cow claimed by a
  kingdom stops (it kept walking to its wander goal, away from its milker), and a milker at a dry cow fills the pail
  at another cow or field nearby, or waits for the milk to regrow when there is none, instead of carrying home one or
  two units a trip. Fields are walkable (`WalkableFootprint`, UNVERIFIED):
  units cross them and each farmer works on a tile of the field instead of digging beside it; nothing can be built on
  a field. Golden and bench hashes re-pinned (gameplay: ruleset hash; AI: streams; benches: gameplay and AI).
* **2026-10-20 — The command card does what the sim can.** Research (castle, guilds, mills), upgrades (towers,
  walls, gates, ships), capture, move-advance and move-sneak, stances, gate control, explore, destroy, emerge and
  unload, become priest or Jihad warrior, attack ground, next idle peasant and the quest log, with costs, research times
  and reasons in the tooltips. Researched technologies and owned upgrades leave the card, as do orders a technology
  unlocks until it is researched (TECHTREE `[orders]`). A card with more than eight orders gets pages of seven and a
  page button (a European peasant has nine); hotkeys reach every page. The frame carries each producer's queue item
  kind (unit, research, upgrade), the entity's upgrades, stance and gate state, the research queued by each kingdom and
  what the local player may research.
* **2026-10-20 — First play-test by the owner; controls and HUD fixed.** A skirmish against the AI and the demo
  campaign's first mission; 15 findings, with causes from the run's log and the client code. Fixed now: a click selects
  a unit anywhere on its drawn figure (the hit box was the INI selection box at its feet); a held right or middle button
  drags the map and a right click gives its order on release; animations and rain follow game time, so they stop on
  pause; the placement ghost has no fill, only the cells that block it turn red; card tooltips show the cost and why a
  button is greyed out (the "unavailable" barracks and lumber mill lacked resources the card did not show); the frame
  carries what the local player can make by the sim's requirement check and its researched technologies, so
  technology-gated buttons no longer stay grey for ever; Repair on the card and by right click on an own damaged
  building; portraits wear the owner's colour (ICONS.BMP uses the sprites' player range 240–255); cursors by action,
  with Tzar2's own pickaxe (gold, stone) and saw (trees) painted in code, since the original has one harvest cursor, a
  shovel, kept for fields; a hand over cows. The rest is work queue items 1–6.
* **2026-10-20 — Owner decision: the AI keeps the original scripts for strategy, new tactics.** `random.ais` keeps
  choosing the build order, technologies and army mix (the computer's character); the tactics are rewritten: threat
  assessment, base defence, target choice, retreat, tower avoidance, with difficulty scaling how smart they are. The
  play-test showed the military executor sending the army onto a watch tower's tile with a move-advance: the goal is
  blocked, the soldiers stand under fire. An ADR comes with the work.
* **2026-10-20 — Main campaign: 14 of 19 missions complete (measured).** The editor's acceptance test played every
  mission with the scripted solver on the clean GOG install and the current rules (walking speeds, gathering, TIME, the
  37.5-minute day): the same 14 as reported before the measurements; the five others time out.
* **2026-10-20 — Playable from the repository; local-only tests green again.** `tools/play/play.ps1` imports the
  owner's copy, builds and starts the client. The importer no longer takes the game's crash dumps (`Logs/`) and the
  installer's files from a played GOG install into the package. The local-only tests had gone stale since #24 because
  CI cannot run them: re-pinned on the clean GOG v1.10 EN install (`legacy.referenceDataHash`, the reference build's
  2,615 INI sections, TIME and speed pins) and restricted to the files of the reference manifest.
* **2026-10-19 — Diagnostics for the play-tests (ADR 0176).** A log folder per run of the client (`game.log`: match
  setup, every command, gameplay events, AI goals and orders, triggers, kingdom summaries every 20 s, Godot's output and
  errors), crash reports with the running match's replay, and a replay of every match including campaign missions.
  Default level: debug everywhere. A traced match at verbose has the same hash as an untraced one. The first trace
  shows the Hard AI ordering two buildings onto one site in a tick.
* **2026-10-19 — Gathering: 900 ms per unit (ADR 0175).** One load of 10 wood took ~9.0 s in the `gather-rate` burst;
  applied to every resource. Batches B and E had timed groups of peasants, so "two or three times faster" was wrong.
* **2026-10-19 — The fidelity gate moves to M8 (ADR 0174, owner decision).** Next: the gathering rate (one
  measurement), tracing for diagnosis, then a playable build for the owner's first play-test.
* **2026-10-19 — Owner decision: stop measuring unit by unit.** Measuring each unit's speed, attack and so on is not
  worth it at this stage. Where the original's tables have a value and a conversion has been measured (TIME 1.25 s per
  unit, carry loads, walking speed by stride, the day), apply the conversion to every entity; where a value is missing
  (e.g. a rate), measure it once on one object and apply the result to all. The fidelity tests are left for the final
  stages. The maintainer reviews what the project really lacks before choosing the next work.
* **2026-10-19 — Batch E: groups walk at their slowest member's speed.** Group speed verified (suite 9 of 30). The other
  scenarios failed for reasons now known (kingdom 2's AI takes over units given to it; Feudalism did not start; Ctrl+C
  chose another castle; the capture analysis missed the colour change); not repeated, by the decision above.
* **2026-10-19 — Batch E prepared.** Batch A's melee duels gave no hits because the enemy defender, idle while memory
  was scanned, walked off to Nature's cows in MP2's middle and the attack click hit empty ground. `damage-bonuses` now
  fights by our base (row 53, ADR 0171's view); the other batch E scenarios use peasants, buildings or our base already.
* **2026-10-19 — A day lasts 37.5 minutes; a match starts at 10:00 (ADR 0173).** Batch D: the clock globe (24 frames,
  one per hour) stepped every 93.7 s; the day-clock analysis now reads the globe and ignores brightness that units
  spoil. The Hard AI ended the passive game after 12 minutes, before the night: the night's share needs a longer run.
  Suite **8 of 30**.
* **2026-10-19 — Walking speed is per class, not from SPEED (ADR 0172).** Batch C and the third `unit-speed` run, both
  clean: every foot class walks 40 px/s whatever its SPEED, mounted 72, troll 47, ghost 67, cows and bears 26; speed
  follows the walk sheet's stride. Measured classes get their own speed; the speed check passes: **7 of 30**. The
  gameplay benchmark starts each kingdom's first house damaged so the golden stream still covers repair.
* **2026-10-19 — The 144-entry class table stays (product owner).** The previous agent's open question about "the 144
  class names in the converter" is `LegacyClasses` (PR #33): the original's built-in object class order, which map
  files index into. Owner decision: it is a format fact and stays; recorded in
  [original-build/README.md](original-build/README.md#1-repository-policy).
* **2026-10-19 — Unit-speed run at the new place missed it (ADR 0171 amendment).** The minimap click (0.1, 0.95) fell
  outside MP2's map, which the minimap draws in its middle 64 px; the view stayed at the start and the path crossed our
  own cows. Facts not committed; the scenarios click map tile (6, 60) and a test guards MP2 minimap clicks.
* **2026-10-19 — Unit speeds: SPEED 4 = 40 px/s; engine 10 px/s per point (ADR 0171).** Peasant, archer, priest and
  mage walked 39.9–40.1 px/s both ways. The soldiers attacked Nature's cows in MP2's middle instead of walking; the
  speed scenarios moved next to our base and the analysis rejects short or wrong-way fits. The gameplay benchmark bot
  repairs with the nearest busy peasant so the golden stream still covers repair; hashes re-pinned. The capture ran on
  a Russian keyboard layout: the harness's switch to US (PR #60) works.
* **2026-10-19 — Batch B: a node sets its own load; cows carry 50 (ADR 0170).** Wood, stone and gold verified at 10 per
  trip. Unit speeds failed because the scenario dragged a box into the edge-scroll zone; fixed, to be re-captured; a test
  now keeps scenario mouse positions 16 px from the edges.
* **2026-10-19 — Project docs (PR #64).** This file, [DEVELOPMENT.md](DEVELOPMENT.md), the ADR index and `AGENTS.md`,
  so that another agent or developer can continue the work.

* **2026-10-19 — Batch A merged; TIME is 1.25 s per unit (ADR 0169).** Training (peasant 9.99 s for TIME 8) and
  construction (lumber mill 36.85 s for TIME 30) both measured 1.25 s per unit; research times (ms in
  TECHNOLOGIES.INI) stay unscaled until batch E measures one. Golden hashes and CI bench hashes re-pinned.
* **2026-10-19 — Harness fixes from the operator runs (PR #60).** `hmnopop` before every spawn (spawning obeys the
  house limit), DirectDraw device count = `DirectDrawEnumerateA` + 1, typing on the US layout whatever the
  operator's layout. `hmkingdom N` changes the owner of the selected unit.
* **2026-10-18 — PR clean-up after the handover.** The agent's open PRs were merged after a combined local build and
  test run; stacked PRs that conflicted after squash merges (#54, #55, #59) were replaced by the same commits replayed
  on `main` (#58, #63) instead of force-pushing. #57's failing allocation gate was a test precondition, not a
  regression (fixed in the PR).
* **2026-10-18 — Handover.** The implementation agent is no longer available; the maintainer continues. Workflow
  chosen by the product owner: branches and PRs through the GitHub CLI, squash merge only after green CI.
* **2026-10-18 — Original game on the operator's PC.** Clean GOG v1.10 EN; captures need GOG's compatibility database
  removed for the session (`sdbinst -u`, the harness prints the command), `DIRECTXDEVICE=0` and cnc-ddraw with
  `devmode` (ADRs 0110, 0111; [DEVELOPMENT.md](DEVELOPMENT.md#capture-sessions-on-the-operators-pc)).
* **2026-10-17/18 — M0–M6 built by the implementation agent** (PRs #1–#53; ADRs 0001–0168).
* **2026-10-11 — Original files stay local.** Nothing from `Origin/` is committed; manifests and hashes only; the
  Spanish FX Gold Edition campaigns (local Russian translations) are kept locally as a test corpus
  ([original-build/README.md](original-build/README.md)).
* **2026-10-11 — Windows first (DEC-14).** 1.0 ships for Windows x64; macOS, Linux, Android and iOS follow in the
  platform-expansion phase, with the cross-platform constraints kept from the start.
* **2026-10-11 — Technical design (TDD).** C# core + Godot 4 .NET, deterministic lockstep, importing assets from the
  user's install (DEC-01…DEC-14).
