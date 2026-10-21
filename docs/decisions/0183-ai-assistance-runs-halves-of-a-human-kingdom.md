# 0183. Let a human's standby AI run the economy, the war or both of the kingdom when asked (AI assistance)

* **Status:** Accepted
* **Date:** 2026-10-21
* **Deciders:** agent (maintainer); product owner (asked for the rest of the command card, 2026-10-21)
* **Related:** ledger AI-004, HUD-033; REQ-NET-07, REQ-AI-03; ADRs 0051, 0163, 0182

## Context

The original offers AI assistance:

* With nothing selected, the card is MENU.INI `[ownership_ai]` instead of `[ownership]`. It adds `view_ai`, which opens
  `[aiassist]`: `set_ai_none`, `set_ai_eco`, `set_ai_war` and `set_ai_full` (ORDERS.INI `SET ai_*`; STRINGS2.INI "NO",
  "ECONOMY", "WAR" and "FULL Assistance").
* The option that turns it on is "AI Assist", a string of the multiplayer setup screen (STRINGS.INI `[mp_setupmap]`
  `ai_assist`). The single-player setup has no such string.
* No source says exactly what each half does.

What we have:

* The AI already splits into the map's "Economy AI" (economy, construction, production, research, fishing) and "War
  AI" (defence, attacks, naval landings) settings. Each executor checks them.
* A human kingdom of a multiplayer match already has a standby AI. It stays idle until the drop policy hands the
  kingdom to the computer (`PlayerFlags.AiControlled`, `SetController`; ADR 0163).

## Decision

* **Simulation:** two player flags, `AssistEconomy` and `AssistWar`, are set by a new player command, `SetAiAssist`
  (Arg0: bit 1 economy, bit 2 war; 0 none). The flags are state: they are hashed and kept in snapshots.
* **AI:** a standby AI runs when its kingdom is taken over or asks for assistance. Its executors run the economy half
  when the kingdom is taken over or asks for economy, and the war half when it is taken over or asks for war (always
  within the AI's own map settings). A computer kingdom runs as before.
* **Match option:** `MatchSetup.AiAssist`, the lobby's "AI Assist" (a bit of the match settings, carried by replays and
  in the match fingerprint when set). With it, every human kingdom gets a standby AI.
  * A single-player skirmish always sets it, as a usability improvement (the owner's rule: UI may improve on the
    original).
  * Peers drop `SetAiAssist` in a multiplayer match without the option, all alike.
* **Card:** with nothing selected the card is the kingdom's (`ICardSource.KingdomMenu`: `ownership`, or `ownership_ai`
  when the match offers assistance). The four buttons send `SetAiAssist`, and the one in force is lit
  (`FramePlayer.AiAssist`).

## Alternatives considered

* **A separate AI per half:** two AIs would fight over the same peasants and gold. The existing halves already share
  one script and one budget.
* **Reuse `AiControlled` for full assistance:** the drop policy sets and clears it from the relay's events. Mixing the
  two would let a returning player's `SetController 0` switch off assistance they asked for, or the reverse.
* **Only in multiplayer, as in the original:** the card would offer nothing in single player, where assistance helps
  most.

## Consequences

* An assisted kingdom's AI may reassign peasants or soldiers the player just ordered. That is what the original's
  assistance means, and the player can switch it off.
* What exactly each half does in the original is unknown (⚠ UNVERIFIED). Ours is the AI's existing split. A capture of
  the original with assistance on would settle it.
* Assisted AIs never resign for their humans: the AI only records a resign request, and nothing acts on it.
