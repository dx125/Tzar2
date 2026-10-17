# Spike S3: LiteNetLib relay lockstep prototype

Question (TDD §25 M0, DEC-08, §19.1-19.2): does deterministic lockstep through a LiteNetLib relay give a steady
20 Hz tick, acceptable input latency, ≤ 2 KB/s per client and working desync detection and reconnect, for 2-8
players at 0-250 ms RTT and 0-5 % loss? Result and decision: [ADR 0009](../../docs/decisions/0009-spike-s3-lockstep-relay.md).

Throwaway code; not part of `Tzar2.sln`. The reusable part (RTT estimator and adaptive input delay) is in
`src/Tzar2.Net/Lockstep/` with tests in `tests/Tzar2.Net.Tests`.

| Project | Content |
|---|---|
| `S3.Sim` | Toy deterministic sim: 64 units per player walking in `Fixed` coordinates, combat RNG from `RngStreams`, xxHash64 state hash via `StateHasher`, binary snapshot. Built with `Tzar2Deterministic` + `Tzar2NoIO`, so the determinism analyzers apply. |
| `S3.Lockstep` | Wire protocol (bounded decoding), `RelayServer` (lobby by code, deadline-based tick closing, hash comparison, rejoin), `LockstepClient` (bot input, batches, adaptive D, closed-tick barrier, RTT, hash reports, snapshot/rejoin). |
| `S3.Host` | `relay` / `client` processes, a UDP impairment proxy per client (delay, jitter, loss), and the harness: `bench`, `matrix`, `faults`. |

LiteNetLib's built-in `SimulatePacketLoss`/`SimulateLatency` only exist in DEBUG builds of the library
(`#if DEBUG || SIMULATE_NETWORK`); the NuGet package is a release build. The harness therefore impairs the real UDP
datagrams in a proxy, like `tc netem`: one-way delay = RTT/2, jitter ±5 % of the RTT per direction (uniform, may
reorder), loss independent per datagram and direction.

## Run

```sh
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
dotnet build spikes/S3-net/S3.Host -c Release
S3=spikes/S3-net/S3.Host/bin/Release/net10.0/S3.Host

$S3 bench --clients 4 --rtt 100 --loss 5 --seconds 20 --delivery redundant   # one scenario
$S3 bench --clients 4 --rtt 0,0,0,250 --loss 0,0,0,5                         # per-client conditions
$S3 bench --clients 3 --docker 1=linux/arm64,2=linux/amd64                   # clients 1, 2 in Linux containers
$S3 matrix --seconds 20             # 2/4/8 clients x 0/100/250 ms x 0/5 % x reliable/redundant -> results/matrix.md
$S3 matrix --deliveries redundant --clients 8 --hash-interval 10 --table matrix-hash10.md
$S3 faults                          # rejoin, forced desync, slow player, Linux clients     -> results/faults.md
```

Every run writes `relay.log`, `client-N.log` and JSON reports under `spikes/S3-net/results/<scenario>/` (git-ignored);
`matrix.md` and `faults.md` are kept.

## Protocol sketch

```
client                      relay (per match)                         other clients
Hello(Create n | Join code | Rejoin slot token)  ->  Welcome(code, slot, token, seed, tick, hashInterval, delivery)
Ready(hash of initial state)                     ->  all ready + equal hashes: Start   (loading barrier, REQ-NET-05)
every tick: Batch(seq, target = estOpenTick + D, ackTick, commands[], hashes[])  (empty = heartbeat)
                            tick T closes at start + (T+1) * 50 ms; batches for closed ticks go to the next open
                            tick (+ Late notice); ordered commands of T -> Closures(batchAck, [T ...]) -> everyone
client executes T only when Closures(T) arrived ("tick T complete"); reports hash(T) in its next batch
                            relay compares hashes once every live peer reported T -> Desync(T, majority, oddSlots)
Rejoin: relay asks a healthy peer for Snapshot(S, hash, state); sends Resume(S, hash, state, closures S+1..now)
```

`reliable`: one closure per tick and one batch per tick on LiteNetLib `ReliableOrdered`. `redundant`: unreliable
datagrams that repeat everything not yet acknowledged (closures since the client's `ackTick`, batches since the relay's
`batchAck`); no retransmission timers, no head-of-line blocking.
