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
| 5. Capture | **~25 min** | the game starts and closes about 15 times, in a 1024×768 window; the mouse moves by itself, chat commands are typed, the camera jumps around the map |
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

## For developers

* `t2 fidelity capture` (Windows) runs the scenarios in `tools/capture/scenarios/*.yaml` with the defaults in
  `session.yaml`; `t2 fidelity analyze <zip> --original <install>` turns a returned zip into fact files under
  `tests/fidelity/facts/` (numbers only; raw captures never go into git, REQ-LEGAL-01). Design: ADR 0030.
* `t2 fidelity scenarios` validates the YAML files; `t2 fidelity check <zip>` summarises a session;
  `t2 fidelity selftest` checks capture/input/memory on any Windows machine.
