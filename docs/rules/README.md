# Rules of the original

The rules of *TZAR: The Burden of the Crown* (v1.10), one chapter per file. Each rule has its source and its status
in Tzar2, and tests name the rules they check. Why it exists: [ADR 0181](../decisions/0181-a-ledger-of-the-originals-rules-checked-against-tests.md).

* **Before building a feature,** read its chapter. A feature is done when its rules are `tested`.
* **A play-test finding about the original** becomes a rule with a test in the change that fixes it.
* **A deliberate difference** is `deviation`, with the ADR or owner decision in its note.
* Hotkeys and the editor keep their own checklists: [hotkey-parity.md](../mechanics/hotkey-parity.md) and
  [editor-parity.md](../mechanics/editor-parity.md).

## Writing a rule

```yaml
schema: tzar2-rules/1
prefix: PROD
title: Production and population
rules:
  PROD-001:
    rule: A building queues any number of units of any kinds, as long as each is paid for when ordered.
    source: [owner 2026-10-20 (third play-test)]
    status: tested
    adr: [0180]
```

* `rule`: our own words, never the manual's text (REQ-LEGAL-01). Quote a short phrase only when the wording matters.
* `source`: one or more. Each starts with its kind:

  | Kind | Example |
  |---|---|
  | `manual` | `manual ch. 5 §2.3` |
  | `guide` | `guide p. 12` |
  | `editor-manual` | |
  | `patch` | `patch 1.05 (TZAR110.TXT)` |
  | `data` | `data IMAGES.INI [cursor]` |
  | `help` | `help HK.TXT` |
  | `strings` | `strings STRINGS.INI [orders_add]` |
  | `owner` | `owner 2026-10-20` |
  | `capture` | `capture capture-20261019-221300` |
  | `community` | |
  Quote a source that holds a comma or brackets: `'data IMAGES.INI [cursor]'`.
* `status`:

  | Status | Meaning |
  |---|---|
  | `tested` | done, and at least one test names it |
  | `implemented` | done as far as the code shows, no test yet |
  | `partial` | done in part; `note` says what is missing |
  | `missing` | not done |
  | `deviation` | Tzar2 does it differently on purpose; `note` names the ADR or owner decision |
  | `unclear` | the sources disagree or say too little; `note` says what would settle it |
* `adr`, `note`, `work` (the work-queue item or issue that will close it) are optional.
* Ids never change and are never reused. A rule that turns out wrong is reworded, or becomes `unclear` with a note.

## Naming rules in tests

```csharp
[Fact]
[Trait("Rule", "PROD-001")]
public void A_queue_takes_any_number_of_any_units() { ... }
```

`dotnet test --filter Rule=PROD-001` runs the tests of one rule. A trait on a class applies to all its tests.

## The original's data

[data-usage.yaml](data-usage.yaml) says what Tzar2 does with every data file of the original. It covers the INI files
and the unit and building tables, and lists names only (REQ-LEGAL-01). Each file is accounted for in one of these ways:

* by its key names, section names or table columns, each of them:
  * used (the code quotes it);
  * restated (its facts are typed into code or rules by hand);
  * ignored, with why;
  * to do, with the rule that will use it;
* as a whole table that a reader takes as it is;
* as not read at all, with why.

On CI, `t2 rules check` fails when a used name is not quoted anywhere in the code, or when a to-do names an unknown
rule. With the original install it also fails on any section, key or column of the reference build that is not
accounted for:

```sh
t2 rules check --origin "E:\Games\Tzar"     # or set TZAR2_ORIGIN; RulesLedgerTests does the same locally
```

## Commands

* `t2 rules check [--origin <install>]`: validates the chapters, the tests' rule ids, the data account and this report.
  CI runs it (`RulesLedgerTests`); with an install it also checks the data account against every data file.
* `t2 rules report`: rewrites the report below.
* `t2 rules list missing partial`: prints the rules with those statuses.

## Report

<!-- BEGIN t2 rules report: generated, do not edit -->

| Chapter | Rules | Tested | Implemented | Partial | Missing | Deviation | Unclear |
|---|---|---|---|---|---|---|---|
| [Computer opponents](ai.yaml) (AI) | 4 | 1 | 0 | 1 | 0 | 1 | 1 |
| [Capture](capture.yaml) (CAP) | 4 | 3 | 1 | 0 | 0 | 0 | 0 |
| [Combat](combat.yaml) (WAR) | 14 | 8 | 4 | 0 | 2 | 0 | 0 |
| [Construction and repair](construction.yaml) (BLD) | 22 | 17 | 4 | 0 | 0 | 0 | 1 |
| [Selection, mouse and pointers](controls.yaml) (CTRL) | 29 | 18 | 10 | 1 | 0 | 0 | 0 |
| [Diplomacy and victory](diplomacy.yaml) (DIP) | 9 | 7 | 1 | 1 | 0 | 0 | 0 |
| [Resources and gathering](economy.yaml) (ECO) | 22 | 16 | 5 | 0 | 0 | 0 | 1 |
| [Experience and heroes](experience.yaml) (EXP) | 7 | 6 | 0 | 1 | 0 | 0 | 0 |
| [Holders and transport](holders.yaml) (HOLD) | 9 | 8 | 0 | 1 | 0 | 0 | 0 |
| [The screen](interface.yaml) (HUD) | 34 | 8 | 14 | 5 | 4 | 2 | 1 |
| [Items](items.yaml) (ITEM) | 16 | 10 | 3 | 1 | 1 | 1 | 0 |
| [Magic, religion and spies](magic.yaml) (MAG) | 21 | 16 | 4 | 0 | 0 | 0 | 1 |
| [Nature, gateways, day and weather](nature.yaml) (NAT) | 6 | 5 | 0 | 1 | 0 | 0 | 0 |
| [Unit orders](orders.yaml) (ORD) | 18 | 13 | 5 | 0 | 0 | 0 | 0 |
| [Animations, effects and sounds](presentation.yaml) (PRES) | 9 | 2 | 3 | 2 | 2 | 0 | 0 |
| [Production and population](production.yaml) (PROD) | 15 | 10 | 4 | 0 | 0 | 1 | 0 |
| [Menus, setup and pause](shell.yaml) (SHELL) | 28 | 2 | 10 | 8 | 6 | 2 | 0 |
| [Technologies and upgrades](technology.yaml) (TECH) | 36 | 29 | 3 | 1 | 2 | 0 | 1 |
| [Trade, tavern and loans](trade.yaml) (TRADE) | 10 | 5 | 1 | 1 | 1 | 0 | 2 |
| [Exploration and visibility](visibility.yaml) (FOG) | 7 | 6 | 1 | 0 | 0 | 0 | 0 |
| [Terrain and movement](world.yaml) (WORLD) | 15 | 7 | 5 | 1 | 0 | 2 | 0 |
| **All** | 335 | 197 | 78 | 25 | 18 | 9 | 8 |

## Not done yet

Missing and partial rules.

### Computer opponents

* **AI-001** (partial): Each computer kingdom plays an AI script, chosen per player before the game. *Tzar2 picks the script; it cannot be chosen (SHELL-010, SHELL-016).* (ADR 0178)

### Combat

* **WAR-009**: A ghost may let out a banshee scream that lowers the attack and defence of the enemies around it.
* **WAR-010**: The Messiah (campaign) is immune to magic.

### Selection, mouse and pointers

* **CTRL-017** (partial): An order on an object (a building to enter, repair or attack) is confirmed by an animation around the object. *Tzar2 blinks the object's selection corners three times; which animation the original plays is not identified.* (ADR 0179)

### Diplomacy and victory

* **DIP-008** (partial): F4 shows the scores. *The scores show as messages; there is no score screen.* (ADR 0106)

### Experience and heroes

* **EXP-005** (partial): Very experienced units become heroes and can be named. *Units become named heroes at level 12 with Heroism; the player cannot name them.*

### Holders and transport

* **HOLD-008** (partial): Ships, magic carpets and traders can load units, but only ships and carpets carry them over water. *Traders do not load units.*

### The screen

* **HUD-009**: The interface can be placed along the bottom, the left or the right of the screen. Work: STATUS work queue 12 (M8, owner decision 2026-10-21).
* **HUD-010** (partial): The resource bar at the top shows food, wood, stone and gold, the population against its limit, and the CHAT, DIPLOMACY and MENU buttons. *DIPLOMACY says "not available yet" and CHAT works only in multiplayer.*
* **HUD-011** (partial): The chat screen opens with the CHAT button or Enter; the message goes to the allies, the enemies, everyone, or players picked from a list that shows each one's stance. *Multiplayer only, as a chat line where Tab cycles everyone, allies and enemies; there is no list of players to pick from.* (ADR 0168)
* **HUD-016** (partial): The selection view shows the selected object's picture, name, kingdom, health, attack, mana, power, range, resistance (buildings), units inside, defence, time and level. *The kingdom's name (only its colour shows) and power are not shown. INSIDE and LEVEL show for the player's own objects only.*
* **HUD-020**: The question mark on the orders bar opens the online help, whose topics explain every unit, building and technology and how to play. *The help's 407 topics are in the original's HELP/ITEMS.TXT.*
* **HUD-025**: The fish patch a kingdom's boats are fishing is marked with a blue selection.
* **HUD-028** (partial): The \ key shows the trees translucent so that units under them can be seen. *Trees are drawn at 45 % opacity, not with the original's look.*
* **HUD-029**: An order shows only when its condition holds; the quest order only when there are quests. *The quest order is always there.*
* **HUD-033** (partial): With nothing selected the card offers the kingdom's lists of heroes, spies and items, the quests, AI assistance, the next idle peasant and the online help. *The kingdom's card shows with nothing selected. The quests, AI assistance, the next idle peasant and the lists of heroes, spies (and ninjas) and carried items work; an entry selects the unit (an item, its carrier) and shows it. Heroes inside buildings are not listed. The online help is missing.*

### Items

* **ITEM-010** (partial): A soul item kills a unit or makes it go berserk; a hero is not killed. *The simulation does it; the card's item_kill and item_berserk orders do nothing yet.*
* **ITEM-015**: The random items of a map take their strength from the data (treasure, weapons, armour, potions).

### Nature, gateways, day and weather

* **NAT-004** (partial): Cows can be slaughtered, and their meat collected by peasants or other units. *Peasants collect the meat; other units do not.*

### Animations, effects and sounds

* **PRES-002** (partial): Arrows, bolts, stones, cannon balls, rockets and spells fly as the original's pictures, and stones, shells, rockets and bolts leave its explosions. *Which picture goes with which weapon, the flames' spots and the arcs are Tzar2's guesses.*
* **PRES-005**: Units have their own animations for capturing and collecting meat (peasants), murder (ninjas), prayer (priests), casting (mages and priests) and other attack swings (footmen, macemen, peasants, bears, ninjas).
* **PRES-008**: Some animations start inside their sheet, at the row and column the data gives.
* **PRES-009** (partial): Trees, stumps and bushes are drawn from the tree sheet of the terrain set, at the positions and with the shadows the data gives. *Trees are drawn from the sheet with an assumed position of the trunk; the data's positions and shadows are not read.*

### Menus, setup and pause

* **SHELL-006** (partial): The setup of a custom map shows the players with their names and colours and lets each choose the European, Arabian or Asian civilization. *Only the player's own civilization can be chosen; the computer kingdoms keep the map's.*
* **SHELL-007** (partial): On the setup of a custom map the map's own settings apply until USE MAP SETTINGS is unchecked; then the number of kingdoms, game type, victory condition, difficulty, starting resources, No Fog and Explore can be changed. *Each option has a "map" choice instead of the one checkbox; the number of kingdoms, the game type and the victory condition cannot be changed.*
* **SHELL-010** (partial): The random map screen sets for each player an AI script (the small button by the name), the civilization, the number of kingdoms, the game type (Standard or Deathmatch), the victory condition, the difficulty, the starting resources, No Fog and Explore. *The AI script, the computer kingdoms' civilizations, the game type and the victory condition cannot be chosen.*
* **SHELL-011** (partial): A random map has a size (Medium, Large, Huge, Enormous, Real World), a type (Random, Archipelagos, Dry, Island, Continental, Lakes), a terrain set (Grass, Desert, Summer, Winter) and map resources (Low, Normal, High), and may have a guarded town, animals, gateways, magic items and cliffs. *Size, type and terrain set are offered; map resources and the five options are not on the screen.*
* **SHELL-012**: The settings of the random map screen are kept for the next time.
* **SHELL-013**: The random map screen and the multiplayer options let each computer kingdom resign on its own (a flag) or never (a sword).
* **SHELL-016** (partial): In the multiplayer setup each player has a ready flag (red or green), a colour, an AI script, a civilization and a team; settings can only change while not ready; there is a chat; only the creator has PLAY, active when everyone is ready. *The colour and the AI script cannot be chosen.* (ADR 0168)
* **SHELL-017**: Only the player himself chooses his AI script in the multiplayer screen, and every player is told when the host changes one.
* **SHELL-018** (partial): The multiplayer settings choose a random or a custom map, the random map's options, the game type, the difficulty, the victory condition, the starting resources, No Fog, Explore, the population limit, and day and night and weather; with MAP SETTINGS only No Fog, Explore, day and night and weather can change. *The game type, the victory condition and the random map's resources and options cannot be set.* (ADR 0168)
* **SHELL-019** (partial): The preferences set the sound volume (off when unchecked) with unit speech, building sounds and nature sounds kept or not, the music volume or CD audio, the game speed, the scroll speed and the path depth, day and night and weather effects, and when the menu bar appears. *CD audio and when the menu bar appears are missing; the path depth is dropped on purpose (TDD ORIG-03).*
* **SHELL-020**: The credits screen shows the makers of the game. *CREDITS prints where the credits file is.*
* **SHELL-021** (partial): The in-game menu (F1, F10) offers instructions, load game, save game, options, restart, quit and back to game. *Instructions are missing.* (ADR 0187)
* **SHELL-026**: The controls of the menu screens explain themselves in a hint line.
* **SHELL-027**: Quitting, resigning and disconnecting ask for confirmation.

### Technologies and upgrades

* **TECH-028** (partial): Mastery (Asians) lets priests teach kung-fu units and turn mana into experience. *Mastery gates the spell that turns mana into experience (spell_raiseexp), which works; whether priests "teaching kung-fu units" is more than that is not known.*
* **TECH-029**: Tournaments (Asians) lets kung-fu units share their experience.
* **TECH-030**: Jewelry (Asians) lets the markets sell and buy items, the same items in every market. *The markets' item menus (market_items, market_sell) are empty in MENU.INI and filled by the game; Tzar2 has no item trade.*

### Trade, tavern and loans

* **TRADE-002** (partial): A trade is set up by picking the starting market or harbor and its goods, the far one and its goods, and the amount, then START. *The near market is the trader's nearest own one; the far one is clicked (ADR 0103).*
* **TRADE-008**: The amount the tavern will lend shows in the bottom left corner of the view.

### Terrain and movement

* **WORLD-013** (partial): A random map grows as many trees as its terrain set's data gives for its size. *Tzar2's generator uses its own forest density (ADR 0060).*

## Unclear

The sources disagree or say too little.

### Computer opponents

* **AI-003**: The computer does not repair its damaged towers. *The guide describes one campaign mission; whether it is the AI's general behaviour is not known.*

### Construction and repair

* **BLD-020**: Deleting a building just after its construction began gives its cost back. *Tzar2 refunds a cancelled site in full at any time (placeholder); "at the beginning" suggests the original refunds only early, or less later.*

### Resources and gathering

* **ECO-002**: Gathering broken off before the load is complete gives nothing. *Tzar2 drops the work done (ADR 0180); a capture of an interrupted gather would settle it (UNK-17).*

### The screen

* **HUD-031**: Selection marks and the selection box are drawn in the terrain set's selection colour (white in every set). *Tzar2 draws own units green, enemies red and Nature yellow, and the box green; whether the original colours them by owner is to be confirmed.*

### Magic, religion and spies

* **MAG-008**: Hypnotism (Asian, from Concentration) is a spell of the Asian mages. *Neither the manual nor the game's text says what it does; Tzar2 makes it hold the unit like freeze.*

### Technologies and upgrades

* **TECH-031**: Piracy gives boarding units an attack bonus, and Boarding Ram lets ships board others. *The manual and the game's texts name them, but the original's data has no such technologies: they look cut from the game.*

### Trade, tavern and loans

* **TRADE-003**: Trade can also be done with other kingdoms' markets. *Whether a trader may use another kingdom's market, and who profits, is not known.*
* **TRADE-004**: Asian peasants can trade between markets as caravans do. *The help names "a caravan or an Asian peasant"; the manual gives the Asian market caravans. Tzar2 trades with caravans and traders only.*

## Deliberate differences

Tzar2 does these differently on purpose.

### Computer opponents

* **AI-002**: The computer's strategy (build order, technologies, army mix) comes from the original's scripts; its tactics are Tzar2's own, smarter with the difficulty. *Owner decision (2026-10-20, ADR 0178).* (ADR 0178)

### The screen

* **HUD-001**: An order button's hint gives its name, its hotkey, its price and what it still requires ("Create Knight - 'K' ( Gold:70 Food:120 ) Requires Stable, Plate Armor"). *Tzar2 shows a box with the name and a hotkey key cap, the price with the resource icons (what is short in red), the time, the order's description and the requirements; the owner preferred it to the original's single line (2026-10-21).*
* **HUD-034**: The hint of a category of the build menu lists the letters of the buildings in it. *The original's categories have no letter of their own (MENU.INI); the owner asked to see shortcuts for them too, and the letters of what is inside work from the build menu (CTRL-029).*

### Items

* **ITEM-014**: Two units reaching an item at once never both get it. *Fixes the original's item cloning (TDD §5, ORIG-11).*

### Production and population

* **PROD-005**: New units never appear in a pocket shut in by walls or buildings while the building has a way out elsewhere. *Fixes a bug of the original, at the owner's request.* (ADR 0180)

### Menus, setup and pause

* **SHELL-015**: The multiplayer screen takes the player's name, lets him create a game, search for games (with their information) and join one. *IPX and direct TCP/IP give way to a relay with join codes and a LAN browser (TDD ORIG-07, ADR 0168).* (ADR 0168)
* **SHELL-028**: Esc opens the game menu when there is nothing left for it to cancel or deselect. *The original opens the menu only with the MENU button, F1 and F10; the owner asked for Esc too.*

### Terrain and movement

* **WORLD-010**: The bodies of the dead never block the way (a bug of the original where battlefields became walls). *Fixes a bug of the original (TDD §5, ORIG-01).*
* **WORLD-011**: Units find a way to any place they can reach, however long; there is no path depth to set. *The original limited the search depth (TDD §5, ORIG-03); Tzar2 always finds the way and drops the option.*

## The original's data

[data-usage.yaml](data-usage.yaml) accounts for 32 files or groups of files: 4 read whole, 7 not read, and of the names in the others 147 used, 4 restated by hand, 18 not needed and 43 to do.

To do:

* `DATA/CREDITSFNT.INI` (the whole file): the font of the credits screen (SHELL-020).
* `DATA/EQUATIONS.INI` `enablequestbutton`: when the quest order is on (HUD-029)
* `DATA/IMAGES.INI` `startcol`: animations that start inside their sheet (PRES-008)
* `DATA/IMAGES.INI` `startrow`: animations that start inside their sheet (PRES-008)
* `DATA/LAYOUT.INI` `layout1024l`: the interface down the left side (HUD-009)
* `DATA/LAYOUT.INI` `layout1024r`: the interface down the right side (HUD-009)
* `DATA/OBJECTS/*.INI` `buildicon`: meaning not decoded
* `DATA/OBJECTS/*.INI` `image_attack2`: other attack swings (PRES-005)
* `DATA/OBJECTS/*.INI` `image_attack3`: other attack swings (PRES-005)
* `DATA/OBJECTS/*.INI` `image_capture`: the peasant's capture animation (PRES-005)
* `DATA/OBJECTS/*.INI` `image_meat`: the peasant's meat collecting animation (PRES-005)
* `DATA/OBJECTS/*.INI` `image_murder`: the ninja's murder animation (PRES-005)
* `DATA/OBJECTS/*.INI` `image_pray`: the priest's prayer animation (PRES-005)
* `DATA/OBJECTS/*.INI` `image_spell`: the casting animation of mages and priests (PRES-005)
* `DATA/ORDERS.INI` `enable`: an order shown only when its condition holds, the quest order (HUD-029)
* `DATA/RES.INI` `armouritem`: the strength of the map's random items (ITEM-015)
* `DATA/RES.INI` `potionitem`: the strength of the map's random items (ITEM-015)
* `DATA/RES.INI` `treasuryitem`: the strength of the map's random items (ITEM-015)
* `DATA/RES.INI` `weaponitem`: the strength of the map's random items (ITEM-015)
* `DATA/TREES.INI` `bushes`: where the bushes are in the tree sheet (PRES-009)
* `DATA/TREES.INI` `chopped`: where the stumps are in the tree sheet (PRES-009)
* `DATA/TREES.INI` `img_shadows`: the trees' shadow sheet (PRES-009)
* `DATA/TREES.INI` `img_trees`: the tree sheet (PRES-009)
* `DATA/TREES.INI` `pos_shadow`: where a tree's shadow sits (PRES-009)
* `DATA/TREES.INI` `pos_tree`: where a tree sits on its tile (PRES-009)
* `HELP/HELP_STR.INI` (the whole file): the labels of the online help (HUD-020).
* `IMAGES/TERRAIN/*.IMAGES.INI` `ruinpoints`: meaning not decoded
* `IMAGES/TERRAIN/*.TERRAIN.INI` `colors`: the colour of selection marks (HUD-031)
* `IMAGES/TERRAIN/*.TERRAIN.INI` `randomtreeset`: how many trees a random map grows, by map size (WORLD-013)
* `IMAGES/TERRAIN/*.TERRAIN.INI` `treename`: the kinds of trees in the sheet (PRES-009)
* `LOCAL/HELP.INI` (the whole file): the hints of the menu screens' controls (SHELL-026).
* `LOCAL/STRINGS.INI` `adds`: messages such as "Build more houses" (HUD-026)
* `LOCAL/STRINGS.INI` `defmsg`: the hint line's description of orders (HUD-001)
* `LOCAL/STRINGS.INI` `msg`: the game's messages in the original's words (HUD-026)
* `LOCAL/STRINGS.INI` `peons`: a working peasant named by its job, woodcutter, farmer, miner, builder (HUD-032)
* `LOCAL/STRINGS.INI` `q`: the confirmations of quit, resign and disconnect (SHELL-027)
* `LOCAL/STRINGS.INI` `wnd_message`: the message window's button (HUD-026)
* `LOCAL/STRINGS.INI` `wnd_quests`: the quest window's texts (HUD-026)
* `LOCAL/STRINGS2.INI` `chooseai`: the AI script dialog (SHELL-010)
* `LOCAL/STRINGS2.INI` `vm`: messages such as a time limit hit or a location sent (HUD-026)
* `LOCAL/STRINGS3.INI` `defmsg`: the hint line's description of orders (HUD-001)
* `LOCAL/STRINGS3.INI` `msg`: the game's messages in the original's words (HUD-026)
* `LOCAL/STRINGS3.INI` `vm`: messages such as sending results (HUD-026)

<!-- END t2 rules report -->
