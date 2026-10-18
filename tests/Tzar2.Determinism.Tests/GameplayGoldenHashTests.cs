using System;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Core.Hashing;
using Tzar2.Sim;
using Tzar2.Sim.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Determinism.Tests;

/// <summary>
/// Cross-platform determinism of the gameplay systems (ADR 0039, TDD §22.2): the economy-and-combat scenario —
/// classic ruleset compiled from the synthetic tables, bases, harvesting, house building, training, and a battle of
/// four kingdoms — must give the same hash stream on Windows x64, Linux x64 and macOS arm64, on .NET 8 and .NET 10, in
/// Debug and Release and with any worker count. The ruleset hash is pinned too: the compiler is deterministic (ADR 0012).
/// </summary>
public class GameplayGoldenHashTests(ITestOutputHelper output)
{
    // Changing the rules compiler, the classic YAML, the synthetic tables, a gameplay system, the scenario or the hashed
    // layout changes these values on purpose: update them in the same commit and say why. History: (M2 part C) first; (M3 A) every unit, building, technology and upgrade imported.
    private const ulong GoldenRulesetHash = 0x84BC373609E0B32DUL;
    private const ulong GoldenHashStream = 0x66969E55A3C7D43BUL;
    private const ulong GoldenFinalFullHash = 0xA84210BA0BA770A1UL;

    private const int Ticks = 1600;

    private static readonly GameplayScenarioOptions Options = new()
    {
        MapSize = 128,
        Players = 4,
        WorkersPerPlayer = 20,
        SoldiersPerPlayer = 120,
        TreesPerPlayer = 120,
        Seed = 0x5EED_6A3EUL,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Gameplay_scenario_produces_the_golden_hashes(int workers)
    {
        Ruleset ruleset = SyntheticLegacyContent.CompileRuleset();
        using var scenario = new GameplayScenario(ruleset, Options, workers);
        var stream = new StateHasher();
        for (int t = 0; t < Ticks; t++)
        {
            TickResult result = scenario.RunTick();
            stream.Add(result.StateHash).Add(result.FullHash ?? 0UL);
        }

        ulong final = scenario.Simulation.ComputeFullHash();
        output.WriteLine($"workers={workers} runtime={Environment.Version} ruleset={ruleset.ContentHash:X16} stream={stream.GetCurrentHash():X16} final={final:X16} {scenario.Describe()}");
        Assert.True(scenario.Delivered > 0 && scenario.Kills > 0 && scenario.Trained > 0 && scenario.Built > 0, scenario.Describe());
        Assert.Equal(GoldenRulesetHash, ruleset.ContentHash);
        Assert.Equal(GoldenHashStream, stream.GetCurrentHash());
        Assert.Equal(GoldenFinalFullHash, final);
    }
}
