# Hotkey and mouse parity checklist (M3)

TDD §15.2 (REQ-UI-03), §22 item 10, §25 M3 ("hotkey parity checklist complete"). Sources: the original manual
(chapter 6 "Hot Keys" and the selection/orders sections), the in-game help `HELP/HK.TXT` and Appendix E.6. The
descriptions below are our own words; nothing is copied from the original files.

The fixed keys live in one table, `Tzar2.Game.Input.HotkeyTable` (every binding has the id used here and can be
rebound). Command-card letters come from the user's imported `MENU.INI` and the Ctrl+letter building classes from
`KEYS.INI`, so they are data, not code. `HotkeyTests.The_parity_checklist_lists_every_binding` fails when a binding
is missing from this page.

Status: **done** = works as in the original; **partial** = works with the difference noted; **blocked** = bound, but
the feature behind it is not in the simulation yet (the key shows a message naming the missing feature).

## Summary

| | Fixed keys | Group keys | Building-class keys |
|---|---|---|---|
| Bindings | 45 | 18 | 26 |
| Done | 34 | 9 (assign) | 26 |
| Partial | 10 | 9 (select: the double-press centring is assumed) | — |
| Blocked (waits for location messages) | 1 | — | — |

Mouse semantics: 16 rows, 11 done, 4 partial, 1 deliberately unbound (wheel zoom).

The checklist is complete: every original key and mouse action is listed, bound and either works or reports what it
waits for. "Blocked" rows close by themselves when the features land (location messages in the multiplayer chat).
Updated 2026-10-22: the diplomacy, tribute and inventory screens are in.

## Function keys

| Id | Keys | What it does | Status | Notes |
|---|---|---|---|---|
| `menu` | F1 | Opens the game menu | done | Load/save entries of the menu are M6 |
| `menu-f10` | F10 | Same as F1 | done | |
| `save` | F2 | Save dialog | done | the save dialog of every single player game (ADR 0187); multiplayer matches are not saved |
| `load` | F3 | Load dialog | done | the load dialog (ADR 0187) |
| `scores` | F4 | Scores | partial | every kingdom's sim score and statistics as messages (ADR 0106); the full score screen is the results dialog |
| `diplomacy` | F5 | Diplomacy screen | done | the diplomacy screen (HUD-012) |
| `tribute` | F6 | Tribute dialog | done | the tribute screen (HUD-013) |
| `objectives` | F8 | Current objectives | done | the quest log of scenario maps (ADR 0132); the victory condition in skirmishes |
| `options` | F9 | Options | partial | toggles day/night (Shift+F9 also rain); a full options dialog follows the dialog set of REQ-UI-02 |
| `screenshot` | F11 | Saves a snapshot of the screen | done | PNG in the user folder |

## Keys during play

| Id | Keys | What it does | Status | Notes |
|---|---|---|---|---|
| `chat` | Enter | Chat dialog | partial | multiplayer: the chat line (ADR 0168); single player has none, so its cheat codes cannot be typed yet |
| `send-location` | Ctrl+Enter | Sends the view position to the other players | blocked | the multiplayer chat has no location messages yet |
| `center` | Space | Centres the view on the selection | done | |
| `default-item` | Backspace | Uses the unit's default item at once | done | the selected units with an inventory use their first item on themselves (GAME-10) |
| `escape` | Esc | Cancels a pending order or placement, closes a sub-menu, else deselects | done | with nothing selected it opens the game menu (the owner's request, SHELL-028) |
| `event-last` | Home | Jumps to the latest event (unit trained, building finished) | done | technology events come with research |
| `event-next` | PgDn | Next event after Home | done | |
| `event-prev` | PgUp | Previous event after Home | done | |
| `event-select` | Ctrl+Home | Like Home, and selects the object | done | |
| `location-last` | Shift+Home | Jumps to the latest received location | partial | locations are our own "under attack" alerts until players can send them (M6) |
| `location-next` | Shift+PgDn | Next location | partial | as above |
| `location-prev` | Shift+PgUp | Previous location | partial | as above |
| `rally` | End | Shows the rally point of the selected building | done | |
| `delete` | Del | Deletes own selected units and buildings | partial | castles need Shift+Del (Appendix E.6); the original's confirmation rules for guilds are not verified |
| `delete-important` | Shift+Del | Deletes including castles | done | |
| `speed-up` | + | Faster game | done | steps 25 … 300 % of the classic tick rate |
| `speed-down` | − | Slower game | done | |
| `trees` | `\` | Translucent trees | partial | drawn at 45 % opacity above units, not with the original's stencil look (§13.3) |
| `inventory` | `/` | Shows the selected object's inventory | partial | opens and closes the inventory on the card (HUD-017); the manual and HK.TXT name the two slash keys the other way round, verify in the running game |
| `inventory-prev` | < | Previous inventory page | done | the previous page of the inventory or the card (HUD-018) |
| `inventory-next` | > | Next inventory page | done | the next page of the inventory or the card (HUD-018) |
| `tab` | Tab | Shows one unit of a multiple selection (cycles) | done | |
| `bars-hold` | `` ` `` (held) | Shows every health bar while held | done | |
| `bars-toggle` | Ctrl+`` ` `` | Health bars on/off | done | |
| `group-clear` | Ctrl+0 | Removes the selected units from their group | done | |
| `xp-above` | Ctrl+] | Selects units with more experience than the average | done | by level, of the selection or of the own units on screen (GAME-08) |
| `xp-below` | Ctrl+[ | Selects units with less experience than the average | done | as above |
| `hp-above` | Alt+] | Selects units healthier than the selection's average (no selection: above 50 %) | done | |
| `hp-below` | Alt+[ | Selects units weaker than the average (no selection: below 50 %) | done | |
| `ungrouped` | Alt+`\` | Selects the own units on screen that are in no group | done | |
| `scroll-left` | ← | Scrolls the view | done | also mouse at the screen edge |
| `scroll-right` | → | Scrolls the view | done | |
| `scroll-up` | ↑ | Scrolls the view | done | |
| `scroll-down` | ↓ | Scrolls the view | done | |
| `pause` | Pause | Pauses the game | partial | the original's pause key is not in HK.TXT; Pause is assumed |

## Groups and building classes

| Id | Keys | What it does | Status | Notes |
|---|---|---|---|---|
| `group-assign-1` … `group-assign-9` | Ctrl+1 … Ctrl+9 | The selection becomes group n (members leave other groups) | done | |
| `group-select-1` … `group-select-9` | 1 … 9 | Selects group n; pressing it again centres on it | partial | the double-press centring is assumed |
| `building-a` … `building-z` | Ctrl+A … Ctrl+Z | Selects the own buildings of the class `KEYS.INI` binds to the letter, one after the other (Ctrl+C castles, Ctrl+B barracks…) | done | Appendix E.6 calls them "build hotkeys"; HK.TXT says they select. We follow HK.TXT |

## Command card

| What | Status | Notes |
|---|---|---|
| Card letters of `MENU.INI` (M move, B build, H harvest, A attack, S stop, P peasant…) | done | letters are read from the user's tables; plain letters go to the card first |
| Card orders move, attack (on ground: move-advance), harvest, patrol, guard (follow), stop | done | |
| Build menu and its sub-menus, placement ghost with legality, Shift keeps placing | done | right click or Esc cancels (manual ch. 6) |
| Train / right click on a train button takes one queued unit off | done | Shift+click queues five, Shift+right click takes five off (the owner, 2026-10-21) |
| Repair, capture, explore, stand ground, pick items, bribe, spells, technologies, gates | done | the command card model (ADR 0182); an order greyed out says why in its tooltip |

## Mouse

| What | Status | Notes |
|---|---|---|
| Left click selects; Shift+click adds or removes a unit | done | |
| Drag box selects own units (an own building when the box holds no units) | done | |
| Double click: same type on screen | done | |
| Ctrl+double click: same type and same current state | done | state = current activity |
| Alt+double click: all military or all non-military | done | |
| Alt+Ctrl+double click: military/non-military with the same state | done | |
| Right click: default action (move, attack, harvest, help build) | done | |
| Ctrl+right click: move-advance; Ctrl+Shift: with waypoints | done | |
| Alt+right click: move-sneak; Alt+Shift: with waypoints | partial | the flags reach the sim; sneak behaviour is the sim's (GAME stream A) |
| Shift+right click: waypoints | partial | queued orders are the sim's |
| Building selected: Ctrl+right click sets the rally point | done | a plain right click sets it too |
| Alt / Ctrl+Alt+right click on a building: rally with default action / move-advance | partial | sets a plain rally point until the sim has rally kinds |
| Right click cancels placement and pending orders | done | |
| Minimap: left click/drag moves the view, right click orders to that point | done | |
| Item clicks (use, drop, instant, default item) | done | in the inventory: left click uses (a book's targeted spell waits for a click), Ctrl at once, right click drops, Alt + click makes the default item (HUD-018) |
| Mouse wheel zoom | — | off in the Authentic preset (§15.2); not bound |
