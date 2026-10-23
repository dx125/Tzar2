# 0230. Rotate the session token on every rejoin; encrypt the handshake with Noise later

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** maintainer agent (work queue item 12)
* **Related:** REQ-NET-06, REQ-NET-11, TDD §19.6, G-05, ADR 0160 (known limitation, review M3), ADR 0162, ADR 0168

## Context

ADR 0160 left a known limitation: the protocol is not encrypted, and the 64-bit session token travels in plain text in
the rejoin hello (and in every welcome). Someone on the network path who captures it can take the player's slot; the
real player's connection is then dropped as stale. Before this change a token stayed valid for the whole match, so a
token captured once (from a packet capture, a log, or the ticket the client keeps in `user://multiplayer.cfg`) kept
working until the match ended.

What an attacker can see decides what helps:

* **A passive observer of one direction or one moment** (a capture of one rejoin hello, an old capture, a leaked
  ticket file): a token that is single-use is enough.
* **A passive observer of both directions over time**: they also read the new token from the welcome. Only
  confidentiality helps: a key exchange so that the token is never on the wire in clear.
* **An active man in the middle**: also needs the relay to be authenticated (a known relay key).

## Decision

* **Now: the relay rotates the token on every successful rejoin.** The token in the rejoin hello is spent; the relay
  draws a new one from `RandomNumberGenerator` and sends it in the welcome; `NetworkMatch.Ticket` reads the new one. A
  replayed hello with a spent token is refused with `BadToken` and leaves the connected player alone. Tokens still
  compare in constant time. No wire format change (protocol version 1 stays).
* The client stores the new ticket as soon as the welcome arrives (`GameApp._Process`), not only once the match is
  loaded again, so a crash while reloading does not lock the player out.
* **Later (backlog, before a public internet relay): a Noise handshake over the connect request.** `Noise_NK` (the
  client knows the relay's static public key: shown with the relay address, or pinned on first use) or `Noise_XX` for
  LAN hosts without a known key, X25519 + ChaCha20-Poly1305 + BLAKE2s, carried in LiteNetLib's connect request data and
  the first reliable message; then every message is sealed with the session keys, with the LiteNetLib sequence as the
  nonce. The token then never travels in clear, and rotation stays as a second line of defence. DTLS is not chosen: it
  needs a datagram TLS stack that .NET does not ship on every platform and would sit under LiteNetLib's own
  reliability layer.

## Alternatives considered

* **Leave the token valid for the match** (ADR 0160): a single capture is enough for the whole match.
* **Challenge-response with an HMAC of the token** (relay nonce or a rising counter in the hello): the token is still
  sent in clear in the welcome, so it protects no more than rotation against a passive observer, at the cost of a
  protocol version and a round trip or a counter in the ticket.
* **Noise or DTLS now:** the right fix for internet play, but a protocol version, a crypto dependency (no X25519 in
  .NET 8's base library on every platform; ADR 0011 keeps dependencies few) and a key distribution story for LAN hosts.
  No public relay exists yet (Q-09), so it can wait for M7/M8.

## Consequences

* A captured token works at most until the player's next rejoin (it works once), and a leaked ticket file of a match
  in which the player has rejoined since is useless. An observer who sees the welcome still gets the new token: the
  relay must still not be relied on beyond fair play among people who share the match code (ADR 0160).
* A rejoin whose welcome is lost together with the client (crash right after the hello) needs the new token, which the
  client never kept: that player cannot return to the match. The window is the few milliseconds between the hello and
  the welcome; the computer keeps the kingdom (ADR 0163).
* Follow-up: the Noise handshake above, when an internet relay is deployed (M7), with a protocol version 2.
