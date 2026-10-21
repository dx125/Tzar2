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

## Commands

* `t2 rules check`: validates the chapters, the tests' rule ids and this report. CI runs it (`RulesLedgerTests`).
* `t2 rules report`: rewrites the report below.
* `t2 rules list missing partial`: prints the rules with those statuses.

## Report

<!-- BEGIN t2 rules report: generated, do not edit -->

| Chapter | Rules | Tested | Implemented | Partial | Missing | Deviation | Unclear |
|---|---|---|---|---|---|---|---|
| [Construction and repair](construction.yaml) (BLD) | 1 | 1 | 0 | 0 | 0 | 0 | 0 |
| [Selection, mouse and pointers](controls.yaml) (CTRL) | 18 | 10 | 7 | 1 | 0 | 0 | 0 |
| [Resources and gathering](economy.yaml) (ECO) | 8 | 5 | 2 | 0 | 0 | 0 | 1 |
| [The screen](interface.yaml) (HUD) | 8 | 0 | 7 | 0 | 0 | 1 | 0 |
| [Unit orders](orders.yaml) (ORD) | 3 | 2 | 1 | 0 | 0 | 0 | 0 |
| [Animations, effects and sounds](presentation.yaml) (PRES) | 4 | 0 | 3 | 1 | 0 | 0 | 0 |
| [Production and population](production.yaml) (PROD) | 5 | 2 | 1 | 0 | 0 | 1 | 1 |
| [Menus, setup and pause](shell.yaml) (SHELL) | 2 | 0 | 2 | 0 | 0 | 0 | 0 |
| **All** | 49 | 20 | 23 | 2 | 0 | 2 | 2 |

## Not done yet

Missing and partial rules.

### Selection, mouse and pointers

* **CTRL-017** (partial): An order on an object (a building to enter, repair or attack) is confirmed by an animation around the object. *Tzar2 blinks the object's selection corners three times; which animation the original plays is not identified.* (ADR 0179)

### Animations, effects and sounds

* **PRES-002** (partial): Arrows, bolts, stones, cannon balls, rockets and spells fly as the original's pictures, and stones, shells, rockets and bolts leave its explosions. *Which picture goes with which weapon, the flames' spots and the arcs are Tzar2's guesses.*

## Unclear

The sources disagree or say too little.

### Resources and gathering

* **ECO-002**: Gathering broken off before the load is complete gives nothing. *Tzar2 drops the work done (ADR 0180); a capture of an interrupted gather would settle it (UNK-17).*

### Production and population

* **PROD-002**: A right click on an order button takes one unit of that kind off the queue and refunds it. *Tzar2's choice (ADR 0180), from the original's per-button counts; whether the original cancels this way is not confirmed.*

## Deliberate differences

Tzar2 does these differently on purpose.

### The screen

* **HUD-001**: An order button's hint gives its name, its hotkey, its price and what it still requires ("Create Knight - 'K' ( Gold:70 Food:120 ) Requires Stable, Plate Armor"). *Tzar2 shows a box with the name and a hotkey key cap, the price with the resource icons (what is short in red), the time, the order's description and the requirements; the owner preferred it to the original's single line (2026-10-21).*

### Production and population

* **PROD-005**: New units never appear in a pocket shut in by walls or buildings while the building has a way out elsewhere. *Fixes a bug of the original, at the owner's request.* (ADR 0180)

<!-- END t2 rules report -->
