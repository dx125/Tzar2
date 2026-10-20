# 0176. Diagnostics: a log per run, match and AI traces, crash reports and a replay of every match

* **Status:** Accepted
* **Date:** 2026-10-19
* **Deciders:** product owner (tracing before the first play-test, "rather too much than too little"), maintainer
  (Claude)
* **Related:** ADR 0004 (determinism analyzers), ADR 0051 (legacy AI), ADR 0153 (trigger debugger), REQ-NET-09
  (replays), TDD §8, §22

## Context

Nobody has played the rebuild yet; the first play-tests must leave enough behind to explain what happened and why,
from a confusing AI decision to a crash. The code had no logging: the simulation, the AI, the triggers and the match
logic wrote nothing; the client printed about 55 lines through Godot. Deterministic assemblies (Core, Sim, AI,
Scripting) may not do file I/O or read the clock (T2D003, T2D006), and nothing may change a match.

## Decision

1. **A write-only trace interface in Core.** `ITraceSink` (`IsEnabled(area, level)`, `Write(area, level, tick,
   message)`), `LogLevel` (Off, Error, Warn, Info, Debug, Verbose) and `TraceArea` (Session, Commands, Events, Sim, Ai,
   Triggers, Net, Client, Content). `World.Trace` defaults to `NullTraceSink`; deterministic code checks `IsEnabled`
   before it builds a message and never reads anything back, so a match is identical with or without a sink, and a
   disabled trace allocates nothing.
2. **A log per run of the game** (`SessionLog`, Tzar2.Game): `user://logs/yyyyMMdd-HHmmss/` with `game.log` (one line
   per record: time, tick, level, area, message), crash reports and the crashed match's replay. A background thread
   writes, so the sim thread never waits on the disk; records beyond a 200,000-line queue are dropped and counted. The
   latest 2,000 lines stay in memory for crash reports. The log rotates at 100 MB; the 30 latest run folders are kept.
3. **Levels per area** from `TZAR2_TRACE` or `user://trace.txt` (`debug`, or `*=info,ai=verbose`; `#` comments). The
   default is **Debug everywhere** until the first play-tests are understood; Verbose (every hit, every tick's hash,
   unchanged AI passes) is opt-in.
4. **What is traced:**
   * *Session:* the run's header (version, OS, Godot, CPU, GPU, arguments), each match's setup (map, ruleset hash, seed,
     players with civilisation, controller and difficulty), pause, speed, a summary of every kingdom every 20 s of game
     time (population, units, buildings, stockpile, flags), the end.
   * *Commands:* every player command of every tick, in words; a rejected one with its reason.
   * *Events:* every gameplay event at a level by weight: the story at Info (buildings completed, research, captures,
     defeats, quests), details at Debug (training, deaths, deliveries, construction started), each hit at Verbose.
   * *AI:* each script pass's goals by the script's names (Debug when they changed, Verbose otherwise), every command
     each executor issues, every AI command the simulation rejects.
   * *Triggers:* every rule that fires and every action it runs; scenario faults are events at Error.
   * *Client:* screens, slow frames, saved replays, and through Godot's `Logger` (`OS.AddLogger`, Godot 4.5+) all of
     Godot's output and errors, including the exceptions Godot catches in our callbacks.
5. **Crashes:** an unhandled .NET exception writes `crash-*.txt` (exception and the latest lines) and the running
   match's replay (`crash-replay.t2rep`) into the run's folder.
6. **A replay of every match:** skirmish and multiplayer matches already kept theirs; campaign missions now do too.
   A replay with the deterministic simulation reproduces a match tick by tick, which is the strongest diagnosis there
   is.

## Alternatives considered

* **Microsoft.Extensions.Logging / Serilog:** dependencies for what a queue and a file do; deterministic assemblies
  would still need an interface of their own.
* **A static global logger:** global mutable state reachable from deterministic code; the world-owned sink keeps the
  dependency explicit and per match.
* **Structured JSON lines instead of text:** harder to read by eye, which is how the first play-tests will be
  analysed; the text format is regular enough for tools (`grep`, a later `t2 trace`).

## Consequences

* `game.log` of a 30-minute match at Debug is a few tens of MB; Verbose several times that.
* The determinism test plays a traced match at Verbose and compares the final hash with an untraced one.
* Already found by the first trace: the Hard AI's construction and economy executors order buildings onto the same
  site in one tick, and one of them is rejected (`IllegalPlacement`).
* Multiplayer (Net) and content loading still write only through Godot's output; their own records come when they are
  play-tested.
