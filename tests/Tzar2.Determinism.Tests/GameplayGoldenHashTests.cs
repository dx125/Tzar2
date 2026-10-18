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
    // layout changes these values on purpose: update them in the same commit and say why. History: (M2 part C) first; (M3 A, ADRs 0040-0044) every unit, building, technology and upgrade imported; research, upgrades, capture, repair, fire, gates, bridges and the new stores; the bot researches castle technologies; then resistanceScale 100 -> 1000 (owner decision, UNK-08): only the ruleset hash changed, no hit on a building happens in these 1,600 ticks; then (review of PR #21) paid, crew-limited repair, rate-limited gates, paid refunds, and a bot that attacks and captures houses, repairs, builds a gate, drags a wall and bridges a river (the map gains a river per base); then maxGateChangesPerTick 16 -> 4 (review): ruleset hash only; then (M3 F, ADRs 0100-0107) the world systems: visibility, diplomacy (kingdoms 0 and 1 ally, 2 turns neutral to 3), tributes, two markets and a caravan per kingdom (0 and 1 trade under hmnotech), a magic gateway, bears and wild cows in the middle, Standard victory evaluation; new synthetic rows (trader, gateway, bear, orc, dwarf, ghost, skeleton); then (M3 stream E, ADRs 0090-0096) combat, experience, holders, items and magic: projectiles, splash, the new stores, spells, stances; the map gains a mage, a priest and a catapult per base and items at the center; the bot researches Learning first, garrisons and unloads soldiers, casts, bombards the center and picks items up; then (M3 F tavern, ADR 0108; Taverns option) a tavern for kingdoms 0 and 1, who hire a mercenary group and a hero, and the tavern settings in the ruleset; then (M4-A, ADR 0121) the inert entity comm.ground_object: entity type ids after it shift, which every hash sees.
    private const ulong GoldenRulesetHash = 0x4B4E708303593D9CUL;
    private const ulong GoldenHashStream = 0x0695453CE18F72D8UL;
    private const ulong GoldenFinalFullHash = 0x1D30A33EA10034D2UL;

    private const int Ticks = 1600;

    private static readonly GameplayScenarioOptions Options = new()
    {
        MapSize = 128,
        Players = 4,
        WorkersPerPlayer = 20,
        SoldiersPerPlayer = 120,
        TreesPerPlayer = 120,
        Seed = 0x5EED_6A3EUL,
        Taverns = true,
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
        Assert.True(scenario.Delivered > 0 && scenario.Kills > 0 && scenario.Trained > 0 && scenario.Built > 0 && scenario.Researched > 0, scenario.Describe());
        Assert.True(scenario.M3Events is { Captured: > 0, Fires: > 0, Repaired: > 0 } && scenario.Simulation.World.Gates.Count > 0, scenario.Describe());
        Assert.True(scenario.WorldEvents is { Claimed: > 0, Sales: > 0, Tributes: > 0, Diplomacy: > 0 } && scenario.Hired > 0, scenario.Describe());
        Assert.True(scenario.CombatMagicEvents is { Spells: > 0, Projectiles: > 0, Boarded: > 0, Picked: > 0, Levels: > 0 }, scenario.Describe());
        Assert.Equal(GoldenRulesetHash, ruleset.ContentHash);
        Assert.Equal(GoldenHashStream, stream.GetCurrentHash());
        Assert.Equal(GoldenFinalFullHash, final);
    }
}
