# 0241. Package Windows with a Godot headless export, a zip and an Inno Setup installer, and import on first run

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** maintainer (Claude)
* **Related:** TDD §12.1–12.2 (REQ-IMP-01…04), §20 (REQ-PLAT-02), §24 (nightly Windows export), §25 M7; DEC-05,
  DEC-09; ADRs 0002, 0176, 0240, 0242, 0243

## Context

M7 needs a Windows build players can install or unzip, built the same way every time, with nothing of the original
game in it (DEC-05): the player's copy is imported on first start. The client had no export preset; the first-run
screen sent the player to the command line (`t2 import`). TDD REQ-PLAT-02 puts every piece of user data, the imported
package included, under Godot's `user://` (`%APPDATA%\Godot\app_userdata\Tzar2` on Windows), never next to the
executable. The installer tool must run headless on `windows-latest` and its licence must fit DEC-09 (permissive, no
copyleft into our files, no fee).

## Decision

1. **Export.** `src/Tzar2.Client/export_presets.cfg` has one preset, "Windows Desktop" (x86_64, PCK beside the
   executable, console wrapper always, .NET debug symbols included for line numbers in crash reports, no PE resource
   editing). `tools/package/package-windows.ps1 -Stage export` runs `godot --headless --import` and
   `--export-release "Windows Desktop"` with the pinned Godot 4.7.2 .NET and its export templates. Godot's export wants
   `Tzar2.Client.sln` beside `project.godot`; the script writes a throw-away one (git-ignored). `Optimize` is on for
   Godot's `ExportRelease` configuration in `Directory.Build.props`, which the SDK leaves unoptimised and the project
   references inherit.
2. **Package.** `-Stage package` adds `version.txt`, `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt`, `README.txt` (the beta
   readme), the optional crash endpoint file and `SHA256SUMS.txt`, then writes
   `Tzar2-<version>-win-x64-portable.zip` (entries sorted, timestamps set to the commit's time) and, with Inno Setup's
   compiler, `Tzar2-<version>-win-x64-setup.exe`.
3. **Installer: Inno Setup 6** (`tools/package/tzar2.iss`). Per-user by default (`PrivilegesRequired=lowest`, to
   `%LOCALAPPDATA%\Programs\Tzar2`, no administrator rights), all users on request; Start-menu entries; silent install
   and uninstall for CI. **The uninstaller removes only what it installed**: there is no `[UninstallDelete]`, and the
   player's data is outside `{app}`. A fixed `AppId` makes later versions upgrade in place.
4. **Version stamp.** `VersionPrefix` 0.7.0 in `Directory.Build.props`; the workflow sets `VersionSuffix=beta.<run>`,
   the SDK appends the commit, so `GameInfo.Version` (main menu, logs, replays, crash reports) and the file names read
   `0.7.0-beta.<run>+<commit>`; `version.txt` and the installer's version resource carry the same.
5. **First-run import** (REQ-IMP). `InstallLocator` (Tzar2.Import) finds the original: `TZAR2_ORIGIN`, GOG's registry
   key (`GOG.com\Games\1207659252`, the product id of every GOG depot), Steam libraries (`libraryfolders.vdf`,
   `appmanifest_825730.acf`), and the usual folders; a folder counts when `Tzar.exe` or `TzarHD.exe` is at its root.
   The first-run screen lists them, imports the chosen one (or a folder picked by hand) on a worker thread with the
   importer's progress lines into `user://import/base.original.t2pkg`, and opens it. `--import <dir>` does the same
   without the screen; with `--smoke` it prints `FIRST-RUN IMPORT OK <package> <seconds> s` and quits, which times the
   import on any PC with the game. The original install is only read (REQ-IMP-04).
6. **Pipeline.** `.github/workflows/windows-package.yml` runs on PRs that touch packaging, by hand, and every night
   (the nightly `Windows Godot build` job calls it). It checks: the exported game reaches the first-run screen
   headless and reports the version; a normal quit and a killed run reach the crash reporter (ADR 0243); a second
   export is compared file by file with the first (informational until it is stable); install, start, uninstall,
   and the data folder intact; the zip's game starts. It uploads the zip and the installer as an artifact for 30 days
   and publishes nothing.

## Alternatives considered

* **WiX Toolset:** MSI is the enterprise format, but WiX v4 and later are under the MS-RL with an "Open Source
  Maintenance Fee" on its binaries for commercial users since v6; Inno Setup's licence is permissive, it is free for
  any use, and its scripts are simpler. MSIX needs signing even for testing.
* **NSIS:** also fits the licence; Inno Setup's per-user installs, upgrades by `AppId` and silent switches are what we
  need with less script.
* **Import inside the installer:** the installer would need the importer and the .NET runtime; the game imports
  anyway when the package is missing or broken, so one place does it.
* **Godot's self-contained mode for the portable zip** (`._sc_` next to the executable): it applies to the editor,
  not to exported games; the portable build keeps its data in `%APPDATA%` like the installed one, which also lets a
  player switch between them.

## Consequences

* Every build is unsigned until ADR 0242's secrets exist; SmartScreen warns.
* The package holds no original content; `check-no-original-files.sh` keeps guarding the repository, and the import
  writes only under the user's data.
* Godot rewrites `export_presets.cfg` when the preset is edited in the editor; keep its comment block and the options.
* Follow-ups: an icon (none yet, `application/icon` is empty), PE version resources in `Tzar2.exe`
  (`modify_resources`), signing the uninstaller, the reproducibility check made a failure once two exports match.
