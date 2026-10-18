# 0161. Host the relay in a headless service with a read-only HTTP API, JSON-line logs and a Linux container

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M6)
* **Related:** TDD §19.7, §7.2 (`Tzar2.Server`: Core, Net), REQ-NET-04, G-04, G-05, Q-09, ADR 0011, ADR 0160

## Context

§19.7 asks for a ".NET headless service (Linux container): relay with LiteNetLib, a minimal HTTP lobby API, Prometheus
metrics, structured logs, per-match isolation. The server address is configurable so the community can self-host."
The relay itself is `Tzar2.Net.Relay.RelayServer` (ADR 0160). `Tzar2.Server` may use only Core and Net, and no packages
of its own (ADR 0011).

## Decision

* `Tzar2.Server` (net10.0, AOT-compatible) is a thin host: options from arguments or `TZAR2_*` environment variables
  (UDP port 47625, HTTP port 8080, desync directory, lobby and peer caps), the relay on its own thread
  (`RelayServer.Run`, 1 ms timer resolution on Windows), SIGTERM/Ctrl+C end every match with `Shutdown`.
* **HTTP API with `HttpListener`** (part of the base runtime, no ASP.NET Core): `GET /lobbies` (JSON), `GET /healthz`,
  `GET /metrics` (Prometheus text format from `RelayMetrics`). Read-only: lobbies are created and joined over the
  UDP protocol only, so the HTTP side has no state to protect and no request bodies are read.
* **Logs**: every relay event as one JSON object per line on stdout (`ts`, `event`, `lobby`, `slot`, `detail`), written
  with `Utf8JsonWriter` (trim-safe, no reflection). The relay logs player names only on join and addresses only on
  refused hellos (G-05 data minimisation).
* **Desync reports** (REQ-NET-04): the relay hands bundles to `DesyncDirectory`, which writes them on its own thread as
  `desync-<code>-<match id>-t<tick>.t2desync` (only relay-generated fields in the name) and keeps the newest 200.
* **Per-match isolation** is logical: each lobby has its own slots, logs, limits and state in one process; a malformed
  or flooding member is disconnected without affecting other lobbies. Process-per-match was not needed at this scale.
* **Container**: `src/Tzar2.Server/Dockerfile` (multi-stage, `sdk:10.0` → `runtime:10.0`, non-root `app` user, volume
  `/data`), a deny-all `.dockerignore` that admits only the four projects the service needs (so nothing local such as
  `Origin/` can enter an image), and `compose.yaml` for self-hosting. CI builds the image, runs it, probes the HTTP API,
  creates a lobby and browses over UDP against it, and checks the clean stop.

## Alternatives considered

* **ASP.NET Core minimal API:** a framework reference and a larger image for three GET endpoints.
* **Lobby creation over HTTP:** the creator needs the UDP connection anyway (slot ownership is the connection plus
  its session token), so a second path would only add attack surface.
* **One process per match:** stronger isolation, but the relay does little work per match (no simulation) and the
  per-member limits already contain misbehaving peers.

## Consequences

* Self-hosting is `docker compose up` or one `dotnet run`. Only UDP 47625 must be public.
* Who operates public servers stays open (Q-09); nothing here assumes a central service.
* Not done: TLS for the HTTP API (put it behind a reverse proxy), authentication of operators, persistent lobby state
  across restarts (a restart ends running matches; players can resume them from an MP save, ADR 0166).
