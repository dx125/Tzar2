# Appendix C — Legacy scenario system reference (rules, events, actions, conversations, campaigns)

Source: original `Editor_Manual.pdf` (48 pages), re-expressed. This is the **feature contract** for the trigger engine (TDD §17.1), the editor (§18) and the `.wmp`/`.wcm` converter (§18.4). Items marked ⚠ need verification in the original (RE-04).

## C.1 Map-level data model

| Group | Fields |
|---|---|
| Map properties | Name (default “Randomly Created Map”), Author (default “UNKNOWN”), Description; Settings: Game Type (Standard, Not Specified), Victory Condition (Standard, Destroy Castle, Score Limit, Time Limit, Not Specified), Population Limit, Difficulty (Easy/Normal/Hard), No Fog, Explore |
| Terrain | Terrain set (fixed after creation), size (fixed after creation): 64, 96, 128, 192, 256, 384, 512; terrain painting; cliffs; trees; ground patches; ground/water objects; special locations; resources (with amount) |
| Kingdoms (8) | General: kingdom name, Tzar name, civilization (European/Arabian/Asian), player type (Human or AI, AI Only, Rescue), Active. Resources: food, wood, gold, stone. AI: script (from `ai.ini`), Economy AI, War AI. Technology: prohibited buildings, prohibited units, initial researches, prohibited researches, prohibited upgrades. Diplomacy: per pair Ally/Enemy/Neutral; for allies: Shared Vision, Shared Resources, Command Units; Shared Victory flag |
| Pseudo-players | Rescue, Nature (animals default to Nature) |
| Starting locations | One per player (camera start for humans) |
| Objects | Buildings, units, items, resources, ground/water objects, special locations (see C.2) |
| User resources | Images (BMP high color), sounds (WAV), music (MIDI); import/export/delete; `Minimap.bmp` → custom minimap (menu toggle), `Maptitle.bmp` → custom title image |
| Custom sounds | Name; sound; repeat: Every N / Looped / Random from–to / Delayed start; source: map point (X,Y) or map object (moves with it) |
| Custom music | Ordered list (played in random order) |
| Rules, actions, events, variables, timers | C.3–C.6 |
| Conversations | C.7 |

## C.2 Object properties
* **Building:** kingdom; name; title; skills: health, max health, resistance (each with a “use default” checkbox); flag **Not Appear** (hidden until an Appear Object action).
* **Unit:** kingdom; name; title (single selection only); icon (44×44) and big icon (96×96) from user images; skills: health, max health, attack, mana, defence, level (each with a “use default” checkbox); flags **No AI** (⚠ the manual says “when checked, the unit is under AI control”, which is contradictory, UNK-37), **Persistent** (properties carried to later campaign maps onto a unit with the same name), **Not Appear**. Multi-selection edits apply to all.
* **Items:** name, title, Not Appear, plus per type:
  * Resource: amount.
  * Armor: defence delta.
  * Weapon: attack delta (restored when dropped).
  * Scroll: text, scroll time, auto-close, image (~295×390 high color), sound, music.
  * Spell book: magic type, needs mana, time restriction (cooldown), max charges.
  * Potion: level, attack, defence, health, mana/power deltas.
* **Resources:** amount (default from `RES.INI`).

## C.3 Rules
* A rule = **name** + **condition** (expression) + ordered **action list**.
* All rules are checked **about every second**. When the condition is non-zero, the actions execute top to bottom. After execution the rule is **disabled**, unless its action list contains **Keep Rule**, in which case it stays active for the whole game.
* Actions are defined independently (named) and referenced by rules. Deleting an action removes it from all rules.
* Names: events = letters, digits, underscore; actions = letters, digits, spaces, underscores, apostrophes. The new editor allows Unicode display names but keeps legacy-safe identifiers internally.

## C.4 Shared property pages
* **Object (set):** an explicit list of map objects, with **All** (all must satisfy) or **Any**.
* **Object (type):** type + players (multi-select) + count with comparison `>=` or `<=` (not available in some actions) + All/Any.
* **Holder:** a single holder object (Enter/Leave Holder events).
* **Area:** a rectangle (editable top-left/bottom-right) **or** an object (its current position).
* **Expression:** see C.6.
* **Players:** a multi-select of 1–8.
* **Text, Image, Sound, Music:** user resources.
* **Map:** the next map name (campaign), used by Player Win/Lose and Load Map.

## C.5 Events (8)
Each event instance is a **named variable** that the game sets to TRUE while the event condition holds.

| Event | Properties | True when |
|---|---|---|
| Object In Area | Object, Area | the specified objects (All/Any, or type count) are in the area |
| Object Destroyed | Object | the specified objects are destroyed/killed |
| Objects Enter Holder | Object, Holder | the objects entered the holder (holders: buildings such as the tavern, ships, carpets, camel traders, peasants carrying items, …) |
| Object Leave Holder | Object, Holder | the objects left the holder |
| Object Exist | Object | the objects exist (are alive) |
| Player Defeated | Players | a selected player lost |
| Player Won | Players | a selected player won |
| Object Expression | Object, Expression | the expression evaluated for the objects is true (“when one or more properties of the objects change”) ⚠ evaluation context (UNK-38) |

## C.6 Expression language
```
expr        := assignment | logical_or
assignment  := variable '=' expr
logical_or  := logical_and ( '||' logical_and )*        // short-circuit, result 0/1
logical_and := equality ( '&&' equality )*              // short-circuit, result 0/1
equality    := relational ( ( '==' | '!=' ) relational )*
relational  := additive ( ( '<' | '<=' | '>' | '>=' ) additive )*
additive    := term ( ( '+' | '-' ) term )*
term        := unary ( ( '*' | '/' | '%' ) unary )*
unary       := '!' unary | '-' unary | primary          // ⚠ unary minus support to verify
primary     := integer | variable | function | '(' expr ')' | event_name | timer_name
variable    := '$' identifier
function    := '@' ('gold'|'stone'|'food'|'wood') '(' expr ')' | '@rnd' '(' expr ',' expr ')'
```
* **Integers only.** ⚠ Width, overflow and division by zero to verify (UNK-41).
* **Reserved variables** (refer to the “current object” / current player): `$health`, `$maxhealth`, `$attack`, `$defense`, `$level`, `$manna`, `$maxmanna`, `$kingdom`, `$x`, `$y`, `$gold`, `$stone`, `$food`, `$wood`.
* **User variables:** `$NAME` (must not collide with reserved names). They are global to the map ⚠ (and to the campaign? verify).
* **Functions:** `@gold(p)`, `@stone(p)`, `@food(p)`, `@wood(p)` with player 1–8; `@rnd(a, b)` random in [a, b] ⚠ inclusive bounds; the manual also shows `rnd(...)` without `@`.
* **Timers:** a Set Timer action creates a variable with the action's name that counts milliseconds (optionally counting down from an initial value that may be an expression).
* Event variables (C.5) can be used directly in conditions.

Manual examples: `(5*3 – 8)/12*7 >= ($My_VAR2/10 + 5)`, `@gold (2) == $stone + 100`, `$My_VAR1 = 2*$My_VAR1 – ($My_VAR2/10 + 5)`, `$My_VAR1 = rnd ($My_VAR1, 4*$My_VAR1)`.

## C.7 Actions (27)
| # | Action | Properties | Semantics |
|---|---|---|---|
| 1 | Add Quest | Text, Image | Adds a quest (named after the action) to the quest list (F8) |
| 2 | Remove Quest | Quest | Removes a quest |
| 3 | Appear Object | Object | Makes objects flagged Not Appear appear |
| 4 | Change Diplomacy Settings | two kingdoms, diplomacy (stance + ally sharing flags) | Changes diplomacy at runtime |
| 5 | Change Object Owner | Object, Kingdom | Transfers ownership |
| 6 | Change Player Status | Kingdom, “cannot win” / “cannot lose” | Locks a player's win/lose state |
| 7 | Conversation | Conversation | Starts a conversation (the game pauses) |
| 8 | Destroy Object | Object | Kills/destroys with the death animation |
| 9 | Erase Object | Object | Removes immediately without animation |
| 10 | Explore Area | time (ms), Reveal Fog, Area | Explores an area for a time |
| 11 | Expression | Expression | Evaluates (assignments) |
| 12 | Keep Rule | — | Keeps the rule active for the whole game |
| 13 | Load Map | Map | Loads the next campaign map |
| 14 | Lock View | time (ms), Area | Freezes the camera on an area |
| 15 | Lock/Unlock Units | Object, lock/unlock | The player cannot command locked units |
| 16 | Object Order | Object, Area, Order | Issues an order executed in an area |
| 17 | Player Lose | Players, Map | Forces the loss, then loads the next map |
| 18 | Player Win | Players, Map | Forces the win, then loads the next map |
| 19 | Scroll View | Area from, Area to | Moves the camera from one area's centre to another's |
| 20 | Send Chat Message | Players, Message | Chat message to players |
| 21 | Set Object Properties | Object, Expression | Sets properties via an expression using reserved variables |
| 22 | Set Timer | initial value (expression), count-down flag | Starts a ms timer variable named after the action |
| 23 | Show Message | Title, Text, Image | Pop-up message |
| 24 | Show Story Screen | scroll time (ms), Auto Close, Text, Image, Sound, Music | Story screen with scrolling text |
| 25 | Spawn Object | object type, owner, count (number), Area | Creates objects |
| 26 | Stop Timer | Timer | Stops a timer |
| 27 | Unlock View | — | Releases Lock View |

## C.8 Conversations
* Any number of independent conversations per map. Each is a set of **phrases** forming a graph (follow-up relations; loops are possible but hang the original game).
* A phrase has: name, **initial** flag (≤ 6 initial phrases per conversation), **source** object (speaker, whose portrait is shown), **condition** expression (the phrase is offered only if true), **result** expression (executed when the phrase is said), **text** (≤ 1,000 characters), optional custom **icon** and **sound**, and **follow-ups** (≤ 6).
* With several candidate phrases, the **player chooses**. The game is paused during conversations. Started via the Conversation action.

## C.9 Campaigns (`TzarCampaign.exe`)
* A campaign file = maps + description. Maps are imported/exported/deleted; per map: description, details; one or more **starting (entry) maps** (at least one, otherwise the campaign is invalid); a campaign description is shown on the campaign screen.
* Map order is defined inside the maps through Load Map / Player Win / Player Lose actions (next map name).
* Persistent units: properties saved at the end of each map for units flagged Persistent and alive; applied to units with the same name in later maps.

## C.10 Map validation (original)
On save, the original editor checks the map for errors and missing items. The map is saved even with errors, but it is not playable. The new editor replaces this with live validation (TDD §18.3).
