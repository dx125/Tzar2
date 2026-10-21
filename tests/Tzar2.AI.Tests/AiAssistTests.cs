using System.Collections.Generic;
using Tzar2.AI.Runtime;
using Tzar2.AI.Scenarios;
using Tzar2.Content.Compiled;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Xunit;

namespace Tzar2.AI.Tests;

/// <summary>AI assistance (ADR 0183): a human's standby AI runs only the halves of the kingdom its human hands it.</summary>
public class AiAssistTests
{
    private static readonly AiSkirmishOptions Options = new() { Players = 1, MapSize = 64, TreesPerPlayer = 40 };

    [Fact]
    [Trait("Rule", "AI-004")]
    public void A_standby_AI_waits_then_runs_the_economy_or_the_war_its_human_asks_for()
    {
        Assert.Empty(Run(assist: -1));

        HashSet<CommandKind> economy = Run(assist: 1);
        Assert.Contains(CommandKind.Harvest, economy);
        Assert.DoesNotContain(CommandKind.Attack, economy);

        HashSet<CommandKind> war = Run(assist: 2);
        Assert.DoesNotContain(CommandKind.Harvest, war);
        Assert.DoesNotContain(CommandKind.Build, war);
        Assert.DoesNotContain(CommandKind.Train, war);

        // Back to none: the AI stops.
        Assert.Empty(Run(assist: 1, thenNone: true));
    }

    // The kinds of command the human's standby AI issues in 30 s of play after its human asks for assistance (-1: never).
    private static HashSet<CommandKind> Run(int assist, bool thenNone = false)
    {
        Ruleset rules = AiFixtures.Classic;
        MapData map = AiSkirmishScenario.CreateMap(rules, Options);
        using var sim = new Simulation(rules, map, new SimulationConfig { MatchSeed = Options.Seed, PlayerCount = 1, StartingResources = Options.StartingResources, PopulationLimit = Options.PopulationLimit });
        LegacyAiSystem ai = LegacyAiSystem.Attach(sim, [new AiPlayerSettings { Slot = 0, Program = AiFixtures.Skirmish, Civilization = "euro", Difficulty = AiDifficulty.Hard, Standby = true }], Options.Ai);
        var none = new CommandList();
        var ask = new CommandList();
        if (assist >= 0)
        {
            ask.Add(CommandKind.SetAiAssist, arg0: assist);
        }

        sim.Tick(ask);
        if (thenNone)
        {
            for (int t = 0; t < 200; t++)
            {
                sim.Tick(none);
            }

            var stop = new CommandList();
            stop.Add(CommandKind.SetAiAssist, arg0: 0);
            sim.Tick(stop);
        }

        var kinds = new HashSet<CommandKind>();
        for (int t = 0; t < 600; t++)
        {
            sim.Tick(none);
            for (int c = 0; c < ai.LastCommands.Count; c++)
            {
                kinds.Add(ai.LastCommands[c].Kind);
            }
        }

        return kinds;
    }
}
