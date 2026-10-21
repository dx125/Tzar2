using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.AI.Executors;
using Tzar2.AI.Legacy.Program;
using Tzar2.AI.Runtime;
using Tzar2.AI.Scenarios;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;
using Tzar2.Sim.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.AI.Tests;

/// <summary>
/// The AI and the technologies of GAME-06 (ADR 0050/0052): research items in queues, the real research state, and the
/// research executor for <c>have te_*</c> goals and for tech-gated units.
/// </summary>
public class AiResearchTests(ITestOutputHelper output)
{
    private const int Tile = MapState.UnitsPerTile;

    // Player 0 (human): castle, blacksmith, wooden wall; player 1 (AI): castle, barracks, blacksmith, peasants.
    private static MapData Map()
    {
        var objects = new List<MapObject>
        {
            MapObject.AtTile("euro.castle", 0, 2, 2),
            MapObject.AtTile("euro.blacksmith", 0, 10, 2),
            MapObject.AtTile("comm.wall_wood", 0, 16, 2),
            MapObject.AtTile("euro.castle", 1, 40, 30),
            MapObject.AtTile("euro.barrack", 1, 48, 30),
            MapObject.AtTile("euro.blacksmith", 1, 40, 38),
        };
        for (int i = 0; i < 6; i++)
        {
            objects.Add(new MapObject("euro.peon", 1, ((47 + i) * Tile) + (Tile / 2), (37 * Tile) + (Tile / 2)));
        }

        return ScenarioMaps.WithObjects(ScenarioMaps.Build("research", 64, 48, new byte[64 * 48], []), objects);
    }

    private static Simulation Sim(AiProgram program, out LegacyAiSystem ai, int startingResources = 2)
    {
        var sim = new Simulation(AiFixtures.Classic, Map(), new SimulationConfig { PlayerCount = 2, MatchSeed = 3, StartingResources = startingResources, PopulationLimit = 200 });
        sim.World.SetCivilization(0, "euro");
        sim.World.SetCivilization(1, "euro");
        ai = LegacyAiSystem.Attach(sim, [new AiPlayerSettings { Slot = 1, Program = program, Civilization = "euro" }], new AiOptions { ThinkIntervalTicks = 5 });
        return sim;
    }

    private static EntityId Of(World world, string key, int owner)
    {
        ushort type = (ushort)world.Ruleset.EntityTypeId(key);
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            if (world.Identities.Components[i].TypeId == type && world.Identities.Components[i].Owner == owner)
            {
                return ids[i];
            }
        }

        return EntityId.None;
    }

    [Fact]
    public void Research_and_upgrades_in_queues_do_not_break_the_ai()
    {
        // A human queues research and an upgrade while an AI runs: the AI's census must skip those queue items.
        using Simulation sim = Sim(AiFixtures.Skirmish, out LegacyAiSystem ai);
        World world = sim.World;
        var commands = new CommandList();
        commands.Add(CommandKind.Research, [Of(world, "euro.blacksmith", 0)], arg0: world.Ruleset.TechnologyId("te_temperiron"), player: 0);
        commands.Add(CommandKind.Upgrade, [Of(world, "comm.wall_wood", 0)], arg0: world.Ruleset.UpgradeId("to_wall_stone"), player: 0);
        sim.Tick(commands);
        Production queue = world.Productions.Get(Of(world, "euro.blacksmith", 0));
        Assert.True(queue.Count == 1 && QueueItems.IsResearch(queue.Queue[0].Item));
        var none = new CommandList();
        for (int t = 0; t < 200; t++)
        {
            sim.Tick(none);
        }

        Assert.False(sim.IsFaulted);
        Assert.True(ai.Issued(0) > 0);
    }

    [Fact]
    public void Technology_goals_are_researched_and_seen_by_the_script()
    {
        AiProgram program = AiFixtures.Compile("rule have te_temperiron end\nrule when te_temperiron trigger Done end");
        using Simulation sim = Sim(program, out LegacyAiSystem ai);
        int tech = sim.World.Ruleset.TechnologyId("te_temperiron");
        var none = new CommandList();
        bool queued = false;
        for (int t = 0; t < 3000 && !sim.World.Players[1].HasTechnology(tech); t++)
        {
            sim.Tick(none);
            queued |= ai.LastCommands.Commands.ToArray().Any(c => c.Kind == CommandKind.Research && c.Arg0 == tech);
        }

        Assert.True(queued);
        Assert.True(sim.World.Players[1].HasTechnology(tech));
        for (int t = 0; t < 20; t++)
        {
            sim.Tick(none);
        }

        Assert.True(ai.Players[0].IsFlagSet(program.Flags.IndexOf("done")));
        Assert.DoesNotContain(ai.Players[0].Goals, g => g.Kind == AiGoalKind.Technology);
    }

    [Fact]
    public void A_tech_gated_unit_goal_researches_its_technology_then_trains()
    {
        AiProgram program = AiFixtures.Compile("rule have 2 knights end");
        using Simulation sim = Sim(program, out _, startingResources: 2);
        int platearmor = sim.World.Ruleset.TechnologyId("te_platearmor");
        int knight = sim.World.Ruleset.EntityTypeId("euro.knight");
        var none = new CommandList();
        int knights = 0;
        for (int t = 0; t < 8000 && knights < 1; t++)
        {
            sim.Tick(none);
            knights = sim.World.Identities.Components.ToArray().Count(i => i.TypeId == knight && i.Owner == 1);
        }

        output.WriteLine($"plate armor {sim.World.Players[1].HasTechnology(platearmor)}, knights {knights}, tick {sim.World.Tick.Value}");
        Assert.True(sim.World.Players[1].HasTechnology(platearmor));
        Assert.True(knights >= 1);
    }

    [Fact]
    public void Attempt_stamps_survive_a_resize_of_the_claims()
    {
        // Two worlds of very different sizes: the second Begin grows the claim array; a new run must still be new.
        using var small = new Simulation(AiFixtures.Classic, Map(), new SimulationConfig { PlayerCount = 2 });
        using var big = new AiSkirmishScenario(AiFixtures.Classic, AiFixtures.Skirmish, new AiSkirmishOptions { Players = 2, MapSize = 96 });
        var model = new AiRulesetModel(AiFixtures.Classic);
        var census = new AiCensus(model, 2);
        var context = new AiExecutorContext(census, new CommandList());
        AiBinding binding = AiBinding.Bind(AiFixtures.Skirmish, model, "euro");
        var state = new AiPlayerState(new AiPlayerSettings { Slot = 0, Program = AiFixtures.Skirmish, Civilization = "euro" }, AiFixtures.Skirmish.Flags.Length, AiFixtures.Skirmish.Rules.Length);
        for (int run = 0; run < 3; run++)
        {
            context.Begin(small.World, binding, state, AiOptions.Default);
            Assert.True(context.FirstAttempt(0, 0));
            Assert.False(context.FirstAttempt(0, 0));
        }

        Assert.True(big.Simulation.World.Entities.HighWater > small.World.Entities.HighWater);
        for (int run = 0; run < 3; run++)
        {
            context.Begin(big.Simulation.World, binding, state, AiOptions.Default);
            Assert.True(context.FirstAttempt(0, 0), $"run {run} after the resize reused a stale stamp");
        }
    }
}
