# 0234. Encrypt the relay protocol with a Noise NK or XX handshake carried by LiteNetLib's connect request

* **Status:** Proposed
* **Date:** 2026-10-22
* **Deciders:** maintainer agent (follow-up of ADR 0230)
* **Related:** REQ-NET-06, REQ-NET-11, TDD §19.6, G-05, ADR 0011, ADR 0160, ADR 0230

## Context

The relay protocol (ADR 0160) is plain UDP through LiteNetLib. ADR 0230 made session tokens single-use. An observer
who sees the relay's welcome still reads the new token, and an active man in the middle can read and change anything:
chat, lobby choices, command batches. That is acceptable on a LAN and among friends who share a match code, but not
for a public internet relay (M7).

The constraints:

* LiteNetLib does reliability, ordering and fragmentation; it has no encryption. Its connect request carries an
  application payload (our hello), and every later message is a datagram we write.
* The relay is reached by address and port. A LAN host has no certificate; a public relay can publish a key.
* .NET 8, the runtime Godot loads, has no X25519 in its base library, and its ChaCha20-Poly1305 depends on the OS
  crypto provider (`ChaCha20Poly1305.IsSupported` is not true everywhere). ADR 0011 keeps third-party dependencies few
  and licence-checked.
* The lockstep must stay cheap: about 80 small datagrams a second per client at 4x speed.

## Decision

* **Noise Protocol Framework** (`Noise_NK_25519_ChaChaPoly_BLAKE2s` for a relay with a known static key,
  `Noise_XX_25519_ChaChaPoly_BLAKE2s` for a LAN host or a first contact), as protocol version 2 of the relay protocol.
  * **NK:** the client knows the relay's public key. The internet relay prints it at start, `GET /healthz` serves it,
    and the server address in the multiplayer settings can carry it (`host:port#key`). The first message rides in the
    connect request (LiteNetLib's request data): `e, es` plus the encrypted hello. The relay answers in the first
    reliable message, before the welcome, with `e, ee`. From then on both sides hold two cipher states.
  * **XX** when no key is known (LAN): one more round trip, and the client pins the relay's key for this address
    (trust on first use; the lobby screen shows a fingerprint). A changed key is refused with a clear message.
* **Every message after the handshake is sealed:** ChaCha20-Poly1305 with the session keys. The nonce is a 64-bit
  counter per direction, sent in the clear in each datagram (unreliable datagrams arrive out of order). A sliding
  window of 1,024 nonces rejects replays. The relay keeps no state for a member that has not finished the handshake
  beyond LiteNetLib's pending request, so a spoofed connect request costs one X25519 operation (rate-limited per
  address as now).
* **Tokens:** the session token and its rotation (ADR 0230) stay. A rejoin is a new handshake whose sealed hello carries
  the token, so the token never travels in clear. The lost-welcome lockout of ADR 0230 remains, and stays rare.
* **Crypto library:** `NSec.Cryptography` (libsodium, MIT/ISC, prebuilt for Windows, Linux and macOS x64/arm64)
  behind an internal `INoiseCrypto` seam in `Tzar2.Net`, so the relay and the client share one implementation and
  tests can run against a pure managed fallback. The handshake state machine (about 300 lines) is written to the
  Noise specification revision 34 and tested against its published test vectors. A Noise library is not taken: the
  candidates are unmaintained or GPL.
* **Rollout:** the relay accepts protocol 1 and 2 for one release. Clients speak 2 and fall back to 1 only for a LAN
  host on the same subnet, with a warning. The public relay then turns protocol 1 off.

## Alternatives considered

* **DTLS 1.3:** standard, but .NET has no DTLS. A native stack (OpenSSL, mbedTLS) under LiteNetLib would replace its
  socket layer, and certificates do not fit LAN hosts.
* **TLS over a TCP side channel for the handshake only:** a second port and connection to pass through NAT and
  firewalls, and the datagrams still need their own sealing.
* **WireGuard-style VPN or a QUIC library:** too heavy to embed in a game client. QUIC would replace LiteNetLib and
  the redundant-upstream lockstep design of ADR 0009.
* **HMAC on every message with a key derived from the token:** authenticates but does not encrypt, and the token
  still travels in the welcome; it only moves the problem.

## Consequences

* Cost per datagram: 16 bytes of tag plus 8 of nonce, and one ChaCha20-Poly1305 operation (microseconds). Bandwidth
  at 4x grows by about 2 KB/s per client.
* `RelayTests`, the soak and the fuzz targets run over the sealed protocol. The wire fuzzers also cover the handshake
  messages.
* Implementation is optional for 1.0 (owner decision pending). It becomes required before a public relay is
  announced (M7), together with the reverse proxy of ADR 0161.

## Questions for the product owner

* Will Tzar2 run a public relay at 1.0? If not, this can wait until one is planned; LAN and self-hosted play are
  covered by ADR 0230.
* Is a native dependency (libsodium through NSec) acceptable in the client? The alternative is a managed
  implementation, slower and to be audited.
