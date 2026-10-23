using System;
using Tzar2.Core.Hashing;
using Tzar2.Sim;
using Tzar2.Sim.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Determinism.Tests;

/// <summary>
/// Cross-platform determinism of the sim core (TDD §22.2, ADR 0017): the synthetic scenario — generated map,
/// 2,000 units, bot move orders and delete/respawn churn through the command codec, fork/join movement — must give
/// the same hash stream on Windows x64, Linux x64 and macOS arm64, on .NET 8 and .NET 10, in Debug (overflow policy
/// Throw) and Release (Saturate), and with any worker count. CI's <c>determinism</c> job runs this on all three OSes.
/// </summary>
public class SimGoldenHashTests(ITestOutputHelper output)
{
    // Changing the scenario, a system, the command codec, the hashed layout or a Core primitive changes these values on
    // purpose: update them in the same commit and say why. An unexpected change, or a difference between platforms,
    // is a determinism bug. History: (M2 part A) 06D4D032CD216BBC / 25418CEBC3F46249 -> (M2 hardening, ADR 0019)
    // 297C6D90FC748B9E / 38232E7ED6D36D86: occupancy counts became int instead of ushort, which changes the bytes of the
    // full-state hash only (per-tick hashes are unchanged; the stream includes the full hashes every 20 ticks) ->
    // (M2 part B, ADRs 0021-0025) 271B3EF84BB7433F / F8763BDE53F22E5B: pathfinding and movement replace the straight-line
    // mover; the ruleset, Motion and the hashed layout gained fields (decals, paths, path queue) -> (M2 part B review)
    // B4BF7619451DBAE2 / 56C6C54762811D13: passability version in the map hash, route plans carry portal tiles,
    // enemies block movers, crowd give-up rule -> (M2 part C, ADRs 0033/0034/0039) 2D1E33CB90510133 / C8B62DE0DA848E64:
    // the gameplay stores (combat, worker, resource, construction, production) join the hashed layout, PlayerState holds
    // a resource array and population (the hashed layout); and movers held by an enemy now side-step around it (owner
    // decision, ADR 0025 amendment), which changes this four-player scenario's movement. Its units have no gameplay
    // traits otherwise -> (M3 A, ADRs 0040-0045) B435A37AFB3A1448 / 5A3E1509E520C33D: the M3 stores, the bridge layer,
    // PlayerState technologies and civilization join the hashed layout; behaviour unchanged -> (PR #21 review)
    // CCD85F529731EEC1 / 618722F6DFB2231D: GateState, Production and Construction grew (paid costs), a repair store ->
    // (M3 F, ADRs 0100-0107) BC950AE8AB330411 / A9EC6EED34BA1812: kingdom state and diplomacy join the tick hash,
    // visibility (stamps, explored, last-seen) the full hash, two stores (trade, Nature homes) the layout ->
    // (M3 stream E, ADRs 0090-0096) the GAME-07…11 stores join the hashed layout; behaviour unchanged ->
    // (naval, ADRs 0140-0143) 430E1DC0BEC256CB / F0A9532388B6ED2C: the docking and explorer stores join the layout ->
    // (ADR 0180) ECB4C7BEBF9D9D91 / 793657126549046E: the production queue holds entries (an item and a count) and the
    // worker an idle timer, so the hashed layout version is 8; behaviour unchanged ->
    // (ADR 0184) 6E50902314F85F51 / E5B45D0EB91557CC: the spies' disguise store joins the layout (version 9); behaviour
    // unchanged.
    // (ADR 0250, 2026-10-22) the curse, hypnosis, the banshee and the Messiah as ReTzar reads them: a synthetic Messiah shifts type ids, status effects grow (hashed layout 13): the hashes only.
    // (ADR 0255, 2026-10-22) souls are bound to a unit (item state grows, hashed layout 14), the Messiah takes no magic damage: the hashes.
    // (ADR 0256, 2026-10-22) cows grow meat (a new store, hashed layout 15) that falls as food items peasants carry home: the hashes.
    private const ulong GoldenFinalFullHash = 0x3C42244724F6A602UL;
    // (ORD-005, 2026-10-22) the waypoint store joins the hashed layout (version 10): the hashes only.
    // (ITEM-016, 2026-10-22) items keep their own spell, effect and look, craft jobs their variant (layout 11): the hashes only.
    // (ADR 0189, 2026-10-22) queued orders gain a kind and a target, explorers an area (hashed layout 12): the hashes only.
    // (ADR 0190, 2026-10-22) rules from ReTzar's reading of the original: gathered crusaders in the kingdom state, loads raised by the gathering technologies, ship fire rates: the ruleset and the hashes.
    // (ADR 0220, 2026-10-22) a unit meeting a blocked corner almost head-on slides past it at its full step; mutual
    // allies shove and queue like friends: movement changes.
    private const ulong GoldenHashStream = 0xFAB73D59DB408803UL;

    private const int Ticks = 300;

    private static readonly SyntheticScenarioOptions Options = new()
    {
        MapSize = 128,
        EntityCount = 2000,
        Players = 4,
        Seed = 0x5EED_2026UL,
        OrdersPerPlayerPerTick = 12,
        ChurnPerPlayerPerTick = 2,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Sim_scenario_produces_the_golden_hashes(int workers)
    {
        (ulong stream, ulong final) = Run(workers);
        output.WriteLine($"workers={workers} runtime={Environment.Version} arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} stream={stream:X16} final={final:X16}");
        Assert.Equal(GoldenHashStream, stream);
        Assert.Equal(GoldenFinalFullHash, final);
    }

    private static (ulong Stream, ulong Final) Run(int workers)
    {
        using var scenario = new SyntheticScenario(Options, workers);
        var stream = new StateHasher();
        for (int t = 0; t < Ticks; t++)
        {
            TickResult result = scenario.RunTick();
            stream.Add(result.StateHash).Add(result.FullHash ?? 0UL);
        }

        return (stream.GetCurrentHash(), scenario.Simulation.ComputeFullHash());
    }
}
