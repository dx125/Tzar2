# 0072. Turn input into commands with engine-free selection, order and hotkey models; build the HUD from LAYOUT.INI and the S_VIEW art

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M3 stream D)
* **Related:** TDD §7.4, §15.1 (REQ-UI-01/02), §15.2 (REQ-UI-03), §22 item 10, §25 M3; Appendix E.6;
  docs/mechanics/hotkey-parity.md; ADR 0070, 0071

## Context

TDD §7.4 has a `CommandBuilder` with the UI semantics of modifiers before commands reach the scheduler; §15.2 lists the
original's mouse and key semantics, all rebindable; M3's exit criterion is a complete hotkey parity checklist. The HUD
must be reproduced from the imported bitmaps and `LAYOUT.INI` geometry.

## Decision

1. **Engine-free input models in `Tzar2.Game.Input`**, unit-tested:
   * `SelectionModel` — click, Shift+click, drag box (own units win, else one own building), the four double-click
     variants, control groups (Ctrl+digit, digit, second press centres, Ctrl+0), Tab focus, health filters, ungrouped.
   * `OrderResolver` — right click: enemy → `Attack` (armed units; the rest move), resource → `Harvest` (harvesters),
     own site → `Build` help (builders), ground → `Move` with Shift = queue, Ctrl = move-advance, Alt = sneak;
     producers → `SetRally`. Card orders (move, attack / move-advance on ground, harvest, patrol, guard = follow, rally)
     target the next click. Results are `PlannedCommand`s that append to a `CommandList`.
   * `HotkeyTable` — every fixed key of HK.TXT/E.6 with a stable id, a status (done/partial/blocked) and a note;
     rebinding by id. Command-card letters (`MENU.INI`) and Ctrl+letter building classes (`KEYS.INI`) stay data.
   * `Placement` — ghost legality from the frame's buildable layer, equal to the sim's check (tested).
2. **The parity checklist** (`docs/mechanics/hotkey-parity.md`) lists every binding id; a test fails when one is
   missing. Keys whose feature is not in the sim yet are bound and say why instead of doing nothing.
3. **HUD** (`Hud`, a `Control` on a `CanvasLayer`): the `S_VIEW` screen bitmap of the chosen `LAYOUT.INI` section
   (view, minimap, resource boxes, menu/diplomacy/chat buttons with their two-state bitmaps, clock); the bottom panel's
   inner slots, which the INI does not give, are measured on the panel bitmap and kept as named constants in
   `HudLayout` (same at 800 and 1024, anchored to the bottom): the 2×4 command card, portrait, stat rows, info strip.
   Text uses the original bitmap fonts; card buttons use `ORDERS/BUTTONS.BMP` cells, portraits `ICONS.BMP` cells.
4. **Dialogs** (main menu, skirmish setup, in-game menu, results) are `ArtScreen`s over the `S_MAIN`, `S_SINGLESETUP`,
   `D_MENU`, `D_ENDGAME` bitmaps with measured button slots; labels come from `STRINGS.INI` (the in-game menu's labels
   are baked into its bitmap, UNK-15). The data-defined `ui/layouts/*.yaml` of REQ-UI-02 comes with the full dialog set.
5. **Test driver.** `--autoplay <dir>` plays a scripted skirmish through the same input entry points (clicks, boxes,
   keys, card presses, minimap) with the real mouse ignored, and writes screenshots; `--game-smoke` runs a skirmish
   headless for N ticks (CI-capable with the synthetic package).

## Alternatives considered

* **Godot InputMap actions for everything:** fine for rebinding, but untestable without the engine and blind to the
  original's modifier semantics; the table maps engine keys to `GameKey` in one small class (`KeyMap`).
* **Godot Control themes for the HUD:** would not reproduce the bitmaps pixel for pixel.

## Consequences

* New sim features (items, diplomacy, experience, repair, rally kinds) flip a binding from blocked to done and add the
  command in `OrderResolver`/the card classifier.
* Only `LAYOUT1024H` is wired in the game (the 800 and left/right layouts load through the same class); a layout option
  is a small follow-up.
