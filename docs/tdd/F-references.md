# Appendix F — References & sources

## F.1 Primary (local, in `Origin/`; not redistributable)
* `manual.pdf` — game manual (64 pages): interface, civilizations, technologies, hotkeys, Preferences incl. **PATH DEPTH**.
* `Editor_Manual.pdf` — editor manual (48 pages): full trigger/conversation/campaign model (Appendix C).
* `Strategy_Guide.pdf` — campaign walkthrough and unit tables.
* `TZAR110.TXT` — official patch notes 1.05–1.10 (regression checklist, TDD §5.1).
* `DATA/**`, `IMAGES/**/*.INI`, `LOCAL/**` — rule data (Appendix B).
* `Logs/*/error.log` — 29 crash logs (TDD ORIG-08).
* `readme.txt`, `sources/` — HD patch v3.4 notes and sources (TDD ORIG-09).

## F.2 Game history, editions, rights
* Wikipedia — Tzar: The Burden of the Crown: https://en.wikipedia.org/wiki/Tzar:_The_Burden_of_the_Crown
* Steam store/community (app 825730): https://steamcommunity.com/app/825730/discussions
* Lutris entry: https://lutris.net/games/tzar-the-burden-of-the-crown/
* GOG DB — product 1207659252, build 49982740489443525 (v1.10, 2017-03-15), depots per language: https://www.gogdb.org/product/1207659252/build/49982740489443525
* GOG content-system builds API (source of `docs/original-build/manifests`): https://content-system.gog.com/products/1207659252/os/windows/builds?generation=2
* GOG forum — Tzar 1.10 Windows 11 patch, CD-audio tracks, Anthology expansions translated: https://www.gog.com/forum/tzar_the_burden_of_the_crown/tzar_110_gog_windows11_patch_cd_audio_tracks_anthology_expansions_translated_to_english/post15
* GOG forum — DirectDraw/cnc-ddraw: https://gog.com/forum/tzar_the_burden_of_the_crown/enable_directdraw_and_use_cncddraw
* ProtonDB (app 825730): https://www.protondb.com/app/825730

## F.3 Known issues & community knowledge
* PlayTzar (ES) — “Errores en las partidas” (7-second start lag, 2-minute resync, message overflow crash, ragequit desync): https://playtzar.jimdofree.com/tzar-a-fondo/bugs-del-tzar/errores-en-las-partidas/
* PlayTzar — “Tácticas prohibidas” (item cloning, lag usage, other exploits; page bodies were not retrievable during research): https://playtzar.jimdofree.com/tzar-a-fondo/tácticas-prohibidas/
* PlayTzar — Tzar 1.13b (Polish community balance mod, changelog): https://playtzar.jimdofree.com/viejas-webs/tzar-1-13b/
* Clan Conde forum — “Errores Tzar” (desync when killing many cows, long messages, sync logs, “waiting for response”): https://clan-conde.el-foro.net/t7-errores-tzar
* iXBT Live (RU) review — pathfinding problems, uniform population cost, bugs in long sessions: https://www.ixbt.com/live/games/tzar-burden-of-the-crown-odna-iz-zabytyh-igr-proshlogo.html
* old-games.ru forum threads (RU) — compatibility, patches: https://www.old-games.ru/forum/threads/tzar-the-burden-of-the-crown.34227/
* Igromania walkthrough (RU): https://www.igromania.ru/article/625/Rukovodstvo_i_prohozhdenie_po_'Tzar_The_Burden_of_the_Crown'.html
* Steam threads — campaign transition crash/hang (“Deleting Old Game…”), online play requires GameRanger: https://steamcommunity.com/app/825730/discussions/0/2257934448603189586 , https://steamcommunity.com/app/825730/discussions/0/1649919326316049346
* Cheat codes list: https://www.gamerevolution.com/?p=30064
* tzaronline.com (ES community): https://tzaronline.com/

## F.4 Modding tools & formats
* ModDB Tzar downloads (Arch, RLEsBMPs, QuickBMS, mods): https://www.moddb.com/games/tzar-the-burden-of-the-crown/downloads
* QuickBMS `tzar.bms` (WDT format description; GPL tool — reference only): https://aluigi.altervista.org/bms/tzar.bms
* Mods for compatibility testing: TzarMod, TzarOrcs, *The Sphere of Power*, *Descent Terrains*, *New Terrains* (ModDB/Gamepressure).

## F.5 Prior art
* Tzared (remake by nehhon): https://nehhon.itch.io/tzared , https://tza.red , article: https://www.keengamer.com/articles/features/others/tzared-the-return-of-the-crown/ , download page: https://www.gamepressure.com/download/tzar-the-burden-of-the-crown-tzared-tzar-remake-v523-game/z01325e
* OpenRA (C#, traits + YAML + Lua, imports original assets; GPL — design reference only): https://www.openra.net
* openage, OpenRCT2, fheroes2, 0 A.D. — engine reimplementations using original data.

## F.6 Technology
* Godot C#/.NET docs (platform support: desktop, Android/iOS experimental, no web): https://docs.godotengine.org/en/latest/tutorials/scripting/c_sharp/index.html
* Godot C# platform state (NativeAOT on iOS, trimming caveats): https://godotengine.org/article/platform-state-in-csharp-for-godot-4-2/
* Godot mobile update (April 2026): https://godotengine.org/article/godot-mobile-update-apr-2026/
* Godot releases: https://godotengine.org/blog/release
* Lua-CSharp (Lua 5.2 in pure C#, AOT-safe): https://github.com/nuskey8/Lua-CSharp
* MeltySynth (SoundFont synth in C#, MIT): https://www.nuget.org/packages/MeltySynth
* LiteNetLib (UDP networking, MIT): https://github.com/RevenantX/LiteNetLib
* YamlDotNet (static generator for AOT): https://github.com/aaubry/YamlDotNet
* innoextract (GOG installer extraction, zlib): https://constexpr.org/innoextract/

## F.7 Algorithms & design literature
* M. Terrano, P. Bettner — “1500 Archers on a 28.8: Network Programming in Age of Empires and Beyond” (GDC 2001) — deterministic lockstep.
* A. Botea, M. Müller, J. Schaeffer — “Near Optimal Hierarchical Path-Finding (HPA*)” (2004).
* E. Emerson — “Crowd Pathfinding and Steering Using Flow Field Tiles”, *Game AI Pro* (2013).
* A. Treuille, S. Cooper, Z. Popović — “Continuum Crowds” (SIGGRAPH 2006).
* J. van den Berg et al. — “Reciprocal n-Body Collision Avoidance (ORCA)” (2011).
* D. Harabor, A. Grastien — “Online Graph Pruning for Pathfinding on Grid Maps (JPS)” (AAAI 2011).
