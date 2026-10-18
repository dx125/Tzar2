# Tzar2.Server: relay and lobby service

The headless relay/lobby service of TDD §19.7 ([ADR 0161](../../docs/decisions/0161-relay-server-host-http-api-and-container.md)).
It hosts `Tzar2.Net.Relay.RelayServer` ([ADR 0160](../../docs/decisions/0160-relay-lobby-and-lockstep-protocol-in-tzar2-net.md)),
the same code a LAN host embeds: lobbies by code, chat, the loading barrier, the lockstep tick authority, desync
reports, rejoin, drop policy, saves and pauses. It never simulates and needs no game content.

## Run

```sh
dotnet run -c Release --project src/Tzar2.Server -- --udp-port 47625 --http-port 8080

# or as a container (build context = repository root)
docker build -f src/Tzar2.Server/Dockerfile -t tzar2-server .
docker run -p 47625:47625/udp -p 8080:8080 -v tzar2-data:/data tzar2-server
docker compose -f src/Tzar2.Server/compose.yaml up -d --build
```

| Option | Environment | Default | |
|---|---|---|---|
| `--udp-port` | `TZAR2_UDP_PORT` | 47625 | relay port players connect to |
| `--http-port` | `TZAR2_HTTP_PORT` | 8080 | HTTP API, 0 = off |
| `--http-host` | `TZAR2_HTTP_HOST` | `+` | `localhost` keeps the API on loopback |
| `--desync-dir` | `TZAR2_DESYNC_DIR` | `./desync` (`/data/desync` in the image) | `.t2desync` reports |
| `--max-desync` | | 200 | reports kept |
| `--max-lobbies` | `TZAR2_MAX_LOBBIES` | 256 | |
| `--max-peers` | `TZAR2_MAX_PEERS` | 1024 | |

## HTTP API (read-only)

* `GET /lobbies`: public lobbies as JSON (`code`, `name`, `players`, `maxPlayers`, `phase`, `version`).
* `GET /healthz`: `ok`.
* `GET /metrics`: Prometheus text format (`tzar2_lobbies`, `tzar2_matches`, `tzar2_peers`, `tzar2_desyncs_total`,
  `tzar2_rejoins_total`, `tzar2_takeovers_total`, `tzar2_rate_limited_total`, `tzar2_late_batches_total`, ...).

Lobbies are created and joined over UDP only. The game's browser uses the same UDP port (one unconnected datagram).

## Logs

One JSON object per line on stdout: `{"ts":"...","event":"match.start","lobby":"ABC234","slot":1,"detail":"..."}`.
Events: `relay.start`, `lobby.create`, `member.join`, `member.rejoin`, `member.drop`, `member.kick`, `hello.refused`,
`match.load`, `match.barrier`, `match.start`, `match.desync`, `desync.report`, `match.resume`, `match.takeover`,
`match.pause`, `match.save`, `lobby.end`, `service.ready`, `service.stop`. Player names appear only in `member.join`.
IP addresses appear only in `hello.refused`.

## Operating notes

* SIGTERM ends every match with `Shutdown` and exits within a second.
* Only UDP 47625 must be reachable from players. Keep the HTTP port private or behind a proxy if you do not want to
  publish the lobby list and metrics.
* Who runs the public servers is open (Q-09). The default is community self-hosting.
