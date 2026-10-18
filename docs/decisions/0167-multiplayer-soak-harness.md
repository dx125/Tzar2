# 0167. Soak multiplayer with headless bot matches through the real relay over impaired loopback UDP

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M6)
* **Related:** TDD §22.7, §25 M6 exit criteria, S8, REQ-NET-02…07, REQ-NET-09, ADR 0009 (proxy), ADR 0160, ADR 0162-0164

## Context

M6 exits on "500-match soak with 0 desyncs; reconnect works; replays verify on Windows and in the headless Linux/macOS
core runs". §22.7 describes the soak as "headless bot clients through the relay with simulated 200 ms RTT, 5 % loss
and jitter; 500 two-hour matches with zero desyncs (S8); reconnect and drop-policy scenarios". The relay closes ticks
on the wall clock, so a two-hour match takes two hours of real time at 1x speed (38 minutes at the maximum 4x).

## Decision

* **`t2 soak`** (`Tzar2.Tools.Cli/Soak`) runs N matches, several at a time, against **one in-process `RelayServer`**
  (the production relay, ADR 0160), each player a real `NetworkMatch` (ADR 0162) over loopback UDP behind its own
  **impairment proxy** (from spike S3: one-way delay RTT/2, uniform jitter, independent loss per datagram and
  direction; LiteNetLib's own simulation is compiled out of its release build).
* A match (defaults): 2 bot players and 2 Hard AIs on the 96² skirmish test map (or `--map random`: small continental,
  lakes and dry maps), 6,000 ticks (5 minutes of game time) at 4x speed, 200 ms RTT, 10 ms jitter, 5 % loss. Bots give
  an order every 2-4 s of game time (gather, train, attack, move) through `GameSession.Submit`, so every order takes
  the full lockstep path.
* **Disconnects**: in a share of the matches (default half) one player "crashes" at a random tick between 20 % and
  60 % of the match (its `NetworkMatch` is disposed) and reconnects 0.5 s to 2× the drop timeout later
  (`NetworkMatch.Rejoin` with its ticket). With the 3 s timeout of the soak, about half of the reconnects come after
  the computer took the kingdom over; those check the takeover and the hand-back as well.
* **Verdict per match**: at the end a player pauses the match (the relay stops closing ticks), every peer executes
  every closed tick, then every peer's **full-state hash must be identical**; the relay must have reported **no
  desync**; and every recorded replay (rejoined peers do not record, ADR 0164) is **re-simulated headlessly** and must
  match every stored hash. A match that fails to start or times out counts as a failure. `--report` writes JSON with
  per-match results (ticks, rejoins, takeovers, late batches, mean input delay, bandwidth per client).
* **CI**: every PR records a corpus on Linux (a single-player replay of three Hard AIs plus two soak matches with a
  disconnect each) and verifies it on Windows, Linux and macOS (`replay-verify`). The nightly workflow runs a 20-match
  slice on Linux and Windows (replacing the spike S3 job). Windows raises the timer resolution to 1 ms for the soak.

## Alternatives considered

* **Separate processes per player:** closer to reality, but the spike already measured that; one process lets a
  laptop run dozens of matches at once and keeps failures debuggable.
* **Simulated network inside LiteNetLib:** not in its release build (ADR 0009).
* **Two-hour matches:** the requirement's wording, but 500 of them is about 330 hours of wall-clock time at 4x speed;
  the soak runs 5-minute matches by default and takes `--ticks` for longer ones. The total ticks over all matches are
  reported.

## Consequences

* The soak finds lockstep, relay, rejoin, takeover and replay divergences, but not platform differences (all peers of a
  match run on one OS); the cross-OS check is the replay corpus verified on three OSes plus the determinism matrix.
* At 4x speed and 200 ms RTT the adaptive input delay sits near its 10-tick maximum, and the per-client bandwidth is
  four times that of a 1x match (ADR 0009 measured ~2 KB/s at 1x).
