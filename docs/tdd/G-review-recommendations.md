# Appendix G — Review recommendations (proposed additions)

**Status: PROPOSED, not binding.** These items come from the 2026-10-11 review of the TDD against common practice in remasters and remakes (AoE II DE, StarCraft Remastered, Diablo II Resurrected, and engine reimplementations such as OpenRA). They cover topics the TDD does not specify yet. Each item becomes binding when the product owner accepts it, or through the default of Q-08 if the question stays unanswered. Accepted items are then moved into the TDD body with proper `REQ-*` IDs.

Priority: **H** = should be decided before M0 ends; **M** = before the milestone listed; **L** = post-1.0 candidate.

## G.1 High priority

### G-01 Command feedback and input latency (“feel”) — H, M2/M6
**Gap:** lockstep input delay (100–400 ms in MP, §19.2) makes controls feel sluggish unless the game reacts instantly. The TDD covers hotkey parity but not response feel.
**Proposal:**
* Single-player and replays use an input delay of 1 tick.
* In MP, local **immediate feedback** at the moment of the click, independent of the lockstep delay: the move/attack marker, unit acknowledgement voice, cursor flash, command-card button state. The sim effect follows when the command executes.
* Targets: click → visual/audio feedback ≤ 1 frame; click → unit starts moving: SP ≤ 100 ms, MP ≤ input delay + 1 tick.
* Scroll speed/acceleration, selection-box behaviour and double-click timing are measured from the original and added to the fidelity suite (RE-09).

### G-02 Standalone content and “no-original” mode (total conversions, expansions) — H, M1
**Gap:** the package model allows several `kind: base` packages, but nothing *requires* the engine to work without the imported original. That ability is needed for:
* app-store approval (RISK-07);
* public CI;
* total conversions and standalone expansions (product goal 3);
* a playable first-run experience before import.

**Proposal:**
* REQ: no engine code or engine-default content references original asset keys. The imported original is just one `base` package.
* Ship a small CC0 **starter package** (placeholder units and terrain, one tutorial skirmish map), so a fresh install is playable and testable.
* Total-conversion mods declare their own `base` package and do not depend on the original.

### G-03 Save-game versioning and campaign-progress migration — H, M4
**Gap:** replays are pinned to the engine version (§19.4), but nothing says what happens to saves and campaign progress after a game update. Without a policy, each patch can wipe long campaign runs.
**Proposal:**
* Snapshots carry a schema version with forward migrations, so saves load across all patch releases within a minor version.
* Campaign progress (finished missions, persistent heroes, carried variables) lives in a separate, stable, human-readable **profile** file. It always survives updates; at worst a mission restarts from its checkpoint.
* A migration test corpus of saves from previous releases runs in CI.

### G-04 Release engineering and live operations — H, M6/M8
**Gap:** the TDD defines CI but not releasing, updating and operating the product.
**Proposal:**
* Versioning: SemVer for the game, plus an explicit **network protocol version** and `engineApi`, checked in the lobby.
* Release channels (stable/beta), a changelog, hotfix and crash-triage process.
* Code signing: Windows Authenticode, macOS notarization, Android keystore, iOS provisioning. Keys are stored in CI secrets, never in the repo.
* Updates: store-managed where available; an update notifier for GitHub/itch builds.
* Server operations for relay/lobby: who hosts, monitoring and alerting, cost, abuse handling, an incident process (new open question).
* Store compliance: age ratings (IARC/PEGI/ESRB) and privacy labels.

### G-05 Privacy and compliance — H, M6
**Gap:** telemetry and crash reports are opt-in (REQ-PLAT-04), but there are no privacy requirements.
**Proposal:**
* GDPR-style consent, data minimization, retention limits and a deletion path; a privacy policy.
* Replays and crash bundles contain no personal data beyond the chosen player names.
* The relay hides players' IP addresses from each other (a benefit of DEC-08; state it as a requirement).

## G.2 Medium priority

| ID | Topic | Gap | Proposal | Milestone |
|---|---|---|---|---|
| G-06 | Cross-device progress | Desktop + tablet are both targets; nothing syncs between them | Abstraction `ICloudStorage` (Steam Cloud, iCloud, Google Play Saved Games) plus manual export/import of profiles/saves/replays (they are platform-independent thanks to determinism) | M7 |
| G-07 | Live “classic ↔ enhanced” presentation toggle | Remasters such as AoE DE and D2R let players switch instantly between original and enhanced visuals | A hotkey/setting that switches presentation presets (Authentic ↔ Smooth/HD pack, original ↔ widescreen HUD) without restarting. Also useful for fidelity reviews | M8 |
| G-08 | Observers/spectators | Replays exist, but there are no live spectator slots in MP | Spectator slots in the lobby (they receive the command stream and cannot send commands), optional broadcast delay | M6 |
| G-09 | Achievements, statistics, leaderboards | Requested by the Steam community; typical for re-releases | Optional, behind a platform abstraction; derived from replay-verifiable sim events | Post-1.0 |
| G-10 | In-game help parity + onboarding | The original's online help (“?” order button, `HELP/` screens, hotkey reference `HK.TXT`) is not mentioned; modern players need onboarding | Port the help system as data-driven screens; first-time contextual tips (off in Authentic preset); converted tutorial campaign | M3 |
| G-11 | Music/sound replacement packs | §14 covers MIDI + SoundFont only. Communities produce remastered soundtracks (Tzared ships an orchestral one) | `asset-pack` packages can replace music tracks (OGG) and sound categories; per-pack toggle in settings | M8 |
| G-12 | Script trust & resource limits | Maps transferred in the lobby may carry Lua; there is a CPU budget but no memory limit | Per-script memory cap; lobby shows “map contains scripts”; script errors never crash the match | M5 |
| G-13 | Community & playtesting programme | Only mentioned as a risk mitigation | Closed alpha with veteran players (active ES/RU/PL/BG communities), a public issue tracker and roadmap, a “fidelity panel” sign-off at the M3 gate | M3 |
| G-14 | Definition of Done for the implementing agent | Implicit only | Per feature: spec/ADR, unit + determinism + regression tests, perf budget respected, docs updated, no original content committed, analyzers clean | M0 |

## G.3 Low priority / post-1.0

| ID | Topic | Proposal |
|---|---|---|
| G-15 | Mod distribution | Decide between mod.io (cross-platform incl. mobile) and Steam Workshop (desktop only), or support both behind an interface |
| G-16 | Localization workflow | Translation platform (Weblate/Crowdin), pseudo-localization builds, automated font-coverage tests per language |
| G-17 | Gamepad / Steam Deck UX | Controller scheme and Steam Deck verification (currently noted as post-1.0 in §20) |

## G.4 Related open questions (also listed in TDD §27)
| ID | Question | Default if unanswered |
|---|---|---|
| Q-08 | Which recommendations G-01…G-17 are accepted into the binding TDD? | G-01…G-05 and G-14 accepted; the rest stay proposals |
| Q-09 | Who operates the public relay/lobby servers (hosting, cost, moderation)? | Community self-hosting only until decided |
