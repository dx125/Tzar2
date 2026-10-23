# 0240. Plan M7 (Windows beta) as seven deliverables with measured exit criteria

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** maintainer (Claude), coordinator; the owner's choices are listed below
* **Related:** TDD §4.3 (S2, S8), §12 (REQ-IMP), §20 (REQ-PLAT-01…04), §21, §25 (M7), §27 Q-01, Q-06; ADRs 0010, 0080,
  0176, 0241, 0242, 0243; work queue item 13

## Context

M7 of TDD §25: "Windows installer and portable build, code signing, crash reporting, performance tuning on Windows
min-spec hardware, closed beta with veteran players", exit "beta crash-free rate ≥ 99.5 %; S2 met; no open P0/P1 bugs".
Before this ADR there was no export preset, no package, no crash reporter beyond local `crash-*.txt` files (ADR 0176),
no first-run import in the client (the first-run screen told the player to run `t2 import`) and no performance
measurement on a real Windows desktop: the S2 sim proxy runs nightly on 4-vCPU Linux runners (ADR 0080; p99 3.8–5.9 ms
against 12 ms), the render half of S2 was last measured by spike S1 on the owner's RTX 4070 Ti (ADR 0010).

M3–M6 are still open (fidelity, campaigns, soak), so the beta cannot start yet; the infrastructure can.

## Decision

M7 is these deliverables. Each has a check that says it is done.

| # | Deliverable | Done when | State |
|---|---|---|---|
| D1 | **Windows package** (ADR 0241): reproducible Godot headless export, portable zip, Inno Setup installer, version stamp, uninstall that keeps the player's data | `Windows package` workflow green: the exported, the installed and the unzipped game start; install/uninstall leaves `%APPDATA%\Godot\app_userdata\Tzar2` intact; two exports compared | this PR |
| D2 | **First-run import** in the client (REQ-IMP, TDD §12.2): find GOG/Steam/folder installs, import into the user's data, open | the first-run screen imports; `--import <dir> --smoke` prints `FIRST-RUN IMPORT OK`; ≤ 3 min on the owner's PC | this PR (owner's timing pending) |
| D3 | **Code signing** (ADR 0242): a pipeline step skipped without secrets | self-test signs on every run; a real certificate signs once the owner provides it | this PR (certificate: owner) |
| D4 | **Crash reporting** (ADR 0243): opt-in, Sentry protocol, configurable endpoint, managed and Godot crashes, sessions for the crash-free rate | unit tests; the exported game queues and does not send without consent; a test report reaches the chosen server | this PR (server: owner/coordinator) |
| D5 | **Min-spec tuning** ([min-spec-tuning.md](../release/min-spec-tuning.md)): three tiers, the settings, the benches, the owner's runs | the reference tier meets S2 (both halves) and the minimum tier is measured and published | plan in this PR; runs pending |
| D6 | **Client render bench** (`--bench-render`): the S2 scenario in the real client, frame times, sim p99, memory, load time | numbers for 1080p/1440p/2160p × Mobile/Compatibility on the owner's PC | next PR |
| D7 | **Closed beta** ([closed-beta.md](../release/closed-beta.md)): build channel, feedback route, bug form, checklist | the checklist's "Exit" list is ticked | after M3–M6 allow it |

**Exit criteria of M7** (TDD §25, made measurable):

* **Crash-free rate ≥ 99.5 %** = crashed + abnormal sessions ≤ 0.5 % of all sessions (ADR 0243's session envelopes,
  counted by the crash server's release health) over the last two beta builds, with at least 500 sessions. Sessions of
  testers who did not consent are not counted; the rate is over consenting testers.
* **S2 met**: on the reference tier (Ryzen 5 3600 / GTX 1060 class, 2560 × 1440), the S2 scenario renders at ≥ 60 FPS
  (D6) and the sim tick p99 is ≤ 12 ms (`t2 bench --scenario s2` on that CPU, and the nightly gate stays green).
* **No open P0/P1** with the severity scale of closed-beta.md.

**Order:** D1–D4 now (this PR); the owner's sim bench and installed-build runs (min-spec plan, requests 1 and 2);
D6; the endpoint and the certificate when the owner decides; D7 once M3's fifth play-test is fixed and M4/M6 are far
enough that veterans would play campaigns and multiplayer.

## Alternatives considered

* **Start the beta now with what plays:** M3's play-tests still find rule gaps every round; veterans would report what
  the work queue already knows. The infrastructure goes first so that the beta starts the day the game is ready.
* **Telemetry beyond crashes (play time, features used):** not asked for by TDD (Q-06 is about crash reporting); each
  extra field is a consent question. Sessions (start, end, how it ended) are the minimum the crash-free rate needs.
* **A public beta (GitHub Releases, itch.io):** the repository is public, so any release there is public; TDD asks for a
  closed beta with veterans. Builds stay workflow artifacts handed out by the owner.

## Consequences

* `main` gets a Windows package every night (the nightly `Windows Godot build` job is now the package workflow).
* The version is `0.7.0` (Directory.Build.props) with `-beta.<run>+<commit>` from the workflow; the lobby marks a
  game of another version, so testers of a multiplayer evening must run the same build.
* Open until the owner decides: the crash-report server (ADR 0243), the signing certificate (ADR 0242), the beta's chat
  and shared folder (closed-beta.md), the testers.

## Questions for the product owner

* **Crash reports:** sentry.io's free plan or a server of our own (GlitchTip or Sentry on the project's server)? The
  build sends nothing until this is decided and set.
* **Code signing:** which route of code-signing.md, in whose name (person or company)?
* **Beta:** which chat (Telegram or Discord), which shared folder, and which veterans (5–15)?
