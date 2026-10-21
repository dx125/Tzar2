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
| [Combat](combat.yaml) (WAR) | 14 | 6 | 4 | 0 | 4 | 0 | 0 |
| [Construction and repair](construction.yaml) (BLD) | 22 | 14 | 3 | 1 | 1 | 0 | 3 |
| [Selection, mouse and pointers](controls.yaml) (CTRL) | 29 | 13 | 8 | 2 | 4 | 0 | 2 |
| [Diplomacy and victory](diplomacy.yaml) (DIP) | 9 | 6 | 1 | 2 | 0 | 0 | 0 |
| [Resources and gathering](economy.yaml) (ECO) | 22 | 13 | 5 | 0 | 1 | 0 | 3 |
| [Experience and heroes](experience.yaml) (EXP) | 7 | 4 | 0 | 2 | 1 | 0 | 0 |
| [Holders and transport](holders.yaml) (HOLD) | 9 | 8 | 0 | 1 | 0 | 0 | 0 |
| [The screen](interface.yaml) (HUD) | 34 | 1 | 14 | 6 | 10 | 2 | 1 |
| [Items](items.yaml) (ITEM) | 15 | 8 | 2 | 2 | 2 | 1 | 0 |
| [Magic, religion and spies](magic.yaml) (MAG) | 20 | 13 | 3 | 2 | 1 | 0 | 1 |
| [Nature, gateways, day and weather](nature.yaml) (NAT) | 6 | 5 | 0 | 1 | 0 | 0 | 0 |
| [Unit orders](orders.yaml) (ORD) | 18 | 9 | 4 | 3 | 2 | 0 | 0 |
| [Animations, effects and sounds](presentation.yaml) (PRES) | 9 | 0 | 3 | 2 | 4 | 0 | 0 |
| [Production and population](production.yaml) (PROD) | 15 | 9 | 4 | 0 | 0 | 1 | 1 |
| [Menus, setup and pause](shell.yaml) (SHELL) | 28 | 0 | 10 | 10 | 6 | 2 | 0 |
| [Technologies and upgrades](technology.yaml) (TECH) | 36 | 25 | 1 | 3 | 2 | 0 | 5 |
| [Trade, tavern and loans](trade.yaml) (TRADE) | 10 | 5 | 1 | 1 | 1 | 0 | 2 |
| [Exploration and visibility](visibility.yaml) (FOG) | 7 | 5 | 1 | 1 | 0 | 0 | 0 |
| [Terrain and movement](world.yaml) (WORLD) | 15 | 5 | 5 | 3 | 0 | 2 | 0 |
| **All** | 333 | 153 | 70 | 43 | 39 | 9 | 19 |

## Not done yet

Missing and partial rules.

### Computer opponents

* **AI-001** (partial): Each computer kingdom plays an AI script, chosen per player before the game. *Tzar2 picks the script; it cannot be chosen (SHELL-010, SHELL-016).* (ADR 0178)

### Combat

* **WAR-009**: A ghost may let out a banshee scream that lowers the attack and defence of the enemies around it.
* **WAR-010**: The Messiah (campaign) is immune to magic.
* **WAR-013**: Siege weapons hurt every unit where their shots land, their own kingdom's included. *Stones and shells spare the shooter's own units.*
* **WAR-014**: An attack's blow lands at the frame of its animation that the data marks (the action row), so each unit strikes at its own moment of the swing. *Tzar2 lands every blow at half the cycle (hitDelayPercent, a placeholder, UNK-07).*

### Construction and repair

* **BLD-018** (partial): A wall shows green where it can be laid; a gate can be placed over a wall already built, or anywhere it fits. *A gate cannot replace a piece of wall.*
* **BLD-019**: Holding Alt while placing a bridge turns it. *The editor turns bridges with Alt; the game's placement does not.*

### Selection, mouse and pointers

* **CTRL-017** (partial): An order on an object (a building to enter, repair or attack) is confirmed by an animation around the object. *Tzar2 blinks the object's selection corners three times; which animation the original plays is not identified.* (ADR 0179)
* **CTRL-020** (partial): Ctrl and a digit give the selection that number; the digit selects the group again; the number shows in the corner of the selection. *The group number is not drawn on the units.*
* **CTRL-024**: Holding Alt or Ctrl while placing a gate turns the direction of the gate to be built. *The simulation builds turned gates (the editor places them); the client does not turn the ghost.*
* **CTRL-025**: The waypoints of the selection appear while Shift is held.
* **CTRL-026**: Shift+right click on several enemies one after another makes the selection attack them in turn; Alt+right click on an object attacks every object of its type.
* **CTRL-027**: With Guard, Shift picks several objects to guard one after another and Alt picks every object of the clicked type.

### Diplomacy and victory

* **DIP-008** (partial): F4 shows the scores. *The scores show as messages; there is no score screen.* (ADR 0106)
* **DIP-009** (partial): Computer kingdoms resign on their own when their AI flag allows it. *The scripts' resign request is recorded but never carried out, and the flag cannot be set (SHELL-013).*

### Resources and gathering

* **ECO-021**: Only one peasant tends each field. *Tzar2 lets several farmers work one field.*

### Experience and heroes

* **EXP-003** (partial): Units gain no experience from fighting allies. *Hits on the own kingdom give nothing, but hits on an ally (under confusion, rain of fire, or a scripted attack) still do.*
* **EXP-005** (partial): Very experienced units become heroes and can be named. *Units become named heroes at level 12 with Heroism; the player cannot name them.*
* **EXP-007**: Ctrl+] and Ctrl+[ select the units with more or less experience than the average of the selection (without a selection, those above 7 or below 8 on the screen). *The hotkey checklist still marks them blocked.*

### Holders and transport

* **HOLD-008** (partial): Ships, magic carpets and traders can load units, but only ships and carpets carry them over water. *Traders do not load units.*

### The screen

* **HUD-009**: The interface can be placed along the bottom, the left or the right of the screen. Work: STATUS work queue 12 (M8, owner decision 2026-10-21).
* **HUD-010** (partial): The resource bar at the top shows food, wood, stone and gold, the population against its limit, and the CHAT, DIPLOMACY and MENU buttons. *DIPLOMACY says "not available yet" and CHAT works only in multiplayer.*
* **HUD-011** (partial): The chat screen opens with the CHAT button or Enter; the message goes to the allies, the enemies, everyone, or players picked from a list that shows each one's stance. *Multiplayer only, as a chat line where Tab cycles everyone, allies and enemies; there is no list of players to pick from.* (ADR 0168)
* **HUD-012**: The diplomacy screen (DIPLOMACY, F5) shows each player's civilization and lets you choose ally, enemy or neutral towards him, offer shared vision, resources and unit control, and set allied victory; each button shows whether the other player agreed. *The simulation has stances, sharing and allied victory (ADR 0102); the screen is not built.*
* **HUD-013**: The tribute screen (TRIBUTE, F6) sends gold, food, stone or wood to another player; a left click on the amount raises it and a right click lowers it; SEND or CANCEL. *The simulation has tribute (ADR 0102); the screen is not built.*
* **HUD-016** (partial): The selection view shows the selected object's picture, name, kingdom, health, attack, mana, power, range, resistance (buildings), units inside, defence, time and level. *The kingdom's name (only its colour shows) and power are not shown. INSIDE and LEVEL show for the player's own objects only.*
* **HUD-017**: The eye by the orders bar, lit when the selected object carries items, opens its inventory in the orders bar, where an item can be used or dropped. *Use and drop act on the first item; there is no inventory view.*
* **HUD-018**: In an inventory a left click uses an item, a right click drops it, Ctrl makes either instant, Alt+click makes it the default item, and < and > turn its pages.
* **HUD-020**: The question mark on the orders bar opens the online help, whose topics explain every unit, building and technology and how to play. *The help's 407 topics are in the original's HELP/ITEMS.TXT.*
* **HUD-022**: The kingdom of a spy is shown as its true kingdom to its owner. *Spies cannot impersonate yet (MAG).*
* **HUD-025**: The fish patch a kingdom's boats are fishing is marked with a blue selection.
* **HUD-028** (partial): The \ key shows the trees translucent so that units under them can be seen. *Trees are drawn at 45 % opacity, not with the original's look.*
* **HUD-029**: An order shows only when its condition holds; the quest order only when there are quests. *The quest order is always there.*
* **HUD-030** (partial): The minimap paints terrains, trees and cliffs in the colours of the terrain set's data. *Tzar2 takes each tile's colour from its picture.*
* **HUD-032**: A working peasant is named by its job, woodcutter, farmer, miner or builder.
* **HUD-033** (partial): With nothing selected the card offers the kingdom's lists of heroes, spies and items, the quests, AI assistance, the next idle peasant and the online help. *The kingdom's card shows with nothing selected. The quests, AI assistance, the next idle peasant and the lists of heroes, spies (and ninjas) and carried items work; an entry selects the unit (an item, its carrier) and shows it. Heroes inside buildings are not listed. The online help is missing.*

### Items

* **ITEM-007** (partial): Some items must be used at a place; the Fire Rain book makes fire rain where the player clicks, on everyone there, own units included. *The simulation does it; the client uses the first item, as there is no inventory to pick from (HUD-017).*
* **ITEM-010** (partial): A soul item kills a unit or makes it go berserk; a hero is not killed. *The simulation does it; the card's item_kill and item_berserk orders do nothing yet.*
* **ITEM-012**: A unit that picks up a magic book has yellow stars floating above it.
* **ITEM-015**: The random items of a map take their strength from the data (treasure, weapons, armour, potions).

### Magic, religion and spies

* **MAG-012**: Spies (with Espionage) can impersonate a unit of another kingdom and reveal themselves again. Work: STATUS work queue 3.
* **MAG-017** (partial): After all crusaders have died a full crusade needs some time; one preached earlier brings fewer crusaders for the same price. *Tzar2 refuses a crusade during the wait instead of bringing fewer crusaders.*
* **MAG-018** (partial): A crusader dying in a ship or a building, or impersonated by a spy who then reveals himself, is counted out of the crusade. *Deaths in ships and buildings count; impersonation is missing (MAG-012).*

### Nature, gateways, day and weather

* **NAT-004** (partial): Cows can be slaughtered, and their meat collected by peasants or other units. *Peasants collect the meat; other units do not.*

### Unit orders

* **ORD-004**: A move-sneak (Alt+right click) goes to its destination and fights back only when attacked. *The client sends the Alt flag but the simulation moves as on a plain move.*
* **ORD-005**: Shift+right click adds waypoints; the units walk them in order. With Ctrl or Alt the waypoints are a move-advance or a move-sneak. *The client sends the Shift flag but the simulation replaces the order instead of queueing it.*
* **ORD-006** (partial): Patrol walks between the unit's position and the clicked point; with Shift several points make a patrol path. *Patrol between two points works; the path of several points needs the waypoints of ORD-005.*
* **ORD-007** (partial): Explore sends the unit to the clicked area, which it walks to explore it. *Explore takes no target; the units head for any unexplored ground they can reach.*
* **ORD-014** (partial): Unload makes a loaded vehicle let its units out at the clicked place; the pointer's colour shows whether they can land there. *The Unload button waits for a click on the place and the units land there; the pointer does not change colour.*

### Animations, effects and sounds

* **PRES-002** (partial): Arrows, bolts, stones, cannon balls, rockets and spells fly as the original's pictures, and stones, shells, rockets and bolts leave its explosions. *Which picture goes with which weapon, the flames' spots and the arcs are Tzar2's guesses.*
* **PRES-005**: Units have their own animations for capturing and collecting meat (peasants), murder (ninjas), prayer (priests), casting (mages and priests) and other attack swings (footmen, macemen, peasants, bears, ninjas).
* **PRES-006**: A hero shows a hero's portrait.
* **PRES-007**: A unit plays its creation sound when it is made.
* **PRES-008**: Some animations start inside their sheet, at the row and column the data gives.
* **PRES-009** (partial): Trees, stumps and bushes are drawn from the tree sheet of the terrain set, at the positions and with the shadows the data gives. *Trees are drawn from the sheet with an assumed position of the trunk; the data's positions and shadows are not read.*

### Menus, setup and pause

* **SHELL-006** (partial): The setup of a custom map shows the players with their names and colours and lets each choose the European, Arabian or Asian civilization. *Only the player's own civilization can be chosen; the computer kingdoms keep the map's.*
* **SHELL-007** (partial): On the setup of a custom map the map's own settings apply until USE MAP SETTINGS is unchecked; then the number of kingdoms, game type, victory condition, difficulty, starting resources, No Fog and Explore can be changed. *Each option has a "map" choice instead of the one checkbox; the number of kingdoms, the game type and the victory condition cannot be changed.*
* **SHELL-010** (partial): The random map screen sets for each player an AI script (the small button by the name), the civilization, the number of kingdoms, the game type (Standard or Deathmatch), the victory condition, the difficulty, the starting resources, No Fog and Explore. *The AI script, the computer kingdoms' civilizations, the game type and the victory condition cannot be chosen.*
* **SHELL-011** (partial): A random map has a size (Medium, Large, Huge, Enormous, Real World), a type (Random, Archipelagos, Dry, Island, Continental, Lakes), a terrain set (Grass, Desert, Summer, Winter) and map resources (Low, Normal, High), and may have a guarded town, animals, gateways, magic items and cliffs. *Size, type and terrain set are offered; map resources and the five options are not on the screen.*
* **SHELL-012**: The settings of the random map screen are kept for the next time.
* **SHELL-013**: The random map screen and the multiplayer options let each computer kingdom resign on its own (a flag) or never (a sword).
* **SHELL-014** (partial): The load game screen lists the saved games, filtered by campaign or single player game, and loads or deletes the chosen one. *LOAD GAME loads the last quick save (F5) of a campaign mission; there is no list.*
* **SHELL-016** (partial): In the multiplayer setup each player has a ready flag (red or green), a colour, an AI script, a civilization and a team; settings can only change while not ready; there is a chat; only the creator has PLAY, active when everyone is ready. *The colour and the AI script cannot be chosen.* (ADR 0168)
* **SHELL-017**: Only the player himself chooses his AI script in the multiplayer screen, and every player is told when the host changes one.
* **SHELL-018** (partial): The multiplayer settings choose a random or a custom map, the random map's options, the game type, the difficulty, the victory condition, the starting resources, No Fog, Explore, the population limit, and day and night and weather; with MAP SETTINGS only No Fog, Explore, day and night and weather can change. *The game type, the victory condition and the random map's resources and options cannot be set.* (ADR 0168)
* **SHELL-019** (partial): The preferences set the sound volume (off when unchecked) with unit speech, building sounds and nature sounds kept or not, the music volume or CD audio, the game speed, the scroll speed and the path depth, day and night and weather effects, and when the menu bar appears. *CD audio and when the menu bar appears are missing; the path depth is dropped on purpose (TDD ORIG-03).*
* **SHELL-020**: The credits screen shows the makers of the game. *CREDITS prints where the credits file is.*
* **SHELL-021** (partial): The in-game menu (F1, F10) offers instructions, load game, save game, options, restart, quit and back to game. *Instructions, load game and save game are missing; F2 quick-saves a campaign mission.*
* **SHELL-022** (partial): F2 opens the save dialog and F3 the load dialog. *F2 and F3 quick-save and quick-load a campaign mission; there are no dialogs and no saves of other games.*
* **SHELL-026**: The controls of the menu screens explain themselves in a hint line.
* **SHELL-027**: Quitting, resigning and disconnecting ask for confirmation.

### Technologies and upgrades

* **TECH-019** (partial): Landing Tactics lets ships carry 50 % more troops, once. *Tzar2 adds 4 places (placeholder); the help gives 50 %.*
* **TECH-028** (partial): Mastery (Asians) lets priests teach kung-fu units and turn mana into experience. *Mastery gates the spell that turns mana into experience (spell_raiseexp), which works; whether priests "teaching kung-fu units" is more than that is not known.*
* **TECH-029**: Tournaments (Asians) lets kung-fu units share their experience.
* **TECH-030**: Jewelry (Asians) lets the markets sell and buy items, the same items in every market. *The markets' item menus (market_items, market_sell) are empty in MENU.INI and filled by the game; Tzar2 has no item trade.*
* **TECH-036** (partial): Each cannon level of a ship, up to level six, makes its cannons fire more often. *Tzar2 adds attack instead (placeholder, UNK-21).*

### Trade, tavern and loans

* **TRADE-002** (partial): A trade is set up by picking the starting market or harbor and its goods, the far one and its goods, and the amount, then START. *The near market is the trader's nearest own one; the far one is clicked (ADR 0103).*
* **TRADE-008**: The amount the tavern will lend shows in the bottom left corner of the view.

### Exploration and visibility

* **FOG-005** (partial): Invisible units become visible when they attack, use an item or cast a spell. *Attacking and casting reveal them; using an item does not.*

### Terrain and movement

* **WORLD-012** (partial): Each kind of unit walks at its own speed, which follows the stride of its walking animation. *18 classes are measured; the others use SPEED times 10 px/s, a placeholder, until their strides are read (ADRs 0172, 0174).*
* **WORLD-013** (partial): A random map grows as many trees as its terrain set's data gives for its size. *Tzar2's generator uses its own forest density (ADR 0060).*
* **WORLD-015** (partial): A unit cannot stand where another unit stands. *Units are pushed apart softly with a collision radius of 7 px (UNK-11); walking or fighting in a crowd they come within 8 px of each other, while the original's units have a 25 px selection box and a 32 px object box (work queue).*

## Unclear

The sources disagree or say too little.

### Computer opponents

* **AI-003**: The computer does not repair its damaged towers. *The guide describes one campaign mission; whether it is the AI's general behaviour is not known.*

### Construction and repair

* **BLD-013**: Peasants under a spy's spell cannot build. *The patch note says "obsessed by spies"; what it means is not known, and spies cannot impersonate yet (MAG).*
* **BLD-020**: Deleting a building just after its construction began gives its cost back. *Tzar2 refunds a cancelled site in full at any time (placeholder); "at the beginning" suggests the original refunds only early, or less later.*
* **BLD-022**: How long a repair takes. *Tzar2 lets one peasant repair a building fully in its construction time, and each further peasant adds less (100, 75, 50, 25 %; UNK-18). Five peasants mended a bridge from almost nothing in 22 s, which looked instant while they stood still (BLD-021); whether the original repairs more slowly is to be confirmed.*

### Selection, mouse and pointers

* **CTRL-023**: The image of the building being placed is green where it can be built. *After the owner's first play-test the ghost has no fill and only the cells that block it turn red (CTRL-018); whether the original tints the whole image green is to be confirmed by the owner.*
* **CTRL-028**: Shift+click on an order button of a building queues five. *Tzar2's assumption from the hotkey checklist; no source of the original says so.*

### Resources and gathering

* **ECO-002**: Gathering broken off before the load is complete gives nothing. *Tzar2 drops the work done (ADR 0180); a capture of an interrupted gather would settle it (UNK-17).*
* **ECO-014**: The storage takes gold and iron for the Europeans, gold and stone for the Asians, and iron, gold and stone for the Arabians. *Tzar2 follows the manual, but the European storage researches Stone Mining, which suggests it takes stone too.*
* **ECO-018**: Asian peasants do not tend cows or carry milk. *The manual's list of the Asian peasant's work leaves out milk and cows, though the Asian farm makes cows; Tzar2 lets any peasant milk.*

### The screen

* **HUD-031**: Selection marks and the selection box are drawn in the terrain set's selection colour (white in every set). *Tzar2 draws own units green, enemies red and Nature yellow, and the box green; whether the original colours them by owner is to be confirmed.*

### Magic, religion and spies

* **MAG-008**: Hypnotism (Asian, from Concentration) is a spell of the Asian mages. *Neither the manual nor the game's text says what it does; Tzar2 makes it hold the unit like freeze.*

### Production and population

* **PROD-002**: A right click on an order button takes one unit of that kind off the queue and refunds it. *Tzar2's choice (ADR 0180), from the original's per-button counts; whether the original cancels this way is not confirmed. Tzar2 also queues five with Shift+click, likewise unconfirmed.*

### Technologies and upgrades

* **TECH-006**: Rotating Fields and Irrigation (Asians) make fields give more food. *The help says they raise the amount of food a peasant carries; Tzar2 makes farming faster instead, by a placeholder (UNK-21).*
* **TECH-010**: Saw, Stone Mining, Gold Mining and Ore Flotation (Arabians) make gathering wood, stone and gold more productive. *The help says they raise the amount a peasant carries; Tzar2 makes gathering faster instead, by a placeholder (UNK-21).*
* **TECH-023**: Trade Taxes makes the markets produce resources over time. *The manual says the markets produce resources for a while; the game's own text says it increases trade profit. Tzar2 adds 10 % to the markets' income (placeholder).*
* **TECH-024**: Banking (Asians) adds resources to the treasury over time. *The manual says it adds to the treasury from time to time; the game's own text says it increases trade profit. Tzar2 adds 10 % to the profit at markets (placeholder); until 2026-10-21 it targeted the castle and did nothing.*
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

[data-usage.yaml](data-usage.yaml) accounts for 32 files or groups of files: 4 read whole, 7 not read, and of the names in the others 142 used, 4 restated by hand, 18 not needed and 48 to do.

To do:

* `DATA/CREDITSFNT.INI` (the whole file): the font of the credits screen (SHELL-020).
* `DATA/EQUATIONS.INI` `enablequestbutton`: when the quest order is on (HUD-029)
* `DATA/IMAGES.INI` `actionrow`: the frame of an attack at which the blow lands (WAR-014)
* `DATA/IMAGES.INI` `move_len`: the stride of a walk, for the walking speed of the classes not measured (WORLD-012)
* `DATA/IMAGES.INI` `startcol`: animations that start inside their sheet (PRES-008)
* `DATA/IMAGES.INI` `startrow`: animations that start inside their sheet (PRES-008)
* `DATA/LAYOUT.INI` `layout1024l`: the interface down the left side (HUD-009)
* `DATA/LAYOUT.INI` `layout1024r`: the interface down the right side (HUD-009)
* `DATA/OBJECTS/*.INI` `buildicon`: meaning not decoded
* `DATA/OBJECTS/*.INI` `icon_hero`: the portrait of a hero (PRES-006)
* `DATA/OBJECTS/*.INI` `image_attack2`: other attack swings (PRES-005)
* `DATA/OBJECTS/*.INI` `image_attack3`: other attack swings (PRES-005)
* `DATA/OBJECTS/*.INI` `image_capture`: the peasant's capture animation (PRES-005)
* `DATA/OBJECTS/*.INI` `image_meat`: the peasant's meat collecting animation (PRES-005)
* `DATA/OBJECTS/*.INI` `image_murder`: the ninja's murder animation (PRES-005)
* `DATA/OBJECTS/*.INI` `image_pray`: the priest's prayer animation (PRES-005)
* `DATA/OBJECTS/*.INI` `image_spell`: the casting animation of mages and priests (PRES-005)
* `DATA/OBJECTS/*.INI` `sound_create`: the sound of a unit being made (PRES-007)
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
* `IMAGES/TERRAIN/*.TERRAIN.INI` `minidatargb`: the minimap's colours of terrains, trees and cliffs (HUD-030)
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
