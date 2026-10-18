# 0050. Parse the legacy AI DSL into a flat rule program and run it in a budgeted, integer-only VM

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 part B)
* **Related:** TDD §7.2, §16.1 (REQ-AI-01, REQ-AI-03, REQ-AI-04), §22, Appendix D, REQ-ARCH-01, UNK-35, UNK-36, UNK-41,
  RE-07; ADR 0051 (the AI in the lockstep), ADR 0052 (executors); docs/mechanics/ai-placeholders.md

## Context

REQ-AI-01 asks to parse and execute the original AI scripts (`DATA/AI`: 37 files, `*.ais` strategy scripts that include
`*.ai` modules). Appendix D has a draft grammar and inferred semantics; most of the semantics are unknown until RE-07
measures the original (UNK-35). The original files never enter the repository: the importer stores them in the user's
base package (`text/data/ai/*`, docs/formats/t2pkg.md) and tests read them from `TZAR2_ORIGIN`. User and mod scripts are
untrusted input. The AI runs on every peer (ADR 0051), so its execution must be deterministic, integer-only and
budgeted.

## Decision

### Syntax (`Tzar2.AI.Legacy.Syntax`)

* A hand-written lexer and recursive-descent parser produce an immutable AST with 1-based line/column locations
  (`AiScriptSyntax`, `AiRuleItem`, clauses `when`/`have`/`trigger`/`forget`/`say` in source order).
* Grammar as in Appendix D.3, with what the corpus needs: keywords are case-insensitive (`CRITICAL`, `THIS`, `this`);
  a name right after `rule` that is not a clause keyword is the rule name (one module names a rule after a technology
  and gives it no condition); in a `when` list, `trigger X` after a comma is a condition, without a comma it
  starts the `trigger` clause; `random N` takes an optional `%`; `extern "…"` holds an expression of the trigger language
  (Appendix C.6, parsed by `AiExpressionParser`; `RESIGN.AI` uses it); `!` clears a flag; `;` comments, tabs, CR/LF.
* **Errors** are `AiDiagnostic(severity, code, location, message)`, printed `file(line,col): error AI1005: …`. Codes:
  `AI1xxx` lexing/parsing, `AI2xxx` loading, `AI3xxx` binding. The parser never throws on malformed input; it reports
  and resynchronises at `end`, `rule`, `critical`, `include`, `{` or `}`, so one run lists all problems.
* **Limits** (`AiParseLimits`): 1 MiB per file, block and expression nesting 64 (deeper blocks are skipped without
  recursing), include depth 16, 4,096 include expansions and 262,144 expanded items (rules, blocks, includes; both bound
  diamond-shaped include graphs, which are exponential otherwise), 65,536 rules, 64 parts per rule (conditions, goals,
  flag changes, forgets and says together), 64 rules per name, 256 nodes and 200 levels per `extern` expression,
  1,024-character strings, 200 messages. Include names may not leave the AI folder (`..`, drives). The corpus is far
  below all of them. Together they bound the cost of any single rule, so the per-tick budget holds for hostile scripts.

### Programs (`Tzar2.AI.Legacy.Program`)

* `AiCompiler` loads a strategy script through an `IAiScriptSource` (no I/O in Tzar2.AI, REQ-ARCH-01; the game passes
  a view of the VFS, tools and tests pass texts) and expands includes **textually**: the included items are spliced
  into the current block. Names are normalised (lower case, `/`, `DATA/AI/` prefix dropped). A missing include is a
  warning and is skipped (Appendix D.6); a cyclic include is skipped with a warning; each is reported once.
* The result is a flat `AiProgram`: rules in evaluation order, each with its block end (where a failing `critical` rule
  jumps), contiguous condition and action ranges, and interned tables (symbols, flags, rule names, texts, civilization
  names, extern expressions) in order of first appearance. It is ruleset-independent, immutable and shared by every AI
  player using the script; `ContentHash` (xxHash64 of code and tables) ties saved AI state to the program.
* `AiIni` parses `AI.INI` `[scripts]` (display name → script); the defaults are `random.ais` (skirmish) and `dm.ais`
  (deathmatch), Appendix D.1.

### Binding (`Tzar2.AI.Runtime.AiBinding`, `AiRulesetModel`)

* Names are resolved once per (program, ruleset, civilization) to object classes (the original class keys, e.g.
  `barrack`; plural forms `-s`, `-es` and `-men`→`-man` are accepted), resources, `units`, technologies (`te_*`), and
  the pseudo-objects `convert`, `needs_locked`, `resign`. Unknown names are warnings (`AI3001`): they count 0 and their
  goals are ignored; a class without a type of the player's civilization makes its goals ignored too.
* **The AI consumes the compiled ruleset** (REQ-ARCH-01: Sim, Scripting and AI "consume only compiled `Ruleset` and
  `MapData` objects"), which it reaches through Sim (`World.Ruleset`). Its project still references only Core and Sim
  (§7.2), but its IL references `Tzar2.Content`; `Tzar2.Architecture.Tests` allows exactly the namespace
  `Tzar2.Content.Compiled` for it (a metadata check of every type reference), never the VFS, packages or the rules
  compiler.

### Execution (`AiInterpreter`)

* A **pass** walks the rules top to bottom. A rule is active when all its conditions hold (left to right,
  short-circuit: a `random` after a false condition draws nothing). An active rule runs its actions in source order:
  flag changes take effect at once (later rules of the pass see them), `forget NAME` disables every copy of the named
  rule, `forget THIS` the rule itself, `say` speaks only when the rule becomes active, and each `have` goal is checked
  and, if unmet, collected. A `critical` rule that is inactive or has an unmet goal jumps to its block end.
* Rules re-run their actions in every pass while active (**level** semantics): the corpus' reset-and-recompute idioms
  (a bare rule clears a flag, the next rules set it again from the current state; all of `RESIGN.AI` and `BONUS.AI`)
  need it.
  Goals are therefore **maintained**: losses are rebuilt because the goal is collected again.
* **Percentages** are shares of the player's population limit (a 40 % peasant goal = 80 peasants at limit 200; goals
  round up). With shares of the current units, `START.AI` deadlocks: its gate on ten peasants is never reached with its
  20-30 % peasant goals, and `dm.ais` (5 % peasants, 65 % army) would never fill the population.
* **`needs_locked`** turns the resource goals before it in the pass that are still unmet into a lock: goals after it may
  not spend those resources (until the stock reaches the goal). Workers are exempt so that saving does not stop the
  economy that saves.
* **Technologies** come from the players' research state (GAME-06, ADR 0040; `AiTechnologies.Sim`): a queued research
  is not yet researched, and an unmet `have te_*` goal is collected for the research executor (ADR 0052). A technology
  the ruleset does not have counts as researched, in conditions and goals, so a script's gate on it stays open. `convert` (tavern) is collected but blocks
  nothing; `resign` sets `ResignRequested` when the lobby's "AI resign" flag allows it.
* **Integers only.** `random N[%]` draws `NextInt(100) < N` from the player's `ai[p]` stream; `time N` compares game ms;
  `extern` expressions are 32-bit saturating, division by zero is 0, `$aik` is the 1-based kingdom, unknown names are 0
  (UNK-41).
* **Budget.** A pass costs one instruction per rule visit, condition and action, plus the fan-out of `forget NAME`
  (the rules it disables) and 1 + nodes/8 per `extern`; an AI may execute
  `InstructionBudgetPerTick` (512) per tick and continues the pass in the next tick. Goals become the executors' goals
  only when the pass completes. A pass starts every `ThinkIntervalTicks` (20 = 1 s, like the triggers), staggered by
  player. `random.ais` compiles to 933 rules, about 3,000 instructions: six ticks per pass.
* **Difficulty** is visible to scripts only (`difficulty`). The original data gives the AI no bonus at any level and
  AI.INI has no such setting (UNK-36 open); the levels differ only through the scripts (`fstart.ai`, `bonus.ai`,
  `multiply.ai`).
* `AiDebugView` prints, per AI player, the pass state, stock, workers, set flags and the goals with their rule
  locations (the debug overlay of Appendix D.6).

## Consequences

* With `TZAR2_ORIGIN`, every original file parses with zero errors (37 files, 859 rules) and every strategy script
  compiles with zero messages; `random.ais` and `dm.ais` play skirmishes at every difficulty on the synthetic ruleset.
  `t2 ai check <folder|package> [--bind]` gives modders the same report.
* Every semantic choice not in the data is marked UNVERIFIED in docs/mechanics/ai-placeholders.md and is a single,
  tested place in `AiInterpreter`/`AiBinding`; RE-07 replaces them with measurements.
* Property tests feed random text, token soups and mutated scripts through compile, bind and a short match; nothing
  throws and work stays linear.

## Alternatives considered

* **Interpret the AST directly:** simpler, but a budgeted pass has to resume mid-rule-list; a flat program with a
  program counter is trivially resumable and serialisable.
* **Edge-triggered actions** (a rule acts once when it becomes active): makes `START.AI`'s bare flag-clearing rule an
  initialisation, but leaves `RESIGN.AI` and `BONUS.AI` inert after the first pass. Level semantics plus percentages
  of the population limit make all of them work; `say` alone is edge-triggered (otherwise `TALK.AI` would repeat every
  message every second).
* **Percent of the current units:** self-referential compositions that never fill the population, and the `START.AI`
  deadlock above.
* **Treat technologies the ruleset lacks as missing:** the Arabian common module gates most of itself on a critical rule that
  waits for a technology, so the AI would stall until GAME-06 lands.
