# 0166. Save multiplayer matches after a closed tick once every player consented, and resume them from the lobby

* **Status:** Accepted
* **Date:** 2026-10-18
* **Deciders:** implementation agent (M6)
* **Related:** REQ-NET-10, §19.5, REQ-SIM-13, ADR 0133 (single-player saves), ADR 0160, ADR 0162, ADR 0163

## Context

REQ-NET-10: "MP save: the host requests a save at tick T, every client writes the same snapshot, and the match can be
resumed from the lobby with the same slots (missing humans → AI)." M6 adds: with the consent of all peers, and with the
fingerprint checked. Writing "at tick T" needs every peer to snapshot between the same two ticks.

## Decision

* **Consent**: the host sends `SaveRequest(label)`; the relay sends `SaveProposal` to everyone. Every connected human
  must accept (the host counts as accepting; headless peers can accept automatically). The first refusal, or 20 s
  without an answer, cancels the save and tells everyone who refused. On consent the relay orders `Save(id)` as a
  control event into the next closed tick, so every peer writes after the same tick.
* **File** (`.t2msav`, `MultiplayerSave`): label, match id, the `MatchDescriptor` (map, settings, kingdoms with names),
  the sim tick and the snapshot. It is identified across peers by the snapshot's content hash. The file name has only
  the match id and the tick.
* **Resume**: the host opens a lobby, `PrepareResume(save)` sets the save's settings plus its snapshot hash and puts
  computers in the save's computer slots; players join; `StartResume` turns the save's player slots that are still
  open into computers (missing humans → AI). At load every peer looks for a save with that hash in its save directory;
  a peer without it, or with another one, fails the loading barrier (the save hash is a fingerprint part). The
  kingdoms keep the save's civilizations and teams whatever the lobby choices say. The relay's closure ticks restart
  at 0; the driver maps closure tick k to sim tick `save tick + k`. In closure tick 0 the returning humans get their
  kingdoms back from a takeover (ADR 0163).

## Alternatives considered

* **The host writes the save and sends it to the others:** a snapshot transfer for nothing: every peer has the same
  state already, and the hash check proves it.
* **Save at a future tick T named in the request:** needs the request to arrive everywhere before T; a closed tick is
  ordered by construction.

## Consequences

* A peer who lost its save cannot join the resumption; sending the save through the relay would fix that (same
  mechanism as map transfer, REQ-NET-08, not implemented).
* A resumed match is not recorded as a replay (ADR 0164).
