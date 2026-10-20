# 0177. Fill modern screens with the world (Wide by default) and add a settings screen

* **Status:** Accepted
* **Date:** 2026-10-20
* **Deciders:** product owner (first play-test), maintainer
* **Related:** REQ-GFX-07, REQ-UI-01, REQ-UI-02, ADR 0010

## Context

The client draws the original's 1024 × 768 logical screen scaled by a whole number and letterboxed: the *Authentic*
preset of REQ-GFX-07, the only one built. On the owner's 1440 × 900 window the game is a 1024 × 768 picture in black
bars; on a 2560 × 1440 monitor it is the same picture, one or two times larger. After the first play-test the owner
asked for modern resolutions and a flexible interface instead of the original's sizes, and for a settings item in the
main menu. The TDD already plans for it: REQ-GFX-07's *Wide* preset (the logical view extended to the window, original
sprite sizes) and REQ-UI-01's widescreen HUD variants (panel anchored, world view fills the rest).

The original HUD is one 1024 × 768 bitmap (`S_VIEW/SCREEN 1024.BMP`): a 30-pixel top bar (menu, diplomacy and chat
buttons on the left, five resource boxes, an ornament on the right), a 154-pixel bottom panel (minimap, selection view
and command card on the left, an empty marble area, an ornament on the right) and the clock globe's ornament over the
panel's left end. `LAYOUT.INI` gives its geometry.

## Decision

1. **Two display presets, Wide by default.**
   * **Wide:** the logical screen is the window divided by the interface scale *k*, a whole number. *Auto* takes the
     largest *k* that keeps the logical height at 720 pixels or more, and never less than 1. The root viewport uses
     `canvas_items` scaling with `expand` aspect and integer stretch over that size, recomputed when the window changes.
     The world view takes everything but the HUD bars, so a larger window shows more of the map at the same sprite
     size.
   * **Authentic:** the current 1024 × 768 letterboxed picture with integer scaling.
2. **The Wide HUD is the original bitmap cut into anchored pieces.** The top bar's left part (buttons and resources) is
   drawn at the left edge and its ornament at the right edge, with a plain slice of the bar repeated between them. The
   bottom panel's left part (minimap, selection view, command card) is drawn at the bottom left and its ornament at the
   bottom right. The marble info area between them stretches, so a multi-selection shows more icons. The globe
   ornament stays over the panel's left end. Every position still comes from `LAYOUT.INI` (`LAYOUT1024H`); the anchors
   and filler slices are measured on the bitmap and listed in `HudLayout`.
3. **Menus and dialogs** keep their 800 × 600 art, centred on the logical screen.
4. **SETTINGS in the main menu** opens a settings screen that applies changes at once and saves them in
   `user://game.cfg`:
   * display preset;
   * interface scale (Auto, 1–4);
   * window mode (windowed, fullscreen, borderless) and window size;
   * scrolling (edge scroll on or off, right-button drag scroll on or off, speed);
   * sound and music volume.
5. **Later:** the original's side layouts (`LAYOUT1024L/R`, the HUD as a column) and the *Smooth* preset (fractional
   scale with a sharp-bilinear filter).

## Alternatives considered

* **Scale the 1024 × 768 picture to fill the window (fractional, or Smooth):** shows no more of the map, and pixel art
  either blurs or gets uneven pixels. It does not answer "modern resolutions".
* **Free zoom of the world:** changes the camera, fog, minimap frame and picking at once. It may come later, on top of
  Wide.
* **New HUD art made for widescreen:** we would have to paint it, and it would not be the original's look. Cutting the
  original bitmap keeps the look with no new art.

## Consequences

* A larger window shows more of the map than the original's 1024 × 768. The simulation does not depend on the view, so
  replays and lockstep matches are unaffected. Players with different window sizes see different amounts of map, as
  800 × 600 and 1024 × 768 players did in the original.
* Every screen takes the logical size from `GameApp` instead of a fixed 1024 × 768. The HUD and the world view follow
  window resizes.
* Golden screenshots and the autoplay driver set the Authentic preset or a fixed window, so their pictures stay
  comparable.
* Revisit when the side layouts or Smooth are built, or if players prefer Authentic as the default.
