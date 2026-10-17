# 0009. Lockstep through a deadline-closing relay with redundant unreliable delivery over LiteNetLib

* **Status:** Accepted (spike result; protocol details are revisited in M6)
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M0, spike S3)
* **Related:** DEC-08, DEC-09, REQ-NET-01/02/03/04/05/06/11, REQ-SIM-12, TDD §7.4, §19.1-19.2, §22.7, §25 M0 (S3),
  `spikes/S3-net/`, ADR 0005

## Context

S3 asks whether lockstep over UDP through a relay that orders and forwards commands (DEC-08) gives a steady 20 Hz
tick, acceptable input latency and ≤ 2 KB/s per client (REQ-NET-03), keeps one slow player from freezing the others
(§19.2), and supports desync detection (REQ-NET-04) and rejoin (REQ-NET-06). The exit criterion is "Windows ↔
Windows plus a headless Linux client"; only macOS arm64 was available locally, with Docker.

What was built (`spikes/S3-net/`, see its README):

* **Transport:** LiteNetLib **2.1.4** from NuGet (MIT, checked in the nuspec and repository `LICENSE.txt`; targets
  net8.0 and netstandard2.1, no dependencies), pinned in `Directory.Packages.props`.
* **Toy sim:** 64 units per player in `Fixed` coordinates, combat RNG from `RngStreams`, xxHash64 state hash via
  `StateHasher`, binary snapshot. It is built with `Tzar2Deterministic` + `Tzar2NoIO`, so the determinism analyzers apply.
* **Relay:** lobby created/joined by a 6-character code; loading barrier (all `Ready` hashes equal, then 1.5 s
  countdown); tick T closes at `start + (T+1) × 50 ms` whether or not everyone sent input; a batch for a closed tick goes
  to the next open tick and the sender gets a `Late` notice; per-tick hash comparison with majority vote; rejoin with
  a session token: snapshot from a healthy peer + every closed tick since. The relay stamps the slot on every command.
* **Client:** bot input (~2 commands/s); one batch per tick with `target = estimated open tick + D` (empty = heartbeat);
  executes tick T only after the relay's closure of T arrived; RTT by app-level ping (5 Hz, also during the countdown);
  adaptive D from `Tzar2.Net.Lockstep.InputDelayController`; hash report for every executed tick.
* **Fault injection:** LiteNetLib's `SimulatePacketLoss`/`SimulateLatency` are compiled only into DEBUG builds of the
  library (`#if DEBUG || SIMULATE_NETWORK`); the NuGet package is a release build, so they do nothing. The harness puts
  a UDP proxy in front of each client instead: one-way delay = RTT/2, jitter ±5 % of RTT per direction (uniform, may
  reorder), independent loss per datagram and direction. Plus a client crash/reconnect, a forced one-unit state
  corruption, and Linux clients in Docker.
* **Two delivery modes compared:** `reliable` = closures and batches on LiteNetLib `ReliableOrdered`; `redundant` =
  unreliable datagrams that repeat everything not yet acknowledged (closures since the client's `ackTick`, batches
  since the relay's `batchAck`), with no retransmission timer.

### Measurements

macOS 26.5 arm64 (Apple Silicon), relay + clients as separate local processes, 20 s per run (400 ticks), hashes every
tick, bot 2 commands/s/player. Bandwidth = UDP payload + 28 B IPv4/UDP header per datagram, measured at the proxy, mean
per client. "Stalls" = gaps > 150 ms (3 ticks) between two executed ticks, summed over all clients. Latency = own
command from issue to execution. Full tables: `spikes/S3-net/results/matrix.md`, `matrix-hash10.md`.

**Redundant delivery (chosen):**

| Clients | RTT ms | Loss % | Tick rate min Hz | Stalls (sum ms) | Max gap ms | Latency mean / p95 ms | D mean | Up / down KB/s |
|---|---|---|---|---|---|---|---|---|
| 2 | 0 | 0 | 20.00 | 0 | 57 | 173 / 198 | 2.00 | 1.24 / 1.05 |
| 2 | 0 | 5 | 20.00 | 0 | 102 | 174 / 195 | 2.00 | 1.30 / 1.02 |
| 2 | 100 | 0 | 20.00 | 0 | 60 | 276 / 296 | 3.00 | 2.09 / 1.29 |
| 2 | 100 | 5 | 20.00 | 0 | 142 | 275 / 302 | 3.00 | 2.09 / 1.22 |
| 2 | 250 | 0 | 19.99 | 0 | 128 | 426 / 464 | 5.00 | 3.11 / 1.55 |
| 2 | 250 | 5 | 19.99 | 0 | 119 | 441 / 492 | 4.87 | 3.09 / 1.49 |
| 4 | 0 | 0 | 20.00 | 0 | 53 | 177 / 200 | 2.00 | 1.32 / 1.18 |
| 4 | 0 | 5 | 20.01 | 1 (100) | 150 | 176 / 200 | 2.00 | 1.35 / 1.13 |
| 4 | 100 | 0 | 20.00 | 0 | 62 | 272 / 299 | 3.00 | 2.03 / 1.39 |
| 4 | 100 | 5 | 20.00 | 3 (307) | 156 | 276 / 334 | 3.00 | 2.11 / 1.44 |
| 4 | 250 | 0 | 20.00 | 0 | 75 | 430 / 487 | 4.88 | 3.08 / 1.86 |
| 4 | 250 | 5 | 19.99 | 1 (165) | 215 | 426 / 491 | 4.88 | 3.10 / 1.75 |
| 8 | 0 | 0 | 20.00 | 0 | 68 | 175 / 199 | 2.00 | 1.38 / 1.33 |
| 8 | 0 | 5 | 20.00 | 5 (504) | 151 | 174 / 258 | 2.00 | 1.33 / 1.25 |
| 8 | 100 | 0 | 20.00 | 0 | 62 | 274 / 301 | 3.00 | 2.03 / 1.63 |
| 8 | 100 | 5 | 20.00 | 2 (207) | 155 | 273 / 322 | 3.00 | 2.07 / 1.58 |
| 8 | 250 | 0 | 19.99 | 0 | 79 | 419 / 480 | 4.85 | 3.09 / 2.33 |
| 8 | 250 | 5 | 19.99 | 3 (323) | 168 | 419 / 501 | 4.84 | 3.14 / 2.31 |

Same, 8 clients, **hashes every 10 ticks** (REQ-NET-04): up / down 1.14 / 1.35 KB/s at 0 ms, 1.51 / 1.66 at 100 ms
5 %, 2.06 / 2.28 at 250 ms 5 %; tick rate, stalls and latency unchanged.

**Reliable delivery (LiteNetLib `ReliableOrdered`, default resend settings), selected rows:**

| Clients | RTT ms | Loss % | Tick rate min Hz | Stalls (sum ms) | Max gap ms | Latency mean / p95 ms | D mean (max) | Late batches |
|---|---|---|---|---|---|---|---|---|
| 4 | 0 | 0 | 20.00 | 0 | 54 | 173 / 198 | 2.00 (2) | 0 |
| 4 | 100 | 0 | 19.99 | 0 | 60 | 275 / 303 | 3.00 (3) | 0 |
| 2 | 0 | 5 | 19.54 | 25 (12,404) | 1,522 | 385 / 1,527 | 2.00 (2) | 0 |
| 4 | 100 | 5 | 19.21 | 69 (30,649) | 2,095 | 527 / 1,728 | 3.90 (7) | 19 |
| 8 | 250 | 5 | 19.44 | 121 (72,086) | 2,685 | 856 / 2,642 | 6.63 (10) | 110 |

Without loss both modes are equivalent; with 5 % loss reliable-ordered head-of-line blocking produces stalls of
1-2.7 s, late batches and D climbing to the maximum.

**Faults** (`spikes/S3-net/results/faults.md`, redundant mode):

* **Rejoin** (4 clients, 100 ms, 5 %): slot 2 kills its socket at tick 100 and reconnects after 3 s. The relay asks
  slot 0 for a snapshot (9.5 KB), sends it with the 2 ticks closed since; the client verifies the snapshot hash,
  fast-forwards and is live **250 ms** after reconnecting (relay served the resume 115 ms after the hello). Final
  hashes of all 4 agree; the other three players saw no stall attributable to the drop (max gap 153 ms).
* **Forced desync** (4 clients, 50 ms): slot 1 shifts one unit by one raw `Fixed` unit after tick 150. The relay
  reports **tick 150, odd slot [1]**, 106 ms after closing that tick, and ends the match.
* **Slow player** (RTTs 0/0/0/250 ms, the slow one with 5 % loss): the three fast players keep D = 2, 174 ms latency,
  0 stalls; the slow player runs with D ≈ 4.9 and 426 ms latency. One slow player does not freeze the others.
* **Headless Linux** (3 clients, 100 ms, 5 %): macOS arm64 + Ubuntu 24.04 **arm64** container + Ubuntu 24.04 **x64**
  container (emulated): 395 tick hashes compared, 0 desyncs, identical final hash. This also gives first evidence for
  S2 (Fixed + RNG + xxHash64 identical on x64 and arm64).

## Decision

* **Topology and tick authority** as DEC-08/§19.2: the relay closes tick T at its deadline `start + (T+1) × tick`
  without waiting for anyone; late batches move to the next open tick (+ `Late` notice); peers execute T only after
  the relay's closure of T arrived. The relay never simulates; it compares hashes and serves rejoins.
* **Delivery:** closures and command batches travel as **unreliable datagrams that repeat everything not yet
  acknowledged** (ack fields piggyback on the per-tick traffic; tails are repeated each tick until acknowledged, also
  after the match end). LiteNetLib `ReliableOrdered` is used only for rare or large messages: welcome, start, late,
  desync, end, snapshots/resume, and as fallback when a redundant packet would exceed the MTU. Chat (REQ-NET-03) gets
  its own reliable channel later.
* **Transport library:** LiteNetLib 2.x (MIT) stays the transport (DEC-08). We use it for connection management,
  MTU, fragmentation of snapshots and the reliable control channel; lockstep reliability is ours.
* **Adaptive input delay:** per client (not global), `D = clamp(1 + ceil((SRTT/2 + 2 × RTTVAR + 5 ms) / tick), 2, 10)`
  with RFC 6298 SRTT/RTTVAR from an app-level ping, measured from the lobby/countdown onwards; D rises at once,
  falls one tick per 40 ticks (2 s) of a lower target; a `Late` notice raises it by one at most every 10 ticks.
  Implemented in `src/Tzar2.Net/Lockstep/` (`RttEstimator`, `InputDelayController`) with unit tests.
* **Desync detection:** state hash every **10 ticks** in production (REQ-NET-04; every tick only in debug sessions and
  tests). The relay compares once every live peer has reported a tick (or after 2 s), takes the majority and names
  the odd slots.
* **Rejoin:** snapshot from a healthy peer at its last executed tick S, hash-checked by the rejoiner, plus the relay's
  log of closures after S (REQ-NET-06). The relay keeps closures since the last keyframe.
* **Security baseline:** the relay overwrites the player id of every command with the sender's slot; session token
  per slot for rejoin; all decoders bounded (REQ-NET-11).
* **Clock:** on Windows the client and relay raise the timer resolution to 1 ms (`timeBeginPeriod(1)`); otherwise
  `Thread.Sleep(1)` sleeps 15.6 ms and tick deadlines jitter accordingly.

## Alternatives considered

* **LiteNetLib `ReliableOrdered` for everything:** simplest, and fine on a clean network, but measured 1-2.7 s stalls
  and 1-2.6 s p95 latency at 5 % loss (head-of-line blocking; LiteNetLib resends after `25 ms + 2.1 × RTT`). Tuning
  `ResendRttMultiplier` would shorten, not remove, the blocking.
* **LiteNetLib's built-in network simulation for the tests:** not available in the release NuGet build (see context);
  a proxy also tests the real wire format and works for any transport, including Steam later.
* **Global input delay agreed by all peers:** the slowest link then sets everyone's latency; with deadline closing
  per-client D is safe and keeps fast players at 2 ticks (slow-player scenario).
* **Waiting barrier (relay closes T only when all inputs arrived):** that is the original's "waiting for response"
  freeze (§19.2), rejected by the TDD.
* **Rejoin by replaying the whole command log from tick 0:** simpler, but cost grows with match length; the snapshot
  path took 250 ms.

## Consequences

* Input latency is about `(D + 1.5) × 50 ms + RTT/2`: ~175 ms on LAN, ~275 ms at 100 ms RTT, ~425 ms at 250 ms RTT.
  The batch send phase is currently not aligned to the relay deadline; aligning it should save ~25 ms on average
  (M6 follow-up).
* Bandwidth meets the 2 KB/s typical target up to ~100 ms RTT with hashes every 10 ticks; at 250 ms RTT the
  redundant upstream repeats ~6 batches and reaches ~2.1 KB/s up / 2.3 KB/s down for 8 players, below the 16 KB/s peak
  limit. If needed: delta-encode repeated batches, ack per datagram instead of per tick, drop the separate ping in
  favour of timestamp echoes. A third of the traffic is IP/UDP headers at 20 datagrams/s.
* The relay keeps per-match state that grows with the closure log; M6 bounds it to the last keyframe interval.
* The spike stays out of `Tzar2.sln`; only `Tzar2.Net.Lockstep` is production code. The protocol, message layout and
  relay are re-implemented in M6 in `Tzar2.Net`/`Tzar2.Server` using this ADR as the specification; LiteNetLib then
  enters the solution and the CI license check.
* **Windows ↔ Windows** was not run locally (no Windows machine). The nightly workflow job `spike-s3-net` runs the
  forced-desync and rejoin scenarios (strict) and a 4-client measurement (informational) on `windows-latest` and
  `ubuntu-latest`. The S3 exit criterion is fully met when that job is green; revisit this ADR if it shows different
  numbers on Windows.
* Not covered yet: real WAN paths (bursty loss, asymmetric routes), NAT rebinding, relay restarts, drop policy and
  AI takeover (REQ-NET-07), game-speed changes, pause, chat, LAN discovery, the 500-match soak (§22.7, M6).
