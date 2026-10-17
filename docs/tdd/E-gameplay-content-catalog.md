# Appendix E — Gameplay content catalog & controls

Sources: `manual.pdf`, `Strategy_Guide.pdf`, `DATA/*` of the research install. **Stat values below come from the research install, whose data tables are Russian-localized versions of the GOG v1.10 files (names translated; numeric changes unverified).** They illustrate the data shape and must be validated against the clean reference files (`docs/original-build/`) before entering the `classic` reference data (REQ-RE-03).

## E.1 Civilizations, development paths, buildings

| | European | Asian | Arabian |
|---|---|---|---|
| Core buildings | Castle, House, Farm, Tavern, Storage, Lumbermill, Blacksmith, Workshop, Barracks, Stable, Market, Outpost, Harbor | same set | same set + Magic Workshop |
| Religion path | Cathedral → Church (priests) | Shao Linn → Monastery (monks; priests from monks) | Mosque → Church |
| War path | Warriors' Guild (“academy”) | Warriors' Guild | Warriors' Guild |
| Magic path | Mage Tower | Mage Tower | Mage Tower → Magic Workshop |
| Trade path | Traders'/Merchants' Guild (“tcguild”) → Market | Merchants' Guild | Traders' Guild |
| Special | Crusade (anchor ≤ 4 tiles from cathedral), Spy | Rocket Tower, Great Wall, Ninja, Armored Chariot | Jihad (peasants → Jihad warriors), Janissary, Magic Workshop items, cow poaching |

**Common structures:** Field, Dummy (training, requires Tactics), Bridge (Architecture), Wood Wall → Stone Wall (Architecture), Wood Tower → Wood Guard Tower → Stone Tower → Stone Cannon Tower (Cannon Towers), Wood Gate → Stone Gate, Magic Gateway (neutral, captured by touching), Evil Castle (campaign).

## E.2 Units

**European:** Peasant, Footman, Maceman, Archer, Pikeman (Temper Iron → Halberd), Knight (Plate Armor + Stable), Longbowman (Longbow), Crusader (Fanaticism → Crusade), Mage, Trader (Trade Routes), Priest, Spy (Espionage). Spawned: Bat, Stone Golem (“troll”).
**Asian:** Peasant, Ronin (“footman”), Archer, Samurai (“maceman”), Horse Samurai (Bushido + Stable), Horse Archer (Art of Riding + Stable), Ninja (Ninjas), Armored Chariot, Mage, Priest (from monks), Monk. Spawned: Dragon.
**Arabian:** Peasant, Footman, Archer, Jihad Warrior (from peasants), Cavalry (“heavy_cavalry”, Stable), Horse Archer, Spearman (“pikeman”, Halberd), Satrap (“maceman”, Scimitar/Slavery), Janissary (“enichar”), Mage, Caravan (“trader”), Priest, Spy. Spawned: Magic Carpet, Jinn.
**Common:** Cow, Dwarf, Orc, Ghost, Skeleton (Magic Gateway), Bear, birds/eagles, fish; siege: Ballista, Catapult, Heavy Catapult (Siege Tactics); ships: Galleon (Map Making), Hulk, Xebec (“Pinque” in the manual), Fishing Boat; campaign: Messiah, Evil Castle.

### E.2.1 Sample stat table (European units, research install)
| class | cost | time s | sight | range | HP | atk | def | speed | rate | producer | requires |
|---|---|---|---|---|---|---|---|---|---|---|---|
| peon | 50f | 8 | 5 | – | 50 | 5 | 0 | 4 | 3 | Castle | – |
| footman | 50f | 12 | 5 | – | 65 | 6 | 3 | 4 | 5 | Barracks | – |
| archer | 40f 20w | 12 | 10 | 6 | 50 | 5 | 0 | 4 | 4 | Barracks | – |
| maceman | 120f | 14 | 5 | – | 120 | 8 | 0 | 3 | 3 | Barracks | – |
| pikeman | 90f 40g | 18 | 5 | – | 75 | 8 | 4 | 2 | 2 | Barracks | te_halberd |
| longbowman | 90f 50g | 18 | 18 | 12 | 50 | 6 | 2 | 3 | 2 | Barracks | te_longbow |
| knight | 120f 70g | 22 | 5 | – | 120 | 12 | 6 | 6 | 4 | Barracks | te_platearmor |
| crusader | – | – | 5 | – | 180 | 14 | 8 | 5 | 3 | Spawn | te_crusade |
| priest | 150g | 30 | 21 | 18 | 60 | 20 | 0 | 4 | 0 | Cathedral | – |
| spy | 150g | 30 | 12 | – | 50 | 20 | 0 | 4 | 4 | Castle | te_espionage |
| mage | 150g | 30 | 15 | 10 | 60 | 5 | 0 | 4 | 3 | Mage Tower | – |
| trader | 400f 200w | 50 | 7 | – | 350 | 0 | 0 | – | – | Market | te_traderoutes |

(The full tables for all civs plus common units and buildings are in `DATA/* UNITS.TXT` / `* BUILDINGS.TXT`; the importer maps them 1:1 to rules.)

### E.2.2 Damage bonus groups (BONUS.INI excerpt)
`pikeman` vs `mounted` +600%; `siege` vs `walls` +900%, vs `towers` +600%; `arrow_attack` vs `e pikeman` −75%, vs `chariot` −30%, vs `dragon` +75%, vs `bat` +100%; `priest` vs `mage` +300% and vs `magic_creatures` +300%; `mage` vs `priest` +400%; `dwarf` vs `buildings` +400%; `coast_towers` vs `ships_no_fish` +400%. Full matrix: `DATA/BONUS.INI`.

## E.3 Technologies (87) and upgrades (12)
IDs from `TECHNOLOGIES.INI`: `te_fields te_livestock te_feudalism te_architecture te_longbow te_platearmor te_woodplus te_stoneplus te_goldplus te_ironplus te_halberd te_learning te_temperiron te_crossbow te_scalearmor te_chainmail te_engineering te_cannontower te_heroism te_nobility te_honor te_warfare te_tactics te_mercenaries te_heroesquest te_fame te_siegetactics te_bodybuilding te_fanaticism te_crusade te_espionage te_inquisition te_healing te_medicine te_bless te_traderoutes te_mapmaking te_usury te_gambling te_supplystorage te_bribery te_tradetaxes te_bat te_invinsibility te_lightning te_freeze te_stonegolem te_landingtactics te_cowpoaching te_slavery te_ridinghorses te_janissary te_jihad te_magiccarpet te_jin te_witchcrafts te_rainoffire te_magicvision te_necromancy te_horsemanship te_scimitar te_jewelry te_orefloatation te_greatwall te_budhism te_irrigation te_armoredchariot te_bushido te_suntzubook te_meditation te_philosophy te_kungfu te_ninjas te_mastery te_tournaments te_banking te_chemistry te_gunpowder te_rocketarrow te_seafood te_concentration te_berserk te_flamebolt te_whirlpool te_dragon te_turntostone te_artofriding`.

Upgrades (`UPGRADES.INI`): wood tower → guard tower (`to_wtower2`), → stone tower (`to_stower`), → cannon tower (`to_stower2_1/2`), wood → stone wall/gate (`to_wall_stone`, `to_gate_stone`), and others.

Manual descriptions per civ (effects to quantify in RE-05):
* **European:** Castle — Feudalism (cheaper peasants), Architecture (faster building; stone walls, gates, bridges), Learning (more XP; enables the Warriors' Guild). Farm — Rotating Fields, Domestication. Blacksmith — Plate Armor, Temper Iron → Halberd, Scale Armor → Chain Mail. Lumbermill — Longbow, Saw, Crossbow. Storage — Stone/Gold Mining. Workshop — Engineering, Cannon Towers. Warriors' Guild — Heroism → Leadership → Honor, Warfare → Tactics, Mercenaries → Heroes' Quest → Fame, Siege Tactics, Body Building. Harbor — Landing Tactics. Cathedral — Fanaticism → Crusade, Espionage, Inquisition, Bless, Healing → Medicine. Merchants' Guild — Trade Routes → Map Making, Usury → Gambling, Supply Storage, Bribery, Trade Taxes. Mage Tower — Bat → Stone Golem, Invisibility, Lightning, Freeze.
* **Asian:** Castle — Feudalism, Architecture → Great Wall, Learning, Buddhism (resistance; enables Shao Linn). Farm — Rotating Fields → Irrigation, Domestication, Art of Riding. Blacksmith — Temper Iron, Scale → Chain Mail, Armored Chariot. Lumbermill — Saw, Crossbow. Storage, Workshop as EU. Warriors' Guild — Heroism → Leadership → Honor, Warfare → Tactics → Sun Tzu's Book, Mercenaries → Heroes' Quest, Siege Tactics, Bushido. Shao Linn — Meditation → Philosophy, Kung-Fu, Ninjas, Mastery, Tournaments, Healing → Medicine. Merchants' Guild — Trade Routes, Usury → Banking, Supply Storage → Seafood, Jewelry → Chemistry → Gunpowder → Rocket Arrow. Mage Tower — Concentration → Confusion, Whirlpool, Flame Bolt → Dragon, Turn to Stone.
* **Arabian:** Castle — Feudalism → Slavery, Architecture, Learning. Farm — Rotating Fields, Domestication → Horsemanship, Cow Poaching, Art of Riding. Blacksmith — Temper Iron → Scimitar, Scale → Chain Mail. Lumbermill, Storage, Workshop as EU. Warriors' Guild — Heroism → Nobility(Leadership) → Honor, Warfare, Mercenaries → Heroes' Quest → Fame, Riding Hordes → Janissary, Body Building. Harbor — Landing Tactics. Mosque — Fanaticism → Jihad, Espionage, Bless, Healing. Traders' Guild — Usury → Gambling, Trade Routes → Map Making, Supply Storage, Bribery, Ore Flotation. Mage Tower — Magic Carpet → Jinn, Witchcraft (Magic Workshop), Rain of Fire, Magic Vision, Necromancy.

## E.4 Spells and special actions
| Civ | Caster | Spells/abilities |
|---|---|---|
| European | Mage | Lightning, Freeze, Invisibility, summon Bat, summon Stone Golem |
| Asian | Mage | Whirlpool (vs ships), Flame Bolt, summon Dragon, Turn to Stone, Confusion (units turn hostile to all), Hypnotism, mana → experience (Mastery) |
| Arabian | Mage | Magic Carpet, Jinn, Rain of Fire, Magic Vision (eagles reveal fog), Raise Dead / Necromancy (skeletons from corpses) |
| All | Priest | Heal (Medicine → longer range), Bless (+attack), Exorcism (vs magic creatures; Inquisition boosts it) |
| EU/AR | Spy | Impersonate, Reveal (detect ninjas) |
| AS | Ninja | Cloak, Murder |
| All | Peasant | Build, Repair, Capture, Harvest, Bribe (Bribery; not crusaders/peasants) |

Spell objects in `DATA/OBJECTS`: `LIGHTNING`, `FREEZE_BOLT`, `FLAME_BOLT`, `FLAME_BALL`, `FIRE_RAIN`, `WHIRLPOOL`, `MAGIC`, `FOG_REVEALER`, `EXPLOSION`, `ROCKET_ARROW`, `CANNON`, `ARROW`, `BOLT`.

## E.5 Items
Weapon (+attack), Armor (+defence), Spell Book (spell, charges, mana, cooldown), Scroll (story), Potion (level/attack/defence/health/mana), Resource Item (resource amount), Flag, Soul item (kill / berserk orders), “PlusPlus” item. Object files: `ITEM`, `ITEM_FLAG`, `ITEM_HEALTH`, `ITEM_MAGIC`, `ITEM_PLUSPLUS`, `ITEM_SCROLL`, `ITEM_SOUL`, `RESITEM`, `FAKEITEM`. Arabian Magic Workshop crafts items; Asian Market sells artifacts (Jewelry).

## E.6 Controls (original)
**Function keys:** F1/F10 menu, F2 save, F3 load, F4 scores, F5 diplomacy (disabled by the HD patch), F6 tribute, F8 objectives, F9 options, F11 screenshot. (The HD patch uses F12 to pause its redraw hook. In the rebuild F12 is free.)

**Selection:** click; drag box; Shift+click add; double-click = all of that type on screen; Ctrl+double-click = same type and same state; Alt+double-click = all military or all non-military; Alt+Ctrl+double-click = military/non-military with same state; Ctrl+digit assign group; digit select group; Ctrl+0 remove group number; Tab = show individual unit within selection; Ctrl+`]`/`[` select above/below average experience (no selection: on-screen units with XP > 7 / < 8); Alt+`]`/`[` above/below average health (no selection: > / < 50%); Alt+`\` ungrouped units.

**Orders:** right-click = default action; Ctrl+right-click = move-advance; Ctrl+Shift+right-click = move-advance with waypoints; Alt+right-click = move-sneak; Alt+Shift+right-click = sneak with waypoints; Shift+right-click = waypoints / attack set / guard set; Alt + attack/guard = all objects of a type; building selected: Ctrl+right-click = rally point, Alt+right-click = rally with default action, Ctrl+Alt+right-click = rally with move-advance.

**Inventory:** right-click item = drop; left-click = use; Ctrl+left = instant use; Ctrl+right = instant drop; Alt+left = make default item; Backspace = use default item; `/` = show inventory (manual text swaps `/` and `\`; verify); `<` `>` = inventory pages.

**View/info:** Space = center on selection; Home = last research/production event (PgUp/PgDn navigate); Shift+Home = last received location (Shift+PgUp/PgDn); Ctrl+Home = same + select; Ctrl+Enter = send location to players; End = show rally point; `` ` `` (hold) = health bars, Ctrl+`` ` `` = toggle bars; `\` = translucent trees; `+`/`-` = game speed; Delete = delete own unit/building (Shift+Del for castle/guilds); Enter = chat; Escape = deselect / previous menu.

**Build hotkeys (`KEYS.INI`, Ctrl+letter):** A stable, B barracks, C castle, D dummy, E cathedral/shaolinn/mosque, F farm, G gates, H house, I field, J rocket tower, K blacksmith, L lumber mill, M mage tower, N tavern, O outpost, P workshop, Q bridge, R harbor, S storage, T towers, U church, V magic workshop, W walls, X traders' guild, Y academy (warriors' guild), Z market. Command-card letters per menu are in `MENU.INI`.

**Editor:** Ctrl+O open, Ctrl+S save, 1–8 select player for starting locations, TAB / Shift+TAB rotate a unit or cycle random shapes while placing, Alt-drag move, Ctrl-drag copy, Ctrl/Alt modifiers for gate/bridge orientation.

## E.7 Settings surfaced by the original
Game speed, scroll speed, path depth (removed in the rebuild, see ORIG-03), day/night, weather, UI layout (menu bar position), sound volume, unit speech, building sounds, nature sounds, music volume, CD audio, language prompt, hardware cursor, water animation FPS, 3D sound/EAX (obsolete).
