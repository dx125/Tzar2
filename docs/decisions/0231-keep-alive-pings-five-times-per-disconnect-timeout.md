# 0231. Ping five times per disconnect timeout on LiteNetLib's thread, so a busy game thread does not drop a peer

* **Status:** Accepted
* **Date:** 2026-10-22
* **Deciders:** maintainer agent (work queue item 12, issue #62)
* **Related:** REQ-NET-02, REQ-NET-06, ADR 0160, ADR 0162, ADR 0167, issue #62

## Context

`RelayTests` timed out now and then on the Windows CI runner (issue #62). The relay logs of every failure on CI
(runs 39181876792, 39382251472, 39561119226, 39733435331) show the same thing: `member.drop … Timeout` for one or all
members, after which the lobby moves on without them (a host hand-over that never comes, a `full` join that takes the
dropped host's slot, a load that never completes). Nothing was lost on the way: members were declared dead.

LiteNetLib keeps a connection with its own pings, sent by its logic thread once per `PingInterval`, which defaults to
**1 s**; a peer that hears nothing for `DisconnectTimeout` is dropped. The test relay used a 1 s timeout (the game-layer
harness too, the soak 2 s). With one ping per timeout the connection lived only on the game's own traffic (the
client's 200 ms pings, sent from `Poll` on the game thread), so whenever the test thread did not pump for about a
second (a loaded runner, a GC, JIT), the relay dropped live peers. Reproduced locally: a test that leaves `Poll` and
`Update` uncalled for 2.5 s failed 2 of 3 runs with exactly the CI signature. The same applies to the game: a client
that loads a map, or a relay with a short timeout, can drop a player whose machine is only busy.

## Decision

* The relay and the client set LiteNetLib's `PingInterval` to a fifth of their disconnect timeout, between 50 ms and
  1 s (`KeepAlive.PingIntervalMs`). Liveness then rests on LiteNetLib's threads only: a peer is dropped when its
  machine or the network is silent for the whole timeout, not when its game thread is busy.
* The test timeouts stay as they are (1 s relay, 2 s client): the fix is the keep-alive, not longer waits.
* Regression test: `RelayTests.Members_that_do_not_pump_for_longer_than_the_timeout_stay_connected` (nobody pumps for
  2.5 s; nobody is dropped, and the lobby works afterwards).

## Alternatives considered

* **Longer timeouts in the harness:** hides the cause; a stall a little longer fails again, and the game would keep
  the same weakness.
* **Serialise or slow the relay tests:** they already run in one non-parallel collection; the stall comes from the
  machine, not from other relay tests.
* **Send the game's pings from a background thread:** the client's lock and LiteNetLib's single-threaded send path
  would then be shared with the game thread for no gain over LiteNetLib's own pings.

## Consequences

* At the production relay timeout (5 s) the relay pings once a second as before; the soak relay (2 s) pings every
  400 ms, the test relay (1 s) every 200 ms. Each ping is a few bytes; the bandwidth change is negligible.
* A process that is stalled as a whole (every thread) for longer than the timeout is still dropped, which is what the
  timeout is for.
