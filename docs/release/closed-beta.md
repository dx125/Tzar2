# Closed beta checklist (M7)

How the M7 closed beta with veteran players runs: the build channel, the feedback route, the bug template, and the
checklist before, during and after. Part of the M7 plan (ADR 0240). Exit criteria (TDD §25): **beta crash-free rate
≥ 99.5 %, S2 met, no open P0/P1 bugs.**

## Build channel

* **Builds** come from the `Windows package` workflow (ADR 0241): the nightly run on `main`, or a manual run
  (Actions → Windows package → Run workflow). Each run uploads `tzar2-windows-<run>` with the installer and the
  portable zip, kept 30 days. Nothing is published; the repository is public, so GitHub Releases would make the beta
  public, and are not used.
* **A beta build** is a green run the maintainer names in the beta log (below) with its version
  (`0.7.0-beta.<run>+<commit>`) and its SHA-256 sums (in the run's summary). The owner downloads the artifact and puts
  the installer and the zip into the beta's private shared folder (Google Drive, Yandex Disk or the chat's files;
  the owner's choice), with the sums.
* **Cadence:** a new beta build at most once a week, unless a P0 fix; every build's notes list the issues it fixes.
  Testers keep their data across builds (the installer upgrades in place; the data lives in `%APPDATA%`, ADR 0241).

## Feedback route

* **Chat:** one private group (Telegram or Discord; the owner's choice) for the testers, the owner and the maintainer's
  coordinator. Quick questions and impressions go there.
* **Bugs:** a GitHub issue with the **Beta bug** form (`.github/ISSUE_TEMPLATE/beta-bug.yml`) for testers who have a
  GitHub account; for those who do not, the same fields as a message in the chat with the files attached, which the
  owner or the coordinator files as an issue. Every bug gets the `beta` label and a severity label.
* **Crash reports:** opt-in (ADR 0243). With consent and an endpoint they arrive by themselves; without an endpoint
  they stay in the tester's `crash-reports\outbox`, which the tester zips and sends with the bug.

### Severity

| Label | Meaning | Beta exit |
|---|---|---|
| `P0` | crash, data loss (saves, replays, settings), a desync in multiplayer, the game does not start or import | none open |
| `P1` | a rule of the original broken in a way that changes play, a stuck campaign mission, a broken control with no workaround | none open |
| `P2` | wrong but playable: visuals, sounds, texts, a control with a workaround | triaged |
| `P3` | polish and wishes | triaged |

## Before the beta

- [ ] M7 plan accepted (ADR 0240); the M3 fifth play-test findings fixed (work queue 5).
- [ ] A green `Windows package` run with installer and zip; install, first start, import and uninstall checked on the
      owner's PC (min-spec plan, request 2).
- [ ] Crash reporting endpoint decided and, if it is a server, deployed with the coordinator's approval
      (ADR 0243 "Endpoint"); `CRASH_REPORT_DSN` repository variable set; one test report seen on the server.
- [ ] Code signing: certificate bought and the secrets set (code-signing.md), or the invitation explains SmartScreen.
- [ ] Labels `beta`, `P0`–`P3` exist; the Beta bug form works.
- [ ] The private folder and the chat exist; 5–15 veteran players invited, each with: the build, the README, the
      invitation text (what to test, how to report, that it is confidential and contains no original data).
- [ ] The testers own the original game (GOG or Steam): the build needs it to import.

## During the beta

- [ ] Every week: crash-free sessions per build (server's release health), new P0/P1 issues, the testers' hardware.
- [ ] P0 within 48 hours to a fix in a new build; P1 in the next weekly build.
- [ ] Multiplayer evenings with the relay (ADR 0160), at least two, with replays kept.
- [ ] Beta log: one line per build and per decision in `docs/STATUS.md`.

## Exit

- [ ] Crash-free sessions ≥ 99.5 % over the last two builds, at least 500 sessions (otherwise the rate says little).
- [ ] S2 met on the reference tier (min-spec plan; D6 render bench) and the sim nightly gate green.
- [ ] No open P0 or P1.
- [ ] The beta's findings that are not bugs are in the work queue for M8.

## Bug template

The GitHub form (`.github/ISSUE_TEMPLATE/beta-bug.yml`) asks for these; the chat version is the same list:

1. **Version** (`version.txt` in the game folder, or the main menu's corner).
2. **What you did** (steps), **what you expected**, **what happened**.
3. **How often** (always, sometimes, once) and **severity** in your view.
4. **Mode**: skirmish, campaign (which mission), multiplayer, editor.
5. **Files**: the run's log folder (`%APPDATA%\Godot\app_userdata\Tzar2\logs\<date-time>\`, zipped), the replay
   (`replays\` newest `.t2rep`, or `crash-replay.t2rep` in the log folder), a screenshot if it is visual.
6. **PC**: Windows version, CPU, graphics card, screen resolution (the log's first lines hold them).
