# 0107. Make the hm* codes sim commands gated by the match; extend the golden scenario to the world systems

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream F)
* **Related:** TDD §10 GAME-19, §22.2, §21, UNK-30, `TZAR110.TXT` (1.06: hmnotech in MP without cheats; 1.10:
  `hmspawn magic` crash), ADRs 0039, 0045, 0100

## Context

The original's chat codes (`hmnext`, `hmnotech`, `hmspawn <unit>`, `hmdvaleva`, `hmpetleva`, `hmgod`, `hmbuildozer`,
`hmreveal`, `hmnofog`; community list, UNK-30 — the executable's strings are not readable) must become debug commands
that replay deterministically, allowed only in a sandbox or when every player agreed, never in ranked matches.

## Decision

* **Commands.** `Cheat` (0xF010, Arg0 = `CheatCode`) and the existing `DebugSpawn`/`DebugKill` need
  `SimulationConfig.CheatsAllowed` and refuse `Ranked` matches (`Ranked` with cheats is an invalid configuration).
  Codes: reveal (explore and record everything), No Fog, no technology requirements, invulnerability (hits do nothing),
  instant construction/training/research (toggles), resources (`world.cheatSmallResources` / `cheatLargeResources` of
  each, UNVERIFIED), win (`hmnext`). Switches live in `KingdomState.Cheats` (snapshot-validated: only when cheats are
  allowed).
* **Chat.** `CheatCodes.TryParse` (Tzar2.Game) turns a line into the command; `hmspawn` takes a key or class name and a
  count at the given point; unknown names are ignored (the 1.10 crash). `GameSession.SubmitChat` submits it; the lobby's
  agreement sets `MatchSetup.CheatsAllowed`.
* **Golden scenario.** The gameplay bot now also allies kingdoms 0 and 1 (vision, resources, victory), sends tributes,
  turns kingdom 2 neutral towards 3, runs caravans between two markets per kingdom (0 and 1, under `hmnotech`), and the
  map has a magic gateway (claimed by the armies), bears and wild cows; Standard victory is evaluated. Pinned: gameplay
  ruleset `EDB4DB94B89278CA`, stream `56EA1F5070968572`, final `E74B6E5360B3B2C0`; CI gameplay bench `91DEE35A0A7E65DA`;
  synthetic movement `BC950AE8AB330411` / `A9EC6EED34BA1812` (layout only).

Measured (Apple M5, macOS arm64, Release, .NET 10, 4 workers, shared machine):

| Scenario | Result | Budget |
|---|---|---|
| synthetic, 8 kingdoms, ~4,000 units, 512² (nightly gate) | p99 6.77 ms (7.27 ms with visibility switched off: noise) | p99 ≤ 12 ms |
| gameplay, 8 kingdoms (nightly gate), 4,171 entities at the end | p99 3.41 ms, max 9.0 ms | p99 ≤ 12 ms |

## Consequences

* The client has no chat box yet (multiplayer, M6); the parser and `SubmitChat` are ready for it.
