# The original's command-line switches, chat cheats and tick settings

Status: **names known, most behaviour UNVERIFIED** (UNK-30). Sources: the operator's reading of `Tzar.exe`'s strings
(GOG v1.10 EN, 2026-10-18), observed behaviour in the first capture session (`capture-20261018-024524`, ADR 0111),
and the earlier community list (ADR 0107). Only names and our own one-line descriptions are recorded here; nothing is
copied from the executable beyond the names.

## Chat cheats (UNK-30)

Typed into the chat line with the prefix `hm` (e.g. `hmreveal`). Names followed by "‹arg›" appear in the executable
with a trailing space, so they take an argument.

| Code | What we know |
|---|---|
| `reveal` | **Observed:** reveals the whole map. |
| `nofog` | **Observed:** switches the fog of war off. |
| `spawn ‹unit›` | **Observed:** shows the named unit at the cursor as an all-green figure that neither moved nor fought; probably waits for a click to place it (the next session checks). Our implementation: ADR 0107. |
| `kingdom ‹n›` | **Observed:** no visible effect when sent with nothing selected (the diplomacy screen still showed kingdom 1 as the viewer). Either switches the controlled kingdom in a way we did not see, or acts on the selection; the next session checks both. |
| `next` | Community list: wins the game. Our implementation: ADR 0107. UNVERIFIED. |
| `notech` | Community list: removes technology requirements. UNVERIFIED. |
| `god` | Community list: own units and buildings take no damage. UNVERIFIED. |
| `buildozer` | Community list: instant construction. UNVERIFIED. |
| `dvaleva`, `petleva` | Community list: resources (our placeholder: 2,000 / 5,000 of each). UNVERIFIED. |
| `discover ‹arg›`, `spy`, `usurp`, `hunt`, `time ‹arg›`, `nopop`, `dclr ‹arg›`, `dset ‹arg›`, `give ‹arg›`, `resign`, `general`, `cont ‹arg›`, `calc ‹arg›`, `hoseed`, `threads`, `timer`, `showgrid`, `ischeater`, `debugpath`, `prettypleasewithsugarontop`, `nompcheat`, `cheatsenabled` | Names only. Effects untested; several look like debugging aids (grid, path, timer, thread display) or cheat-permission switches. |
| `ainone`, `aieco`, `aiwar`, `aifull` | Names only; they look like switches for the computer player's economy and war parts (compare the map flags "Economy AI" / "War AI", ai-placeholders.md S26). UNVERIFIED. |

## Command-line switches

| Switch | What we know |
|---|---|
| `-map <name>.wmp` | **Observed:** opens the single-player setup screen with `MAPS\<name>.wmp` selected; the game starts with PLAY. Only this form works (`-map MP2`, `-map Maps\MP2.wmp`: "Error loading map file"). |
| `-explore`, `-nofog` | **Observed:** no effect, not even on the setup screen's switches. |
| `-nomovies` | Used by every capture; the intro movies did not play. |
| `-nomin`, `-help`, `-noautosave`, `-multiinst`, `-nonetversioncheck`, `-dumpaudio`, `-dumpvideo`, `-lockview`, `-record`, `-demo` | Names only. |
| `join`, `create`, `hgsjoin`, `hgscreate`, `+connect`, `+name`, `+hostname`, `+host` | Network game set-up (direct and through a game service); names only. |

## `tzar.ini` keys

| Key | What we know |
|---|---|
| `VIDEO.DIRECTXDEVICE` | -1 = the game's GDI path (switches the desktop to 16-bit colour itself; GOG's default), 0 = DirectDraw (what a DirectDraw wrapper needs). |
| `VIDEO.DIRECTDRAW_DEVICESNUMBER`, `SOUND.SOUNDDEVICENUMBER`, `SOUND.SOUNDDEVICEDXNUMBER`, `SOUND.SOUNDDEVICEMIDINUMBER` | Compared with the devices found at start-up; any difference makes the game run its configuration program and exit (ADR 0110). |
| `SOUND.CPUSPEED` | Written by `Setup.exe` (MHz); no start-up check observed. |
| `SPGAMETICK`, `MPGAMETICK` | Read by the executable. The Ready2Play launcher writes `SPGAMETICK=50` and `MPGAMETICK=200`; the executable's own defaults are not known. Presumably the game tick in milliseconds for single-player and multiplayer games; see the tick note in TDD REQ-SIM-01 and ADR 0111. |
| `RLEFIRST`, `TZARERRORHANDLER`, `SCREENCHOICE` | Read by the executable; meaning unknown. |
