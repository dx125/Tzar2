# UI review, October 2026: every screen at every display, and likeness to the original

Work queue item 15 (the owner's order of 2026-10-22). ADR 0200 describes the test that backs it.

* **Displays:** 1024 × 768 (4:3); 1280 × 720, 1366 × 768, 1600 × 900, 1920 × 1080, 2560 × 1440 and 3840 × 2160 (16:9);
  1920 × 1200 (16:10); 3440 × 1440 (21:9).
  * Each display is checked in Wide at Auto and at every other interface scale the window allows, and in Authentic:
    22 displays (`DisplayCases`).
  * Windows display scaling of 100–200 % does not change the pixels Godot gets (it is DPI-aware). It only changes which
    interface scale a player picks, and every scale is covered. See UI-L6 for what Auto does with it.
* **Screens:** 35 screens and dialogs at each display, 770 audits per run:
  * **The shell:** main menu, settings, single player, custom map choice and setup, random map setup, campaigns, load
    game, multiplayer, lobby, replays, loading, victory and defeat.
  * **The game:** the HUD with nothing selected, with a building and with many units; the game menu, options, save,
    load, diplomacy, tribute, gamble, exchange, trade, craft, a message, a story screen, the quest log and a
    conversation.
  * **The editor:** the editor and its trigger, import and campaign windows.
* **How:**
  * The UI smoke test (`--ui-smoke`, CI job `godot-smoke`) audits the layout at every display.
  * Screenshots rendered at 1024 × 768 Authentic, 1024 × 768, 1280 × 720 and 3440 × 1440 (1720 × 720 logical) Wide,
    with the package imported from the clean GOG v1.10 install. They stay local.
  * Texts compared with `LOCAL/STRINGS.INI`, fonts with `LOCAL/FONTS` and `CREDITSFNT.INI`, and button art and
    layouts with the `IMAGES/SCREENS` art. Nothing of the original is quoted here; texts are named by their STRINGS.INI
    key (`section.key`).

## Results by display

| Display | Layout audit (open findings) |
|---|---|
| 1024 × 768 Wide | UI-L2 (lobby), UI-L3 (editor windows) |
| 1024 × 768 Authentic | UI-L2, UI-L3 |
| 1280 × 720 Wide | UI-L2 |
| 1280 × 720 Authentic | UI-L1 (fixed: the HUD's lower 48 rows and the bottom of every in-game dialog were cut off); UI-L2, UI-L3 |
| 1366 × 768, 1600 × 900, 1920 × 1080, 1920 × 1200, 2560 × 1440, 3440 × 1440, 3840 × 2160 (Wide, every scale) | UI-L2 |
| the same in Authentic | UI-L2, UI-L3 |

This table is the state when the review was made; the findings' tables below say which are fixed since.

Every other screen fits every display: the menus' 800 × 600 art centred on a logical screen of at least 1024 × 720,
the dialogs over the HUD, and the Wide HUD cut into anchored pieces (ADR 0177). UI-L4 (conversation) and UI-L5 (random
map notice) are on every display. The likeness findings below do not depend on the display.

## Layout findings (the audit)

| Id | Screen | Displays | Finding | Fix |
|---|---|---|---|---|
| UI-L1 | game, all in-game dialogs, editor | Authentic in a window under 768 rows (1280 × 720) | Godot's integer stretch does not scale below 1, so the bottom 48 rows of the 1024 × 768 picture are cut: half the card's second row, the minimap's bottom, the dialogs' buttons. | **Fixed:** a window smaller than the logical screen shrinks the picture to fit (fractional stretch, letterboxed; `DisplayMath.Fits`). |
| UI-L2 | lobby | all | The rows of Godot controls are 30 px apart but 31 px high. The team picker runs into the ready and ping text; "Dropped players" runs into its picker. | **Fixed:** rows 32 px apart, pickers of fixed width that cut their text, the long caption's picker on a line of its own. The look is UI-A7. |
| UI-L3 | editor: import and campaign windows | 1024 × 768 Wide, every Authentic | The windows are 1152 and 1034 px wide, wider than a 1024-wide screen. | **Fixed:** their button rows wrap, and the campaign window's details column is narrower. |
| UI-L4 | conversation | all | Phrases are wrapped to 330 px in a 300 px box (`D_SAY`) and run out of it. | Wrap to the box. |
| UI-L5 | random map setup | all, without the original AI scripts in the package | The notice that the engine's AI is used is wrapped to the screen, not to the left panel, and runs over the map rows. | Wrap it to the panel. |
| UI-L6 | all | 1920 × 1080 at 125–175 % Windows scaling | Auto takes the largest whole scale that keeps 1024 × 720, so a 1080p laptop at 150 % gets scale 1: the HUD is smaller than the system's text. No whole scale fits better (2 gives 960 × 540). | Not a bug of the layout. The Smooth preset (fractional scale, ADR 0177, deferred) is the answer. |

## Likeness findings

The original's fonts are used everywhere except where noted (UI-F). Ids: **T** texts, **A** art, buttons and layout,
**F** fonts.

### Shell

| Id | Screen | Finding | Source | Fix |
|---|---|---|---|---|
| UI-A1 | main menu | The art has five carved button slots, 60 px apart (y 277–517). Our seven buttons are 48 px apart from y 215, so carved slots show between and under them as double frames. | `S_MAIN/back`, `btn` | Needs the owner's choice; see question 1 below. |
| UI-T1 | main menu | SETTINGS is ours; the original's item is `mainscr.pref`. EDITOR and ASSET VIEWER have no original text (ours, kept). | `mainscr.*` | Use `mainscr.pref`. |
| UI-A2 | main menu | CREDITS prints to the log; there is no credits screen. The original has one (`S_CREDITS` art, `CREDITSFNT.INI` fonts, `credits.txt`). | `S_CREDITS` | Build it; see question 6. |
| UI-T2 | random map setup | Map types and sizes are our lower-case names (`continental`, `small`); the original's are `sp_setuprnd.m_type_*`, `m_size_*`. No Fog and Explore show Yes/No in our words; `general.yes`, `general.no`. The subtitle "A new map from the options on the right" and the label "Kingdom" are ours. | `sp_setuprnd.*`, `general.*` | Use the keys; drop the subtitle. |
| UI-T3 | random map setup | Terrain names (`grass`, `summer`...) are ours in lower case. | none found in STRINGS.INI | Capitalise our names (no original key). |
| UI-T4 | custom map setup | Every option shows "Map" for "as the map says"; the original has one checkbox `sp_setupmap.t_mapdef` (SHELL-007, partial). "Kingdom" is ours. | `sp_setupmap.*` | With SHELL-007. |
| UI-A3 | load game (shell and in game) | The type selector's text is drawn left of the art's selector, and its click area is a 284 px strip from there to the left arrow; the right arrow does nothing. | `D_LOAD/back` | Text in the selector's field, arrows as buttons. |
| UI-A4 | victory and defeat | The art has four button slots; we use the middle two. The original offers `endgamedlg.stat` (achievements), `restart`, `quit` and, in a campaign, `next`. The statistics lines are ours. | `D_ENDGAME/back2`, `endgamedlg.*` | Needs the achievements screen; see question 4. |
| UI-T5 | loading | "Loading... the test map" joins a STRINGS text with our words. The original shows `msg.game_random`, `msg.game_load` and the `msg.load_*` stages. | `msg.*` | Show the stage texts. |
| UI-A5 | settings | Our own screen (ADR 0177) on the `S_CAMPAIGN` art: the art's empty page selector and left button slot show at the bottom. | `S_CAMPAIGN/back` | Cover or use them; low priority. |
| UI-A6 | multiplayer, replays | Built on the custom map setup's art (`S_SINGLESETUP`): its six empty option sliders show. Buttons stand in a column over the right panel's frame. The original multiplayer screens have art of their own (`S_MULTISETUP`, `S_MULTIPLAYER`, `D_MAPSETTINGS`) and texts (`mp_search.*`, `mp_mplayer.*`, `mp_setupmap.*`). | as named | Needs the owner's screenshots; see question 2. |
| UI-A7 | lobby | The same borrowed art and Godot's default theme (drop-down lists, check boxes). Civilization keys show raw (`euro`) instead of `mp_mplayer.race_*`. The slot colours are fixed colours, not the kingdoms' palette. | as UI-A6 | Question 2. |
| UI-F1 | multiplayer, lobby, replays, save | Labels, lists and text fields use Godot's default font, not the original's bitmap fonts. | `LOCAL/FONTS` | A Godot theme with the original fonts for these controls. |
| UI-T6 | multiplayer, lobby, replays | Every label is ours, though STRINGS.INI has most: `mp_search.create`, `search`, `join`, `back`, `t_pl_name`, `t_games`, `t_inform`; `mp_mplayer.play`, `settings`, `disconnect`. | `mp_*` | With UI-A6. |

### In the game

| Id | Screen | Finding | Source | Fix |
|---|---|---|---|---|
| UI-A8 | game menu | The INSTRUCTIONS item of the art does nothing (the online help is not built). Its buttons show no pressed state, though the art has one strip per item (`D_MENU/btn *`). | `D_MENU/*`, `menu.inst` | Pressed states; the help with the online help work. |
| UI-A9 | options | The art's slots hold other things than the original's. The second music check is `options.t_cd` in the original and holds Weather Effects in ours. The interface panel's three boxes (`options.t_ml`, the side layouts) are empty, and Hardware Cursor (`options.t_hc`) and Path Depth (`options.t_pd`) have no place. | `D_OPTIONS/*`, `options.*` | Question 3. The side layouts are work queue 14. |
| UI-A10 | message | The art has an image box, a small box under it, a text box and a 210 × 27 button slot. Our text runs across all of them, and our 120 × 25 OK button is not in the slot. The original's button is `wnd_message.close`. | `D_MESSAGE/*` | Text and image in their boxes, the button in its slot. |
| UI-A11 | quest log | The art has two quests per page (image box and text box each), a page selector and a button slot. Ours lists all quests over the boxes, without a page selector. Our title is our own; the original's are `wnd_quests.curquest`, `close`, `page`, `none`. | `D_QUESTS/*`, `wnd_quests.*` | Two quests per page in the boxes, the page selector, the keys. |
| UI-A12 | story screen | The OK button is a grey block: it asks `S_SCRPLAY/ok` (a 600 × 25 strip of states) for a 100 × 30 cell, taller than the strip, and falls back to a placeholder. | `S_SCRPLAY/*` | A cell of the strip's own height at the art's slot. |
| UI-A13 | HUD | PAUSED is our text at the top right; the original has a picture for it (`pause`, 200 × 100). | `IMAGES/SCREENS/pause` | Show the picture; its place needs question 5. |
| UI-T7 | card dialogs | "full load", "per trip" and "power" are ours. | none found | Keep. |

### The editor

The editor is Tzar2's own tool on Godot controls (TDD §18), not a copy of TzarEdit, so likeness does not apply. Its
layout is covered by UI-L1 and UI-L3.

## Screens the data cannot rebuild: screenshots asked of the owner

The art gives the backgrounds and buttons. Where texts and controls go on them is not in any data file. Each request
is one screenshot of the original (GOG v1.10, 1024 × 768), taken on the owner's PC; they stay local like the captures.

1. **Main menu** (UI-A1). No screenshot needed; this is a decision. Five original items fit the five carved slots. The
   editor and the asset viewer would then need another place, such as two small buttons in the bottom corners. The
   alternative is seven buttons at the slots' 60 px pitch from y 217, with the last one on the stone's bottom border.
2. **Multiplayer** (UI-A6, UI-A7, UI-T6), three screenshots:
   * the connection screen with a game found and selected (`S_MULTISETUP`);
   * the lobby with four players, one of them a computer (`S_MULTIPLAYER`);
   * the map settings dialog opened from the lobby (`D_MAPSETTINGS`).
3. **Options** (UI-A9): the in-game options dialog (F10 → OPTIONS).
4. **End of a game** (UI-A4): the victory dialog of a skirmish, and the achievements screen it opens.
5. **Pause** (UI-A13): a paused skirmish (the pause key), to see where the picture sits.
6. **Credits** (UI-A2): the credits screen, two screenshots a few seconds apart, or a 20 s video, to see how it moves
   through the pictures and names.
7. **Campaign dialogs** (UI-A10, UI-A11), from the first mission of the main campaign:
   * its opening message;
   * the quest log (F8) with at least one quest;
   * a conversation with choices, if one comes early.

   These confirm where the title, text and picture go in the boxes the art draws.
8. **Dialogs Tzar2 lacks** (`D_CHAT`, `D_CHOOSEAI`, `D_DIALOG`, `D_MSGWND`, `D_EDIT`, `D_DESYNC`, `D_DISCONNECT`,
   `D_WAIT4PLAYERS`, `S_TOOL`). Only once their features are built; listed so the art is accounted for.

## Fixes

| PR | Findings |
|---|---|
| this PR | the test, this review |
| next | UI-L1, UI-L2, UI-L3, UI-L4, UI-L5 (layout); UI-T1, UI-T2, UI-T3, UI-T5 (texts); UI-A3, UI-A10, UI-A11, UI-A12 (art); one small PR each |
| after the owner's screenshots | UI-A1, UI-A2, UI-A4, UI-A6, UI-A7, UI-A9, UI-A13, UI-F1, UI-T6 |
