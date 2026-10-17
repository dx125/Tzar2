PASS fault-rejoin: snapshot verified=True, away 3000 ms, then reconnect-to-caught-up 250 ms with 2 ticks fast-forwarded; relay served resume 115 ms after hello; final hashes agree=True; others' stalls=4 (max gap 153 ms)
PASS fault-desync: detected at tick 150, odd slots [1], 106 ms after the relay closed the tick; match ended at tick 152
PASS fault-slow-player: fast players: stalls=0, D max=2, latency 174 ms; slow player: D mean=4.88, latency 426 ms, stalls=0; hashes agree=True
PASS linux-docker: platforms [macOS 26.5.2 Arm64 | Ubuntu 24.04.5 LTS Arm64 | Ubuntu 24.04.5 LTS X64], 395 tick hashes compared, desyncs=0, final 399:7E2303FD6C815BA9 agree=True
