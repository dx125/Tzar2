# 0110. Capture harness on Windows 11: detect the machine's tzar.ini values, warn about GOG's shim, start maps through the setup screen

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (capture follow-up), product owner (operator run and patch)
* **Related:** ADR 0030 (capture harness), [tools/capture/README.md](../../tools/capture/README.md) "Windows 11 notes",
  REQ-LEGAL-01, REQ-RE-03

## Context

The first operator session (Windows 11 25H2, clean GOG v1.10 EN, 2026-10-18) only ran after local fixes: removing
GOG's compatibility database for `Tzar.exe` (the game crashed in its sound initialisation with it), `DIRECTXDEVICE=0`,
the device counts of the operator's PC in `tzar.ini` (the game exits when they differ from what it enumerates), cnc-ddraw
`devmode`/`adjmouse`, a click on empty background, `-map <name>.wmp` plus a PLAY click (the switch opens the setup
screen), and the `hmreveal`/`hmnofog` cheats instead of `-explore`/`-nofog` (which do nothing). The device values were
the operator's own and must not be committed. Two scenario families failed: no unit ever fought or moved (the spawned
units looked like all-green, unplaced figures) and `hmkingdom N` never switched the viewed kingdom.

## Decision

1. **Machine values are detected, never committed.** `MachineSettings` maps what `DeviceProbe` reads on the operator's
   PC (DirectDraw entries with secondary devices, waveOut + 1, DirectSound entries, midiOut + 1, the CPU's `~MHz`) to
   `VIDEO.DIRECTDRAW_DEVICESNUMBER`, `SOUND.SOUNDDEVICENUMBER`, `SOUND.SOUNDDEVICEDXNUMBER`,
   `SOUND.SOUNDDEVICEMIDINUMBER`, `SOUND.CPUSPEED`, under the session's `ini` (an explicit local value wins). The rule
   reproduces the game's own `Setup.exe` on the operator's PC and GOG's shipped values. A test keeps those keys out of
   `session.yaml`. Fallback when detection fails: run `Setup.exe` in the work copy and set the values locally.
2. **GOG's shim database: warn, do not act.** The capture and the self-test read
   `HKLM\...\AppCompatFlags\Custom\Tzar.exe` (both registry views) and print `sdbinst -u -g {guid}` and the command that
   reinstalls `goggame.sdb`. Changing HKLM needs administrator rights and is the operator's decision.
3. **Renderer and input.** `VIDEO.DIRECTXDEVICE: 0` in `session.yaml`; cnc-ddraw defaults `devmode=true`,
   `adjmouse=true`. The longer click timing of the operator's patch is not taken: it did not help before the devmode
   fix and was not tested after it.
4. **Scenarios.** `-map <name>.wmp`, `load_s: 5` (setup screen), then a background click, PLAY (772, 596), 10 s, and
   `hmreveal`/`hmnofog`; no `-explore`/`-nofog`. Every `hmspawn` is followed by a left click at the cursor (places the
   unit if it waits for one, selects it otherwise). Owner changes send `hmkingdom N` both before the spawn and with the
   new unit selected, clear the selection before switching back, and restore the camera with a minimap click, so the
   scenarios hold under either reading of the cheat. Player colours no longer rely on Ctrl+C / Space: one knight per
   kingdom on open ground, plus the setup-screen and diplomacy swatches. The smoke scenario records how both cheats
   behave.

## Consequences

* The next session should run without local edits; its smoke trial settles `hmspawn`/`hmkingdom`.
* The device rule is an inference from two data points (operator and GOG); if a third PC disagrees, the log shows the
  detected values and the README gives the `Setup.exe` fallback.
* The prelude steps are repeated in each scenario file (no include mechanism), as in the operator's patch.
