using System;
using System.Collections.Generic;
using Tzar2.AI.Executors;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Map;
using Tzar2.Sim.Scenarios;
using Xunit;

namespace Tzar2.AI.Tests;

/// <summary>The computer's items (AI-005, ADR 0269; ReTzar 0x413e00, 0x413ee0, 0x4155c0).</summary>
public class AiItemTests
{
    private const int Tile = MapState.UnitsPerTile;

    private static MapObject Unit(string key, int owner, int tx, int ty) => new(key, (byte)owner, (tx * Tile) + (Tile / 2), (ty * Tile) + (Tile / 2));

    private static EntityId Of(World world, string key, int owner, int nth = 0)
    {
        ushort type = (ushort)world.Ruleset.EntityTypeId(key);
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            if (world.Identities.Components[i].TypeId == type && world.Identities.Components[i].Owner == owner && nth-- == 0)
            {
                return ids[i];
            }
        }

        return EntityId.None;
    }

    [Theory]
    [InlineData(AiDifficulty.Normal, true, true)]
    [InlineData(AiDifficulty.Easy, true, false)]
    [InlineData(AiDifficulty.Hard, false, false)] // a scenario's match: its items are the story's
    [Trait("Rule", "AI-005")]
    public void An_idle_soldier_beyond_easy_picks_up_the_nearest_item_within_16_tiles_and_drinks_a_potion(AiDifficulty difficulty, bool takesItems, bool picks)
    {
        var objects = new List<MapObject>
        {
            MapObject.AtTile("euro.castle", 1, 2, 2),
            Unit("euro.footman", 1, 10, 20),
            Unit("comm.item_health", MapObject.Nature, 14, 20), // 4 tiles away
            Unit("comm.item_health", MapObject.Nature, 40, 20), // 30 tiles away: out of reach
            MapObject.AtTile("euro.castle", 0, 50, 36),
        };
        MapData map = ScenarioMaps.WithObjects(ScenarioMaps.Build("items", 64, 48, new byte[64 * 48], []), objects);
        using var sim = new Simulation(AiFixtures.Classic, map, new SimulationConfig { PlayerCount = 2, MatchSeed = 5, PopulationLimit = 200 });
        sim.World.SetCivilization(0, "euro");
        sim.World.SetCivilization(1, "euro");
        World world = sim.World;
        EntityId footman = Of(world, "euro.footman", 1);
        EntityId near = Of(world, "comm.item_health", MapObject.Nature, 0);
        if (world.Positions.Get(near).X > world.Positions.Get(Of(world, "comm.item_health", MapObject.Nature, 1)).X)
        {
            near = Of(world, "comm.item_health", MapObject.Nature, 1);
        }

        var model = new AiRulesetModel(AiFixtures.Classic);
        var census = new AiCensus(model, 2);
        var output = new CommandList();
        var context = new AiExecutorContext(census, output);
        AiBinding binding = AiBinding.Bind(AiFixtures.Skirmish, model, "euro");
        var state = new AiPlayerState(new AiPlayerSettings { Slot = 1, Program = AiFixtures.Skirmish, Civilization = "euro", Difficulty = difficulty, TakesItems = takesItems }, AiFixtures.Skirmish.Flags.Length, AiFixtures.Skirmish.Rules.Length);
        var executor = new ItemExecutor();
        CommandList Think()
        {
            output.Clear();
            census.Update(world);
            context.Begin(world, binding, state, AiOptions.Default);
            executor.Run(context);
            return output;
        }

        CommandList first = Think();
        if (!picks)
        {
            Assert.Equal(0, first.Count);
            return;
        }

        Assert.Equal(1, first.Count);
        Assert.Equal((CommandKind.PickItem, near), (first[0].Kind, first[0].Target));
        sim.Tick(first);
        var none = new CommandList();
        for (int t = 0; t < 600 && !world.IsHeld(near); t++)
        {
            sim.Tick(none);
        }

        Assert.True(world.IsHeld(near));
        CommandList drink = Think();
        Assert.Equal((CommandKind.UseItem, 0), (drink[0].Kind, drink[0].Arg0)); // a held potion is drunk
    }

    [Fact]
    [Trait("Rule", "AI-005")]
    public void A_computers_mage_learns_the_spell_of_a_one_charge_book_it_holds()
    {
        var objects = new List<MapObject>
        {
            MapObject.AtTile("euro.castle", 1, 2, 2),
            Unit("euro.mage", 1, 10, 20),
            Unit("comm.item_magic", MapObject.Nature, 11, 20),
            MapObject.AtTile("euro.castle", 0, 50, 36),
        };
        MapData map = ScenarioMaps.WithObjects(ScenarioMaps.Build("books", 64, 48, new byte[64 * 48], []), objects);
        using var sim = new Simulation(AiFixtures.Classic, map, new SimulationConfig { PlayerCount = 2, MatchSeed = 5, PopulationLimit = 200 });
        sim.World.SetCivilization(0, "euro");
        sim.World.SetCivilization(1, "euro");
        World world = sim.World;
        EntityId mage = Of(world, "euro.mage", 1);
        EntityId book = Of(world, "comm.item_magic", MapObject.Nature);
        EntityTraits traits = world.TypeOf(mage).Traits;
        int spell = 0;
        while (traits.CanCast((ushort)spell))
        {
            spell++;
        }

        world.Items.Get(book).Spell = (ushort)(spell + 1);
        world.Items.Get(book).Charges = 1;
        var pick = new CommandList();
        pick.Add(CommandKind.PickItem, [mage], target: book, player: 1);
        sim.Tick(pick);
        var none = new CommandList();
        for (int t = 0; t < 600 && !world.IsHeld(book); t++)
        {
            sim.Tick(none);
        }

        var model = new AiRulesetModel(AiFixtures.Classic);
        var census = new AiCensus(model, 2);
        var output = new CommandList();
        var context = new AiExecutorContext(census, output);
        AiBinding binding = AiBinding.Bind(AiFixtures.Skirmish, model, "euro");
        var state = new AiPlayerState(new AiPlayerSettings { Slot = 1, Program = AiFixtures.Skirmish, Civilization = "euro" }, AiFixtures.Skirmish.Flags.Length, AiFixtures.Skirmish.Rules.Length);
        census.Update(world);
        context.Begin(world, binding, state, AiOptions.Default);
        new ItemExecutor().Run(context);
        Assert.Equal((CommandKind.UseItem, 0, 1), (output[0].Kind, output[0].Arg0, output[0].Arg1));
        sim.Tick(output);
        Assert.True(world.HasLearned(mage, spell));
    }
}
