# Appendix D — Legacy AI script DSL

Derived from the scripts in `DATA/AI` (38 files). The semantics below are **inferred from usage**. Each ⚠ item is part of UNK-35 and must be confirmed by experiments in the original (RE-07).

## D.1 Files and selection
* `AI.INI` `[scripts]`: display name → strategy script, e.g. `Magic = Magic.ais`, `Magic, Religion = Magicrel.ais`, `War, Magic = Warmagic.ais` (one per pair of development paths). `;Deathmatch = Dm.ais` is commented out but `dm.ais` is referenced by the exe for Deathmatch games.
* `*.ais` = top-level strategy scripts (`RANDOM.AIS` is the default skirmish script; `DEF.AI` is just `include "random.ais"`).
* `*.ai` = include modules: `E_*`/`A_*` per civ (`*_COMMON`, `*_MAGIC`, `*_RELIGION`, `*_TRADE`, `*_WAR`), `HOUSES.AI`, `MULTIPLY.AI`, `GATEWAY.AI`, `BONUS.AI`, `FSTART.AI` (hard-difficulty start), `START.AI`, `RESIGN.AI`, `TALK.AI`, Arabian `R_*` modules (`R_COMMON`, `R_MAGIC`, `R_RELIGION`, `R_TRADE`, `R_WAR`, `R_ARMY.AIS`). The original build ships 38 AI files in total, including `RANDOM.BAK`.
* Maps choose a script per kingdom plus “Economy AI” and “War AI” settings (Appendix C.1).

## D.2 Lexical
* Comments start with `;` and run to end of line. Whitespace and newlines are insignificant (rules may span lines).
* Identifiers: object classes (`footman`, `heavy_cavalry`, `te_architecture`, `magicgateway`), plurals accepted (`houses`, `farms`, `barracks`, `knights`, `catapults`, `peons`, `cows`, `fields`) ⚠ plural normalization rules; resource names (`gold`, `stone`, `wood`, `food`); pseudo-objects (`units` = total units, `convert`, `needs_locked`).
* Numbers: integers, optionally with `%`.
* Strings: `"…"` (used by `say` and `include`).
* Keywords (frequency in corpus): `rule` (805), `end` (810), `when` (582), `have` (489), `trigger` (232), `say` (164), `critical` (163), `include` (62), `not` (61), `enemy` (33), `civ` (22), `difficulty` (14), `random` (11), `time` (9), `forget`, `THIS`.

## D.3 Grammar (draft)
```
script      := item*
item        := include | rule | block
include     := 'include' STRING                                   // textual include, relative to DATA\AI
block       := '{' item* '}'
rule        := ['critical'] 'rule' [NAME] clause* 'end'
clause      := 'when' condlist
             | 'have' goallist
             | 'trigger' flaglist
             | 'forget' (NAME | 'THIS')
             | 'say' STRING
condlist    := cond (',' cond)*
cond        := ['not'] atom
atom        := ['enemy'] OBJECT [NUMBER ['%']]                     // object count (own, or enemy) ≥ n (or ≥ n% of …?)
             | RESOURCE NUMBER                                     // gold 300, wood 10000
             | 'units' NUMBER                                      // total own units ≥ n
             | 'time' NUMBER                                       // game time ≥ ms
             | 'random' NUMBER '%'                                 // probability
             | 'difficulty' ('easy'|'normal'|'hard')
             | 'civ' ('european'|'asian'|'arabian')
             | 'trigger' FLAG                                      // flag is set
             | TECH                                                // tech researched
goallist    := goal (',' goal)*
goal        := [NUMBER ['%']] OBJECT                               // have 10 footman / have 40% peon / have castle
             | NUMBER RESOURCE                                     // have 1000 gold (accumulate)
             | NUMBER 'convert'                                    // tavern conversion
             | TECH                                                // research
             | 'needs_locked'                                      // ⚠ meaning
flaglist    := ['!'] FLAG (',' ['!'] FLAG)*                        // !Flag clears the flag
```

## D.4 Inferred semantics (⚠ verify)
* **Rule:** when all `when` conditions hold, the AI pursues the `have` goals (build/train/research/accumulate until the count is reached). Goals are probably **maintained** (rebuilt after losses) rather than one-shot. `trigger` sets or clears named flags (persistent until cleared). `say` posts a chat message (debug/taunt). `forget X` disables rule `X`; `forget THIS` disables the current rule.
* **Named rules:** `rule E_Storage when … end` makes the rule addressable by `forget`.
* **Blocks and `critical`:** inside `{ … }`, rules are evaluated in order. A `critical` rule acts as a **gate**: if its conditions are false, or its goals are not yet achieved, the remaining rules of the block are skipped. Nested blocks inherit the gating. Example: `{ critical rule have blacksmith end  rule have te_temperiron end }` researches Temper Iron only once a blacksmith exists.
* **Percentages:** `have 40% peon` = ⚠ peasants as a share of total population; `when peon 20%` = ⚠ the share condition.
* **Rule order matters** (top to bottom, includes inline).
* **Evaluation cadence and per-tick budget:** unknown (measure).
* Bare `rule trigger !Flag end` lines at the top of modules reset flags (initialization idiom).

## D.5 Representative examples
```text
; maintain houses relative to unit count (HOUSES.AI)
{ critical rule when castle end
  rule when units 15 have 1 house end
  rule when units 27 have 2 houses end
}

; counter-unit logic (BONUS.AI)
rule trigger !MakeSomeFootmen end
rule when enemy pikeman 15, not enemy pikeman 25 trigger MakeSomeFootmen end
rule when trigger MakeSomeFootmen have 20 footman end

; strategy path selection (RANDOM.AIS)
{ critical rule when not trigger Path1 end
  rule when not trigger Path1, random 40% trigger Path1_War, Path1 end
}

; difficulty and conversion (RANDOM.AIS)
{ critical rule when difficulty hard, food 15000 end
  critical rule have tavern end
  { critical rule when not gold 10000 end
    rule say "Converting food to gold" end
    rule have 500 convert end
  }
}
```

## D.6 Implementation notes
* Parse into an AST and keep source positions, for error messages and the editor's AI-script view.
* The interpreter produces **goals**. Executors (TDD §16.1) turn goals into commands deterministically.
* Provide a debug overlay: per AI player, the active rules, flags, goals and the current executor tasks.
* Missing includes → a warning, and the rule set continues (compatibility with partial installs).

## D.7 Implementation (M3 part B)
* Parser, compiler, interpreter and executors: `Tzar2.AI` — [ADR 0050](../decisions/0050-legacy-ai-dsl-toolchain-and-interpreter.md)
  (grammar as implemented, diagnostics, limits, semantics), [ADR 0051](../decisions/0051-ai-in-the-lockstep.md) (the AI
  in the lockstep), [ADR 0052](../decisions/0052-ai-executors-and-default-script.md) (executors, default script).
* Every ⚠ point above has a placeholder decision in [ai-placeholders.md](../mechanics/ai-placeholders.md); notable
  differences from D.4: percentages are shares of the **population limit**, and rule actions re-run in every pass while
  the rule is active (only `say` fires once per activation).
* `t2 ai check <DATA/AI folder | package>` parses and compiles scripts and prints located messages (`AI1xxx` syntax,
  `AI2xxx` loading, `AI3xxx` names).
