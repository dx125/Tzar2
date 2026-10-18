# Capture session — instructions for the operator

Thank you for running this. It measures the original TZAR on your PC (colours, shadows, terrain, player colours,
unit speeds, attack timing) so that the rebuild can match it. **You run one command, wait about 25 minutes without
touching the mouse or keyboard, and send back one zip file.**

## What you need

* Windows 10 or 11 (64-bit), the GOG version of *TZAR: The Burden of the Crown* installed (v1.10, English).
* This repository (the same clone you used for the benchmark): `git pull`, then check out the branch you were told.
* The .NET 10 SDK. If `dotnet --list-sdks` shows no `10.x` line: `winget install Microsoft.DotNet.SDK.10`, then open
  a new terminal.
* PowerShell 7 (`pwsh`) is recommended; Windows PowerShell 5.1 also works.
* Internet access during the first run (it downloads the *cnc-ddraw* DirectDraw wrapper, 2.7 MB, MIT licence; the
  download is checked against a fixed SHA-256 hash).

## Run it

Close the game if it is open. In a terminal at the repository root:

```powershell
pwsh tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar"
```

Without `-Batch` this runs batch A. The second session is split into four batches of at most 30 minutes (ADR 0112);
run them one after another, each gives its own zip:

```powershell
pwsh tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar" -Batch A   # ~21 min: cheats, colours, combat, shadows, production
pwsh tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar" -Batch B   # ~23 min: unit speeds (half), gathering
pwsh tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar" -Batch C   # ~16 min: unit speeds (other half)
pwsh tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar" -Batch D   # ~15 min: day/night clock (no input at all)
```

After batch A has come back and the later scenarios have been adjusted, batches E–I (ADR 0113) follow the same way:

```powershell
pwsh tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar" -Batch E   # ~18 min: group speed, research, bonuses, gathering, capture
pwsh tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar" -Batch F   # ~25 min: fire, repair, healing, field decay, experience
pwsh tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar" -Batch G   # ~13 min: magic, loans, score
pwsh tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar" -Batch H   # ~22 min: combat outcomes (fast game speed)
pwsh tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar" -Batch I   # ~11 min: trade and gambling (discovery)
```

Use your own game folder (the one containing `Tzar.exe`). If PowerShell refuses to run scripts:

```powershell
pwsh -ExecutionPolicy Bypass -File tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar"
```

What you will see:

| Step | Time | What happens |
|---|---|---|
| 1. Checks | seconds | Windows, .NET SDK, game folder |
| 2. Build | ~1 min the first time | builds the `t2` tool |
| 3. Game files | ~10 s | compares your game with the GOG v1.10 English reference (2,916 files) |
| 4. Self-test | ~5 s | a small four-colour window flashes: tests screen capture, mouse/keyboard input, memory reading |
| 5. Capture | **15–25 min** per batch | the game starts and closes about 15 times, in a 1024×768 window; the mouse moves by itself, chat commands are typed, the camera jumps around the map |
| 6. Done | | prints the zip path and opens Explorer on it |

**During step 5 please do not touch the mouse or keyboard**, and keep the PC from locking (the tool asks Windows to
keep the screen on). Progress lines like `[7/16] terrain-tiles / mp2-grass` tell you where it is.
To stop early, click into the terminal and press **Ctrl+C**: the current game closes and a zip with everything
captured so far is still written.

## Send back

The last lines name the file, e.g. `C:\Users\you\Tzar2Capture\capture-20261020-191500.zip` (typically 100–400 MB).
Send that one file (Google Drive, Telegram, any file share). It contains screenshots of the game, small memory
samples, the log and the settings used — nothing else from your PC. Please do not post it publicly: the screenshots
show the game's copyrighted art.

Before you start, switch the keyboard to the **English** layout: the chat commands are typed key by key with the
current layout.

## Good to know

* **Your game folder is not changed.** The session copies the game to `%LOCALAPPDATA%\Tzar2Capture\game` and works
  there (settings, the DirectDraw wrapper, screenshots). While it runs, the per-user registry value
  `HKCU\Software\Haemimont\Tzar\Path` points to that copy; it is restored at the end. Delete
  `%LOCALAPPDATA%\Tzar2Capture` afterwards if you like.
* The game runs **windowed** through cnc-ddraw (that is what makes screenshots and input reliable on modern Windows).
  If the game does not start at all, rerun with `-Renderer native` (full screen, as the game normally runs) or
  `-Renderer existing` (keeps a wrapper that is already in your game folder, e.g. from the GOG Windows 11 patch).
* Only some scenarios: `-Scenarios smoke,colour-depth` (list them: `src\Tzar2.Tools.Cli\bin\Release\net10.0\t2.exe fidelity scenarios`).
* If step 3 says the files differ from the reference build, the safest fix is reinstalling the game from GOG. To
  capture anyway: add `-AllowModified` (the results are then marked as coming from a modified install).
* If the self-test fails, make sure the terminal is the active window and no other program (remote desktop, a game
  overlay, a macro tool) grabs input, then rerun.

## Random maps from the editor (part of the editor session, ADR 0113)

The random-map statistics (TDD RE-10) come from maps the original itself generates. In the editor (`TzarEdit.exe`):

1. **File → New**, choose **Random** (not Empty).
2. **Terrain Set:** Grass. **Size:** Medium (128×128) for the first round, Large (192×192) for the second.
   **Players:** 2 for Medium, 4 for Large. Leave the check boxes as the dialog offers them (note which are on) and
   the **Resources** radio button on its middle setting.
3. **Type:** Archipelagos, then Island, Continental, Lakes, Dry (one after the other; skip "Random").
4. Click **OK**, then **File → Save As**, and save the map with the type first in its name:
   `continental-128-2-1.wmp`, `continental-128-2-2.wmp`, `island-192-4-1.wmp`, …
5. Make **two maps per type and size**: 5 types × 2 sizes × 2 = 20 maps (about a minute each).
6. Put the 20 `.wmp` files in one folder, zip it and send it with a note of the check boxes that were on.
   The maps are made by the original and stay private like the capture zips.

We keep them in the git-ignored `tests/fidelity/local-maps/` (`*.wmp` is ignored everywhere, and the REQ-LEGAL-01
guard rejects the extension) and run `t2 fidelity maps tests/fidelity/local-maps`, which writes only numbers (castle spacing, gold, stone and trees per kingdom)
into `tests/fidelity/facts/random-maps.yaml`.

## Windows 11 notes (first session, 2026-10-18, Windows 11 25H2, GOG v1.10)

The first session ran 16 of 16 trials after these fixes, which the harness now applies or checks by itself:

* **GOG's compatibility database.** The GOG installer registers `goggame.sdb` (an `EmulateHeap` fix) for the file name
  `Tzar.exe`, so it also applies to the work copy. With it loaded the game crashed while initialising sound (an access
  violation in `winmmbase.dll`), even with sound off. The capture checks
  `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Custom\Tzar.exe` and, if a database is
  registered, prints the commands to remove it for the session (`sdbinst -u -g {guid}`, administrator terminal) and to
  put it back afterwards (`sdbinst "<game folder>\goggame.sdb"`). It never removes it by itself. The self-test
  shows the same check.
* **Renderer.** GOG's `tzar.ini` has `DIRECTXDEVICE=-1` (the game's GDI path, which switches the desktop to 16-bit
  colour itself and works only under the compatibility layer GOG sets for the install's own path: "Error switching to
  1024x768 video mode" in the work copy). The session writes `VIDEO.DIRECTXDEVICE=0` (DirectDraw, through cnc-ddraw).
* **Device counts.** At start-up `Tzar.exe` compares `VIDEO.DIRECTDRAW_DEVICESNUMBER` and the three
  `SOUND.SOUNDDEVICE*NUMBER` values with the devices it finds. GOG ships its own PC's values; any difference ends in
  "Invalid TZAR configuration detected. Running configuration program." The harness now reads this PC's DirectDraw,
  waveOut, DirectSound and midiOut devices and the processor clock (`SOUND.CPUSPEED`) at the start of every session
  and writes them into the work copy's `tzar.ini` only (waveOut and midiOut count their mapper as one more device;
  DirectSound and DirectDraw count every entry, primary driver included: this reproduces what the game's own
  `Setup.exe` wrote). The numbers change when, for example, Bluetooth headphones connect, so they are never stored in
  `session.yaml`. If the game still reports an invalid configuration, run `Setup.exe` in the work copy and put the
  values it writes into your local `session.yaml` (`ini:`); those win over the detected ones.
* **First click.** Windowed cnc-ddraw grabbed the cursor on the first click into the window and did not pass that
  click on, so PLAY never registered. The wrapper now runs with `devmode=true` (and `adjmouse=true`, as in
  cnc-ddraw's own profile for the game), and every scenario starts with a click on empty background. Longer click
  timings were tried before this fix and did not help on their own; they are not used.
* **Starting a map.** Only `-map <name>.wmp` works (`-map MP2` and `-map Maps\MP2.wmp` give "Error loading map
  file"). It opens the single-player setup screen with the map selected; the scenarios click PLAY (772, 596) and wait
  10 s. `-explore` and `-nofog` do nothing, not even on the setup screen's switches; the chat cheats `hmreveal` and
  `hmnofog` reveal the map, so every scenario types them after loading.
* **Snapshots.** F11 works in the game but not on the diplomacy screen (F5): those steps take a window capture only.
* **Not resolved by the first session.** `hmspawn <unit>` showed the unit as an all-green figure at the cursor that never
  moved or fought; the scenarios now place it with a click at the same spot. `hmkingdom <n>` changed nothing visible
  (the diplomacy screen still listed kingdoms 2-8 after `hmkingdom 8`); the player-colours, attack-timing and
  combat-pairs scenarios now work whether it switches the controlled kingdom or gives the selected unit to kingdom
  n, and the smoke scenario records which one it is.
* The `MidiSrv` service and the keyboard layout were not the cause of the crash; the layout matters for the chat
  commands (see above).

## For developers

* `t2 fidelity capture` (Windows) runs the scenarios in `tools/capture/scenarios/*.yaml` with the defaults in
  `session.yaml`; `t2 fidelity analyze <zip> --original <install>` turns a returned zip into fact files under
  `tests/fidelity/facts/` (numbers only; raw captures never go into git, REQ-LEGAL-01). Design: ADR 0030.
* `t2 fidelity scenarios` validates the YAML files; `t2 fidelity check <zip>` summarises a session;
  `t2 fidelity selftest` checks capture/input/memory on any Windows machine.
