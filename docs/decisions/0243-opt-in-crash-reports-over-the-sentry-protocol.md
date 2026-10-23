# 0243. Report crashes opt-in over the Sentry protocol, with sessions for the crash-free rate

* **Status:** Accepted (the endpoint is open: nothing is sent until the owner picks one and it is configured)
* **Date:** 2026-10-22
* **Deciders:** maintainer (Claude); Q-06's default (opt-in, Sentry protocol, self-hosted-capable)
* **Related:** TDD §4.3 S8, §20 REQ-PLAT-01, REQ-PLAT-04, §25 M7, §27 Q-06; ADRs 0176, 0240, 0241

## Context

M7's exit is a beta crash-free rate of at least 99.5 %, which needs crashes and the sessions they are a share of to
reach the developers. Q-06 defaults to opt-in reporting over the Sentry protocol, so that sentry.io or a server of our
own (Sentry self-hosted, GlitchTip) can receive it. REQ-PLAT-04: opt-in, no personal data, the last commands and
system info. ADR 0176 already writes, per run, a log folder with `game.log`, `crash-*.txt` on an unhandled .NET
exception, and the crashed match's replay (which holds the last commands and keyframes). Two kinds of crash escape
.NET's handler: a native crash of Godot (the process dies; Godot's own crash handler prints a backtrace into
`user://logs/godot.log`), and the .NET exceptions Godot catches in our callbacks (logged, the game goes on).

## Decision

1. **Consent first.** `crash-reporting.cfg` in the user's data holds `consent` (unasked, granted, denied),
   `attach_replay`, `dsn` and `environment`. The first non-scripted start asks once (closing the question refuses);
   SETTINGS has "Send crash reports" and "Attach match replay". **Nothing is sent unless consent is granted and an
   endpoint is configured.** Refusing deletes the waiting reports and stops new ones. Scripted runs (smoke tests,
   captures, autoplay) do not report at all.
2. **What is reported** (`CrashReporter`, `SentryEnvelope` in `Tzar2.Game.Diagnostics`; the client hooks it into
   `ClientLog`):
   * a **managed crash** (unhandled .NET exception): a `fatal` event with the exception chain and frames (file names
     without folders, line numbers from the shipped `.pdb`s), the last 300 log lines, and the match replay if the
     player opted into replays (≤ 8 MB);
   * a **.NET exception Godot caught** in our callback (its text in Godot's error log): an `error` event, each text once
     and at most 10 per run;
   * a **run that never quit** (its log folder still has `session.running` at the next start): a native Godot crash
     when Godot's log shows its crash handler's backtrace, otherwise an abnormal exit (killed, hung, power cut); a
     `fatal` event with the tails of that run's `game.log` and Godot's log;
   * **every run's end as a session** (`exited`, `crashed` or `abnormal`, with the error count): Sentry's release health
     counts crash-free sessions from these, which is the M7 exit figure.
3. **No personal data.** No user or machine name, no account, no IP field; folder names under `Users`/`home` become
   `<user>` in every text; system info is OS and version, CPU, thread count, GPU and vendor, Godot version, renderer.
   The replay is a separate opt-in because it holds the players' in-game names. The server should also be told not to
   store IP addresses (a project setting on Sentry and GlitchTip).
4. **Delivery.** Reports are written at once as envelope files into `crash-reports/outbox` (a crashing process does no
   networking) and sent at the next start, or right after consent, in the background over HTTPS to
   `<dsn host>/api/<project>/envelope/` with `X-Sentry-Auth`. Sent or rejected (4xx) envelopes are deleted; offline,
   429 and 5xx keep them for the next start; at most 100 wait, none older than 30 days.
5. **Endpoint** (in this order): `TZAR2_CRASH_DSN`, the `dsn` of `crash-reporting.cfg`, then `crash-reporting.dsn` next
   to the executable, which the package workflow writes from the repository variable `CRASH_REPORT_DSN` (a DSN is not a
   secret: it only allows sending). Empty everywhere today: **no server is deployed or chosen**.
6. **Release and environment:** `tzar2@<GameInfo.Version>`; `beta` for a `-beta` build, `development` otherwise.

## Alternatives considered

* **The Sentry .NET SDK:** a dependency that hooks .NET globally, adds its own transport, caching and PII options, and
  still does not see Godot's native crashes; the envelope format is small and stable, and our own writer keeps
  `Tzar2.Game` free of packages (DependencyRulesTests).
* **Godot's native crash handler uploading minidumps (Crashpad/Breakpad):** needs a custom Godot build with symbols
  for each release; the "run that never quit" mark plus Godot's printed backtrace covers the M7 need. Revisit if
  native crashes dominate the beta.
* **Send at crash time:** a crashing process may hang on the network; the outbox survives power cuts and offline PCs.
* **Opt-out:** Q-06 and REQ-PLAT-04 say opt-in.

## Consequences

* The crash-free rate counts consenting testers only; the beta asks testers to agree (closed-beta.md).
* **Server choice for the owner** (deployment needs the coordinator's approval): sentry.io's free plan (release
  health included, nothing to run) or a self-hosted server on the project's machine — GlitchTip (light, Sentry
  protocol; check that its version ingests session envelopes, else the crash-free rate needs sentry.io) or Sentry
  self-hosted (complete, but heavy: about 16 GB RAM).
* A killed process and a native crash look alike when Godot wrote no backtrace; the report says "abnormal exit" then.
* Follow-ups: a game that crashes within seconds of every start may die before the background send finishes; show the
  player the outbox in the settings.
