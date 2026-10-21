using System.Collections.Generic;
using System.Linq;
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
        var model = new AiRulesetModel(AiFixtures.Classic);
        Assert.Empty(Run(assist: -1));

        // The economy adviser gathers, builds and trains workers; no troops (the owner's description).
        List<(CommandKind Kind, int Arg0)> economy = Run(assist: 1);
        Assert.Contains(economy, c => c.Kind == CommandKind.Harvest);
        Assert.DoesNotContain(economy, c => c.Kind == CommandKind.Attack || (c.Kind == CommandKind.Train && model.IsMilitary(c.Arg0)));

        // The war adviser never gathers; what it trains are troops.
        List<(CommandKind Kind, int Arg0)> war = Run(assist: 2);
        Assert.DoesNotContain(war, c => c.Kind == CommandKind.Harvest || c.Kind == CommandKind.Research);
        Assert.All(war.Where(c => c.Kind == CommandKind.Train), c => Assert.True(model.IsMilitary(c.Arg0)));

        // Back to none: the AI stops.
        Assert.Empty(Run(assist: 1, thenNone: true));
    }

    // The commands (kind and first argument) the human's standby AI issues in 30 s of play after its human asks for assistance (-1: never).
    private static List<(CommandKind Kind, int Arg0)> Run(int assist, bool thenNone = false)
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

        var issued = new List<(CommandKind Kind, int Arg0)>();
        for (int t = 0; t < 600; t++)
        {
            sim.Tick(none);
            for (int c = 0; c < ai.LastCommands.Count; c++)
            {
                issued.Add((ai.LastCommands[c].Kind, ai.LastCommands[c].Arg0));
            }
        }

        return issued;
    }
}
