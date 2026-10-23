# 0262. Carry the AI resign flag in the match settings, format version 3

* **Status:** Accepted
* **Date:** 2026-10-23
* **Deciders:** agent (maintainer), under ADR 0190; coordinator (SHELL-013, 2026-10-23)
* **Related:** ADR 0162 (match settings and descriptor), ADR 0220 (computer kingdoms resign), ADR 0233 (custom maps in
  the lobby); ledger SHELL-013, DIP-009

## Context

Patch 1.09 (TZAR110.TXT) added an AI resign icon to the single player random map screen and the multiplayer options:
"The flag means that AI players may resign on their own, the sword means that they will never resign." Since ADR 0220
the computer kingdoms of random maps could always resign and those of scenario maps never could.

The flag reaches the simulation through each computer's `AiSettings.MayResign`, so every peer and every replay must
agree on it. It therefore belongs in `MatchSettings`, the lobby blob and the shared part of a `MatchDescriptor`.

An older client that receives settings with a field it does not know must refuse them with a clear message. It must not
read misaligned bytes.

## Decision

* **`MatchSettings.AiResign`** (default true: the white flag, as before) and **`MatchSetup.AiResign`**. A computer
  kingdom may resign when the map is no scenario and the flag is up (`GameSession`).
* **Format version 3.** Settings are written in the lowest version that holds them:
  * 1: no custom map and the flag up;
  * 2: a custom map and the flag up;
  * 3: the sword.

  Version 3 adds one extras byte after the speed:
  * bit 0: a custom map's fields follow;
  * bit 1: computers never resign.

  Other bits are refused. So descriptions, lobby blobs and replay headers with the flag up keep their bytes. An older
  client given the sword stops at the version check: "Unsupported match settings version". This build says that a
  newer Tzar2 made the match.
* **Fingerprint.** `MatchFingerprint.Of` hashes the flag only when it is lowered, following the pattern of AI Assist.
  Peers that disagree are refused, and matches with the flag up keep their fingerprint (mp-review).
* **Screens.** The random map screen draws `AIRESIGN.BMP` at (471, 427), as ReTzar reads the original. The flag is in
  the screen's kept settings. The lobby shows "AI may resign" as a check.
* **Default.** The flag starts up, as Tzar2 has played so far. ReTzar reads a missing `tzar.ini AIRESIGN` as the sword.
  The original's first default is UNVERIFIED (play-test: a fresh install's random map screen).

## Consequences

* No golden or bench hash changes: the default keeps the behaviour and the bytes.
* A match with the sword cannot be joined, replayed or resumed by a build older than this one. It is refused at the
  settings, before any state is built.
* Later fields can use the remaining extras bits without another version, as long as older builds refuse unknown bits.
