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
| [Computer opponents](ai.yaml) (AI) | 4 | 3 | 0 | 0 | 0 | 1 | 0 |
| [Capture](capture.yaml) (CAP) | 4 | 3 | 1 | 0 | 0 | 0 | 0 |
| [Combat](combat.yaml) (WAR) | 14 | 10 | 4 | 0 | 0 | 0 | 0 |
| [Construction and repair](construction.yaml) (BLD) | 22 | 18 | 4 | 0 | 0 | 0 | 0 |
| [Selection, mouse and pointers](controls.yaml) (CTRL) | 29 | 19 | 10 | 0 | 0 | 0 | 0 |
| [Diplomacy and victory](diplomacy.yaml) (DIP) | 9 | 8 | 1 | 0 | 0 | 0 | 0 |
| [Resources and gathering](economy.yaml) (ECO) | 24 | 19 | 5 | 0 | 0 | 0 | 0 |
| [Experience and heroes](experience.yaml) (EXP) | 7 | 6 | 0 | 0 | 0 | 0 | 1 |
| [Holders and transport](holders.yaml) (HOLD) | 9 | 9 | 0 | 0 | 0 | 0 | 0 |
| [The screen](interface.yaml) (HUD) | 34 | 14 | 15 | 1 | 1 | 2 | 1 |
| [Items](items.yaml) (ITEM) | 19 | 14 | 3 | 0 | 0 | 1 | 1 |
| [Magic, religion and spies](magic.yaml) (MAG) | 28 | 28 | 0 | 0 | 0 | 0 | 0 |
| [Nature, gateways, day and weather](nature.yaml) (NAT) | 6 | 6 | 0 | 0 | 0 | 0 | 0 |
| [Unit orders](orders.yaml) (ORD) | 18 | 13 | 5 | 0 | 0 | 0 | 0 |
| [Animations, effects and sounds](presentation.yaml) (PRES) | 9 | 5 | 3 | 1 | 0 | 0 | 0 |
| [Production and population](production.yaml) (PROD) | 15 | 10 | 4 | 0 | 0 | 1 | 0 |
| [Scenario scripts](scenario.yaml) (SCN) | 2 | 2 | 0 | 0 | 0 | 0 | 0 |
| [Menus, setup and pause](shell.yaml) (SHELL) | 28 | 15 | 10 | 1 | 0 | 2 | 0 |
| [Technologies and upgrades](technology.yaml) (TECH) | 36 | 32 | 3 | 0 | 0 | 0 | 1 |
| [Trade, tavern and loans](trade.yaml) (TRADE) | 10 | 9 | 1 | 0 | 0 | 0 | 0 |
| [Exploration and visibility](visibility.yaml) (FOG) | 7 | 6 | 1 | 0 | 0 | 0 | 0 |
| [Terrain and movement](world.yaml) (WORLD) | 18 | 11 | 5 | 0 | 0 | 2 | 0 |
| **All** | 352 | 260 | 75 | 3 | 1 | 9 | 4 |

## Not done yet

Missing and partial rules.

### The screen

* **HUD-009**: The interface can be placed along the bottom, the left or the right of the screen. Work: STATUS work queue 12 (M8, owner decision 2026-10-21).
* **HUD-028** (partial): The \ key shows the trees translucent so that units under them can be seen. *Trees are drawn at 45 % opacity, not with the original's look; neither the data nor ReTzar says how the original does it.* Work: UI review screenshot 8 (docs/reviews/2026-10-ui-review.md).

### Animations, effects and sounds

* **PRES-005** (partial): Units have their own animations for capturing and collecting meat (peasants), murder (ninjas), prayer (priests), casting (mages and priests) and other attack swings (footmen, macemen, peasants, bears, ninjas). *Done - capturing and cutting meat (peasants), casting (mages and priests, image_spell; the Asian priest's prayer is the same picture) and the second swing on half the blows (footmen, macemen, peasants, bears, ninjas, priests; the original draws at random, 0x4e5720 by ReTzar's reading). the ninja's murder (image_murder, else the attack's; ADR 0268). Left - the Asian peasant's third swing under Kung Fu.*

### Menus, setup and pause

* **SHELL-019** (partial): The preferences set the sound volume (off when unchecked) with unit speech, building sounds and nature sounds kept or not, the music volume or CD audio, the game speed, the scroll speed and the path depth, day and night and weather effects, and when the menu bar appears. *The checks by the sound and music volumes turn them off, as in the options' art; CD audio is shown and stays off (Tzar2 plays MIDI); the hardware cursor is the system's pointer. When the menu bar appears is missing, the path depth is dropped on purpose (TDD ORIG-03), and where weather and day/night sit waits for the owner's screenshot (UI review question 3).*

## Unclear

The sources disagree or say too little.

### Experience and heroes

* **EXP-005**: Very experienced units become heroes and can be named. *Units become heroes at level 12 with Heroism and take the next name of HEROES.INI, as ReTzar reads the original's promotion (0x506d60); Tzar2 does the same. Neither the original's data nor ReTzar show a way for the player to type a name, so the manual's "can name" may mean only this; a play-test on the owner's PC (select a level-12 hero, click its name or press Enter) settles it.*

### The screen

* **HUD-031**: Selection marks and the selection box are drawn in the terrain set's selection colour (white in every set). *Tzar2 draws own units green, enemies red and Nature yellow, and the box green; whether the original colours them by owner is to be confirmed.* Work: UI review screenshot 9 (docs/reviews/2026-10-ui-review.md).

### Items

* **ITEM-015**: The random items of a map take their strength from the data (treasure, weapons, armour, potions). *The original's random map generator does not read these sections (ReTzar 0x4b5c50 makes its items with ranges of its own; work queue 17); what reads them is not known.*

### Technologies and upgrades

* **TECH-031**: Piracy gives boarding units an attack bonus, and Boarding Ram lets ships board others. *The manual and the game's texts name them, but the original's data has no such technologies: they look cut from the game.*

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

[data-usage.yaml](data-usage.yaml) accounts for 32 files or groups of files: 4 read whole, 4 not read, and of the names in the others 162 used, 5 restated by hand, 22 not needed and 24 to do.

To do:

* `DATA/LAYOUT.INI` `layout1024l`: the interface down the left side (HUD-009)
* `DATA/LAYOUT.INI` `layout1024r`: the interface down the right side (HUD-009)
* `DATA/OBJECTS/*.INI` `buildicon`: meaning not decoded
* `DATA/OBJECTS/*.INI` `image_murder`: the ninja's murder animation (PRES-005)
* `DATA/OBJECTS/*.INI` `image_pray`: the priest's prayer animation (PRES-005)
* `DATA/TREES.INI` `bushes`: where the bushes are in the tree sheet (PRES-009)
* `DATA/TREES.INI` `chopped`: where the stumps are in the tree sheet (PRES-009)
* `DATA/TREES.INI` `img_shadows`: the trees' shadow sheet (PRES-009)
* `DATA/TREES.INI` `img_trees`: the tree sheet (PRES-009)
* `DATA/TREES.INI` `pos_shadow`: where a tree's shadow sits (PRES-009)
* `DATA/TREES.INI` `pos_tree`: where a tree sits on its tile (PRES-009)
* `IMAGES/TERRAIN/*.IMAGES.INI` `ruinpoints`: meaning not decoded
* `IMAGES/TERRAIN/*.TERRAIN.INI` `colors`: the colour of selection marks (HUD-031)
* `LOCAL/STRINGS.INI` `adds`: messages such as "Build more houses" (HUD-026)
* `LOCAL/STRINGS.INI` `defmsg`: the hint line's description of orders (HUD-001)
* `LOCAL/STRINGS.INI` `msg`: the game's messages in the original's words (HUD-026)
* `LOCAL/STRINGS.INI` `peons`: a working peasant named by its job, woodcutter, farmer, miner, builder (HUD-032)
* `LOCAL/STRINGS.INI` `wnd_message`: the message window's button (HUD-026)
* `LOCAL/STRINGS.INI` `wnd_quests`: the quest window's texts (HUD-026)
* `LOCAL/STRINGS2.INI` `chooseai`: the AI script dialog (SHELL-010)
* `LOCAL/STRINGS2.INI` `vm`: messages such as a time limit hit or a location sent (HUD-026)
* `LOCAL/STRINGS3.INI` `defmsg`: the hint line's description of orders (HUD-001)
* `LOCAL/STRINGS3.INI` `msg`: the game's messages in the original's words (HUD-026)
* `LOCAL/STRINGS3.INI` `vm`: messages such as sending results (HUD-026)

<!-- END t2 rules report -->
