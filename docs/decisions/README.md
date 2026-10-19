# Architecture decision records

Every decision that shapes the code, with its context, the alternatives and the consequences. Start a new one from
[0000-template.md](0000-template.md) with the next free number, then regenerate this index with
`pwsh tools/docs/adr-index.ps1`. A decision that changes an earlier one supersedes it (status line of both).

"Accepted (with notes)" means the status line qualifies the acceptance (a provisional part, a follow-up); read it.

| ADR | Decision | Status | Date |
|---|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted | 2026-10-17 |
| [0002](0002-target-frameworks-and-solution-layout.md) | Target net8.0 for Godot-loaded assemblies, net10.0 for headless apps, pin SDK 10.0.402 | Accepted | 2026-10-17 |
| [0003](0003-test-framework.md) | Use xUnit v2 with FsCheck, a direct Roslyn harness for analyzer tests, and BenchmarkDotNet | Accepted | 2026-10-17 |
| [0004](0004-determinism-analyzers.md) | Enforce determinism with an opt-in Roslyn analyzer (rules T2D001-T2D006) | Accepted | 2026-10-17 (amended 2026-10-17 by the M0 hardening review: pattern/local coverage, `Keys`/`Values`, hashing, fewer I/O false positives) |
| [0005](0005-core-numeric-and-id-conventions.md) | Core numeric, angle, RNG and ID conventions | Accepted | 2026-10-17 |
| [0006](0006-t2-verify-original.md) | Semantics of `t2 verify-original` | Accepted | 2026-10-17 |
| [0007](0007-spike-s2-determinism.md) | Keep the sim integer-only and let Lua cross the boundary as validated integers (spike S2) | Accepted | 2026-10-17 |
| [0008](0008-spike-s4-lua-sandbox.md) | Use LuaCSharp 0.5.7 with a count-hook instruction budget and a whitelist sandbox (spike S4) | Accepted | 2026-10-17 |
| [0009](0009-spike-s3-lockstep-relay.md) | Lockstep through a deadline-closing relay with redundant unreliable delivery over LiteNetLib | Accepted (with notes) | 2026-10-17 |
| [0010](0010-spike-s1-instanced-renderer.md) | Draw world sprites as canvas MultiMeshes in CPU radix-sorted painter's order from a Texture2DArray | Accepted (with notes) | 2026-10-17 |
| [0011](0011-allowed-packages-per-assembly.md) | Allow NuGet packages per assembly; System.IO.Hashing in Core | Accepted | 2026-10-17 |
| [0012](0012-content-is-deterministic.md) | Mark Tzar2.Content as deterministic | Accepted | 2026-10-17 |
| [0013](0013-spike-s6-sprite-memory.md) | Store sprites as trimmed, lossless indexed8 / RGB5A1 / R8 cells and pack them at load time into 2,048² texture arrays | Accepted | 2026-10-17 |
| [0014](0014-fixed-overflow-policy.md) | Throw on Fixed overflow in debug builds, saturate and log in release builds | Accepted | 2026-10-17 |
| [0015](0015-in-house-ecs.md) | Use an in-house sparse-set ECS with index-sorted dense arrays | Accepted | 2026-10-17 |
| [0016](0016-commands-hashes-snapshots-replays.md) | Commands, state hashes, snapshots and replays as hand-written binary formats | Accepted | 2026-10-17 |
| [0017](0017-deterministic-worker-pool-and-determinism-ci.md) | Fork/join with data-defined jobs and fixed-order merges; golden hashes in CI on three OSes | Accepted | 2026-10-17 |
| [0018](0018-sim-map-model-and-compiled-contract.md) | Keep the compiled Ruleset/MapData contract small in Content/Compiled; derive map layers in Sim | Accepted | 2026-10-17 |
| [0019](0019-sim-hardening.md) | Treat snapshots and replays as untrusted input; ticks never fail half-way | Accepted | 2026-10-17 |
| [0020](0020-m1-asset-viewer.md) | Build the M1 asset viewer as a client mode on the production sprite path; play MIDI with MeltySynth and a user-supplied SoundFont | Accepted | 2026-10-17 |
| [0021](0021-navigation-layers-and-hpa-clusters.md) | Keep navigation as derived per-movement-class layers with HPA* clusters, portal trees and components | Accepted | 2026-10-17 |
| [0022](0022-path-requests-routes-and-budget.md) | Plan paths as pure, budgeted, parallel requests; follow long paths as abstract routes | Accepted | 2026-10-17 |
| [0023](0023-flow-fields-for-group-moves.md) | Share hierarchical flow fields among groups of six or more, at most 48 at a time | Accepted | 2026-10-17 |
| [0024](0024-arrival-slots-and-group-speed.md) | Give every group member its own arrival slot, keep compact formations, and move groups at their slowest speed | Accepted | 2026-10-17 |
| [0025](0025-local-avoidance-collision-and-corpses.md) | Steer with integer keep-right avoidance, resolve overlaps by a Jacobi push, and keep corpses out of passability | Accepted | 2026-10-17 |
| [0026](0026-benchmarks-b3-b4-and-path-budget.md) | Define B3/B4 as runnable scenarios with functional and tick-time gates; default the path budget to 750,000 | Accepted | 2026-10-17 |
| [0030](0030-capture-harness.md) | Measure the original with a black-box capture harness in `t2 fidelity`, driven by YAML scenarios, run by the operator in one command | Accepted (with notes) | 2026-10-17 |
| [0031](0031-rules-yaml-layers-templates-and-patches.md) | Write rules as layered YAML entries with templates, trait patches and list operations | Accepted | 2026-10-17 |
| [0032](0032-classic-ruleset-from-the-imported-tables.md) | Build `classic` from the user's imported tables; mark everything else UNVERIFIED | Accepted | 2026-10-17 |
| [0033](0033-compiled-gameplay-traits.md) | Extend the compiled contract with immutable gameplay traits, resources, settings, bonuses and map objects | Accepted | 2026-10-17 |
| [0034](0034-player-stockpile-and-population.md) | Keep a fixed-size stockpile per player and recount population every tick | Accepted | 2026-10-17 |
| [0035](0035-gathering-and-drop-off.md) | Gather unit by unit into a load and deliver it to the nearest own completed drop-off | Accepted | 2026-10-17 |
| [0036](0036-placement-construction-and-cancellation.md) | Place construction sites that block at once, grow with builders, and refund when cancelled | Accepted | 2026-10-17 |
| [0037](0037-production-queues-and-rally-points.md) | Train from a fixed-size queue per building, pay when queued, leave toward the rally point | Accepted | 2026-10-17 |
| [0038](0038-combat-acquisition-swings-and-damage.md) | Fight with staggered nearest-target acquisition, a hit-frame swing cycle and a placeholder damage formula | Accepted | 2026-10-17 |
| [0039](0039-gameplay-commands-scenario-and-golden-hashes.md) | Validate gameplay commands at execution; gate economy and combat with a benchmark and golden hashes | Accepted | 2026-10-17 |
| [0040](0040-technologies-upgrades-tech-trees-and-modifiers.md) | Import technologies, upgrades and per-civ trees from the tables; apply effects as per-player modifiers | Accepted | 2026-10-17 |
| [0041](0041-resources-hud-and-mana-pools.md) | Keep iron out of the HUD and give casters a mana pool that regenerates per minute | Accepted | 2026-10-17 |
| [0042](0042-cows-fishing-fields-and-auto-gathering.md) | Gather from footprint-less nodes and animals; boats deliver to harbors; neglected fields decay | Accepted | 2026-10-17 |
| [0043](0043-repair-fire-ruins-walls-gates-and-bridges.md) | Repair, burn and ruin buildings; drag walls; turn and open gates; lay bridge decks over water | Accepted | 2026-10-17 |
| [0044](0044-capture-resistance-and-influence.md) | Capture by digging against resistance plus EMIT influence; poach animals at once; protect the castle | Accepted | 2026-10-17 |
| [0045](0045-m3-commands-rally-modes-snapshots-and-goldens.md) | Append the M3 commands and stores; rally with default action or move-advance; re-pin the goldens | Accepted | 2026-10-17 |
| [0050](0050-legacy-ai-dsl-toolchain-and-interpreter.md) | Parse the legacy AI DSL into a flat rule program and run it in a budgeted, integer-only VM | Accepted | 2026-10-17 |
| [0051](0051-ai-in-the-lockstep.md) | Run the AI as a sim system that issues regular commands and keeps its state in a snapshot extension | Accepted | 2026-10-17 |
| [0052](0052-ai-executors-and-default-script.md) | Turn script goals into commands with five executors; ship an engine-default AI script | Accepted | 2026-10-17 |
| [0060](0060-random-map-generator.md) | Generate random maps in Tzar2.Content from the mapgen stream, on the terrain-set corner model | Accepted | 2026-10-17 |
| [0070](0070-game-session-and-frames.md) | Run single-player matches in a Game-layer session with a sim thread and session-built game frames | Accepted (with notes) | 2026-10-17 |
| [0071](0071-client-game-mode-rendering.md) | Draw the skirmish with the viewer's renderer pieces, a presentation catalog from the imported tables, and isolated placeholders | Accepted (with notes) | 2026-10-17 |
| [0072](0072-input-commands-and-hud.md) | Turn input into commands with engine-free selection, order and hotkey models; build the HUD from LAYOUT.INI and the S_VIEW art | Accepted | 2026-10-17 |
| [0073](0073-legacy-ai-in-skirmish.md) | Run the skirmish's computer kingdoms with the legacy AI inside the sim pipeline | Accepted | 2026-10-18 |
| [0080](0080-sim-tick-performance-and-benchmark-gates.md) | Gate the nightly on an S2 proxy with per-CPU baselines; keep the order-flood scenario as informational stress | Accepted | 2026-10-18 |
| [0090](0090-combat-and-magic-content-model.md) | Model combat, experience, holders, items and magic as traits, a `combat:` section and spell entries | Accepted | 2026-10-18 |
| [0091](0091-projectiles-splash-stances-and-damage-rolls.md) | Fly projectiles, splash at the impact, keep simultaneous hits; minimum range, damage rolls and stances | Accepted | 2026-10-18 |
| [0092](0092-experience-heroes-and-persistent-units.md) | Count experience from damage to enemies, level with a growing cost, name heroes, persist named units | Accepted | 2026-10-18 |
| [0093](0093-holders-boarding-healing-and-damage-shares.md) | Holders take units off the map; board on arrival, unload to free tiles, heal inside, share damage | Accepted | 2026-10-18 |
| [0094](0094-items-inventories-and-crafting.md) | Items are entities that move into inventories atomically; their effects apply while carried or when used | Accepted | 2026-10-18 |
| [0095](0095-spells-religion-crusades-and-conversions.md) | Cast spells in range and pay on effect; lasting effects, summons, area spells; crusades, conversions, bribes | Accepted | 2026-10-18 |
| [0096](0096-combat-magic-commands-stores-snapshots-and-goldens.md) | Append the GAME-07…11 commands, events and stores; validate them on restore; cover them in the goldens | Accepted | 2026-10-18 |
| [0100](0100-world-systems-state-commands-and-snapshots.md) | Keep the world systems in their own state, stores, commands and rules section | Accepted | 2026-10-17 |
| [0101](0101-incremental-per-kingdom-visibility.md) | Compute visibility in the sim: per-kingdom reference-counted grids, bitsets and last-seen records | Accepted | 2026-10-17 |
| [0102](0102-diplomacy-sharing-tribute-and-gate-passage.md) | Asymmetric stances, consented sharing, bounded tribute, and gates that bar non-allies | Accepted | 2026-10-17 |
| [0103](0103-trade-routes-and-the-tavern.md) | Trade routes between two docks with distance profit; tavern conversion, gambling and loans | Accepted | 2026-10-17 |
| [0104](0104-gateways-and-nature.md) | Claim gateways and wild animals by touch; Nature's units wander and guard their home | Accepted | 2026-10-17 |
| [0105](0105-day-clock-and-weather.md) | Derive the day clock and the weather from the tick and the seed | Accepted | 2026-10-17 |
| [0106](0106-victory-conditions-and-statistics.md) | Evaluate victory in the sim with allied victory; count statistics from events | Accepted | 2026-10-17 |
| [0107](0107-cheat-codes-goldens-and-benchmarks.md) | Make the hm* codes sim commands gated by the match; extend the golden scenario to the world systems | Accepted | 2026-10-17 |
| [0108](0108-tavern-mercenaries-and-heroes.md) | Hire mercenary groups and heroes in the tavern from per-period offers derived from the seed | Accepted | 2026-10-18 |
| [0110](0110-capture-harness-on-windows-11.md) | Capture harness on Windows 11: detect the machine's tzar.ini values, warn about GOG's shim, start maps through the setup screen | Accepted | 2026-10-18 |
| [0111](0111-first-capture-fidelity-facts.md) | First capture session: commit its facts, adopt the measured shadow factor and player-colour order, flag the original's tick settings | Accepted | 2026-10-18 |
| [0112](0112-fidelity-suite-and-second-capture-session.md) | The fidelity suite: the §22.3 catalogue and the M3 gate, `Tzar2.Fidelity.Tests`, `t2 fidelity report`, and the second capture session in four batches | Accepted | 2026-10-18 |
| [0113](0113-capture-batches-e-to-i.md) | Capture batches E–I and the editor session: measuring the rest of TDD §22.3 | Accepted (with notes) | 2026-10-18 |
| [0120](0120-scenario-ir-for-triggers-conversations-and-campaigns.md) | Share one scenario IR between the legacy converter and the trigger engine | Accepted (with notes) | 2026-10-18 |
| [0121](0121-legacy-map-and-campaign-converter.md) | Convert legacy maps and campaigns with one deterministic converter into `.t2map`/`.t2camp` | Accepted | 2026-10-18 |
| [0130](0130-legacy-trigger-engine-in-the-sim.md) | Run the legacy trigger engine as a budgeted sim system with its state in a snapshot extension | Accepted | 2026-10-18 |
| [0131](0131-conversations-and-scenario-presentation-events.md) | Run conversations in the sim and send scenario presentation to the client as events | Accepted | 2026-10-18 |
| [0132](0132-scenario-presentation-in-the-game-layer-and-client.md) | Resolve scenario presentation in the game layer and show it with the original dialog art | Accepted | 2026-10-18 |
| [0133](0133-campaign-progression-carry-over-saves-and-profiles.md) | Drive campaigns with an explicit flow over the scenario IR, keep progress in a JSON profile, save missions as snapshot + progress | Accepted | 2026-10-18 |
| [0134](0134-original-campaign-harness-and-scripted-solver.md) | Measure "all original campaign missions completable" with a scripted, escalating, cheat-assisted solver | Accepted | 2026-10-18 |
| [0135](0135-scripted-fights-shared-control-orders-and-solver-goal-discovery.md) | Scripted fights, shared-control orders, and the solver's goal discovery | Accepted | 2026-10-18 |
| [0140](0140-ships-dock-at-the-shore.md) | Ships dock at the shore nearest to both parties to board, unload and be repaired | Accepted | 2026-10-18 |
| [0141](0141-boats-fish-water-nodes-only.md) | Boats gather only nodes on water, land workers only nodes on land | Accepted | 2026-10-18 |
| [0142](0142-naval-combat-across-the-shore.md) | Naval combat: ships and land units fight across the shore only within range | Accepted | 2026-10-18 |
| [0143](0143-explore-order-and-ship-regressions.md) | The Explore order, and the patch-note regressions about ships | Accepted | 2026-10-18 |
| [0144](0144-naval-ai-executor.md) | The naval AI: a harbor, ships and fishing, and landings overseas | Accepted | 2026-10-18 |
| [0145](0145-naval-stores-snapshots-and-goldens.md) | Naval stores, snapshots, goldens and benchmarks | Accepted | 2026-10-18 |
| [0146](0146-crowded-docks-and-landings.md) | Crowded docks and landings: boarders wait, docks spread, taken goals settle | Accepted | 2026-10-18 |
| [0150](0150-editor-model-in-the-allowed-assemblies.md) | Keep the editor model in `Editing` namespaces of Content, Import and Game; the client's editor mode is a view | Accepted | 2026-10-18 |
| [0151](0151-editor-documents-undo-and-byte-identical-packages.md) | Undo with immutable states plus terrain patches; save `.t2map` byte-identically through the package writers | Accepted | 2026-10-18 |
| [0152](0152-editor-map-tools-placement-and-object-extras.md) | Edit maps with headless tools and placement rules; give map objects flags, facing and amount | Accepted | 2026-10-18 |
| [0153](0153-rule-editor-playtest-and-trigger-debugger.md) | Generate the rule editor from a trigger schema; playtest scenario matches; debug triggers through a passive trace | Accepted | 2026-10-18 |
| [0154](0154-legacy-converter-in-the-editor.md) | Drive the one legacy converter from the editor through an import session with a located report | Accepted | 2026-10-18 |
| [0155](0155-campaign-editor.md) | Edit campaigns as a list of map documents; derive the graph from the maps; save `.t2camp` byte-identically | Accepted | 2026-10-18 |
| [0156](0156-editor-acceptance.md) | Prove §18.6 headlessly with the editor's own tools; keep data-dependent acceptance as local-only tests | Accepted | 2026-10-18 |
| [0160](0160-relay-lobby-and-lockstep-protocol-in-tzar2-net.md) | Put the relay, lobby and lockstep protocol in Tzar2.Net and share them between the server and the LAN host | Accepted | 2026-10-18 |
| [0161](0161-relay-server-host-http-api-and-container.md) | Host the relay in a headless service with a read-only HTTP API, JSON-line logs and a Linux container | Accepted | 2026-10-18 |
| [0162](0162-network-match-in-the-game-layer.md) | Run multiplayer matches through a NetworkMatch in Tzar2.Game that feeds GameSession from the relay's closed ticks | Accepted | 2026-10-18 |
| [0163](0163-drop-policy-in-the-sim-with-standby-ai.md) | Apply the drop policy in the sim: standby AIs for every human kingdom and a SetController command | Accepted | 2026-10-18 |
| [0164](0164-replay-format-4-and-recording-every-match.md) | Record every match into replay format 4 with the full settings, a setup description and chat | Accepted | 2026-10-18 |
| [0165](0165-desync-reports-and-component-diff.md) | Collect desync reports with keyframes before and after, and diff snapshots component by component | Accepted | 2026-10-18 |
| [0166](0166-multiplayer-saves-with-consent.md) | Save multiplayer matches after a closed tick once every player consented, and resume them from the lobby | Accepted | 2026-10-18 |
| [0167](0167-multiplayer-soak-harness.md) | Soak multiplayer with headless bot matches through the real relay over impaired loopback UDP | Accepted | 2026-10-18 |
| [0168](0168-multiplayer-ui-and-replay-viewer.md) | Multiplayer menu, lobby, in-game overlay and replay viewer in the client, on Game-layer models only | Accepted | 2026-10-18 |
| [0169](0169-legacy-time-unit-and-batch-a-facts.md) | The legacy TIME column is 1.25 s per unit; batch A's facts and the construction rate from the health bar | Accepted | 2026-10-19 |
| [0170](0170-node-carry-load-and-batch-b-facts.md) | A resource node may set the load carried from it (cows: 50); batch B's facts; unit speeds re-captured | Accepted | 2026-10-19 |
| [0171](0171-speed-four-is-40-px-per-second-and-speed-scenarios-moved.md) | SPEED 4 walks 40 px/s (10 px/s per point); speed scenarios move away from Nature's cows | Accepted (with notes) | 2026-10-19 |
