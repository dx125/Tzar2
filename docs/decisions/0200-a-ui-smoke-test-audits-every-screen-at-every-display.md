# 0200. Audit every screen at every display in a headless UI smoke test

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** maintainer; product owner (work queue item 15, the UI review)
* **Related:** REQ-UI-01, REQ-UI-02, REQ-GFX-07, ADRs 0177 (Wide and Authentic), 0182 (UI logic in models)

## Context

The owner asked for a review of the whole interface at every modern resolution: 1024 × 768, 1280 × 720 to 3840 × 2160
(16:9, 16:10, 21:9) and Windows display scaling of 100–200 %. Nothing may be clipped or overlapping. Until now the
client's smoke tests checked that screens open, never where their controls end up, and they ran only locally: CI had no
Godot binary.

Facts that shape the test:

* Godot lays controls out the same with the dummy renderer of `--headless`, so the layout can be checked without a
  display. Pictures need a renderer.
* ADR 0177 decides the logical screen from the window: Wide divides the window by a whole interface scale (Auto keeps it
  at least 1024 × 720); Authentic is 1024 × 768. The window size decides the layout, not the monitor.
* Godot on Windows is DPI-aware: Windows' 100–200 % scaling does not change the pixels the game gets, only which
  interface scale a player wants. Covering every interface scale a window allows covers the scaling.
* Godot's integer stretch never goes below 1. A window smaller than the logical screen shows its top-left part (measured
  with Godot 4.7.2: Authentic in a 1280 × 720 window loses the bottom 48 rows).
* The CI runners have no original files. Layouts depend on the art's sizes and the fonts' widths.

## Decision

1. **`--ui-smoke`** (`GameApp.UiSmoke.cs`) opens every screen and dialog at every display of the review and audits it:
   * **The shell:** main menu, settings, single player, custom map choice and setup, random map setup, campaigns,
     load game, multiplayer menu, lobby (a LAN host), replays, loading, victory and defeat.
   * **The game:** a skirmish on the test map; the HUD with nothing, a building and many units selected; then the game
     menu, options, save, load, diplomacy, tribute, the card's gamble, exchange, trade and craft dialogs, a message, a
     story screen, the quest log and a conversation (`GameScreen.OpenForAudit`).
   * **The editor:** with its trigger, import and campaign windows.
2. **The displays** (`DisplayCases`, engine-free): each of the nine resolutions in Wide at Auto and at every other
   interface scale that gives a different logical screen, and in Authentic: 22 displays. The test sets the logical
   screen directly (`GameApp._display`), so it runs at any window size, headless.
3. **The audit** (`LayoutAudit`, engine-free; `UiAudit` collects the controls):
   * no visible control may leave the part of the logical screen the window shows (a backdrop covering the whole screen
     may be cut);
   * no two texts, buttons or fields of one layer may cover one another (a text wholly inside a button is its label;
     a dialog over the HUD or a window over the editor is another layer);
   * a text may not leave its button, nor the panel it starts on, and a label may not be wider than its box.

   Text drawn with the original bitmap fonts is a picture; `UiAssets.Text` names it (`text:` + the string) so the audit
   knows it is text.
4. **CI** runs the client's smoke tests in a new Linux job (`godot-smoke`) with the pinned Godot 4.7.2 .NET. The package
   is synthetic: the legacy tables, empty frames of the original art's sizes and blocks of the original fonts' average
   widths (`SyntheticUi`; numbers only, REQ-LEGAL-01). Locally, `TZAR2_PACKAGE` runs the same test on the package
   imported from the original.
5. **Open findings are a ratchet.** The test fails on any issue that no open finding of the review covers
   (`UiFindings.Open`, with the ids of [docs/reviews/2026-10-ui-review.md](../reviews/2026-10-ui-review.md)), and, on
   the synthetic package, on a finding no longer reported. A fix removes its entry in the same PR.
6. **Screenshots** for the likeness review: `--ui-smoke --ui-shots <dir>` without `--headless` saves each screen at its
   logical size. They show the original's art and stay local, never committed.

## Alternatives considered

* **Golden screenshots in CI:** need the original art (not in CI), break on every pixel and say nothing about which
  control is wrong.
* **Resizing a real window per display:** needs a display server and a monitor as large as 3440 × 1440 or 3840 × 2160;
  the logical screen is all that decides the layout.
* **Checking only that screens open (the old smoke tests):** that is what let the lobby's rows overlap.

## Consequences

* A change that pushes a control out of the window or onto another fails CI at once, with the display, screen and
  control named.
* The audit cannot see likeness: text over a panel baked into the art, wrong labels, wrong fonts. That stays a review
  against the original (the review document), with the screenshots.
* The CI job adds about two minutes and one more check to every PR (16).
* Revisit when the side layouts (`LAYOUT1024L/R`, work queue 14) or the Smooth preset arrive: they add displays.
