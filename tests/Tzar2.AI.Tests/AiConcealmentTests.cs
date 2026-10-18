using System;
using System.Collections.Generic;
using Tzar2.AI.Executors;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;
using Tzar2.Sim.Scenarios;
using Xunit;

namespace Tzar2.AI.Tests;

/// <summary>
/// The AI and stream E's combat state (ADRs 0093, 0095): it never aims at units inside holders or at invisible units
/// its kingdom does not detect.
/// </summary>
public class AiConcealmentTests
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

    [Fact]
    public void The_ai_does_not_target_held_or_undetected_invisible_units()
    {
        // Player 0: castle, a mage, two footmen next to each other; player 1 (the AI) far away.
        var objects = new List<MapObject>
        {
            MapObject.AtTile("euro.castle", 0, 2, 2),
            Unit("euro.mage", 0, 2, 16),
            Unit("euro.footman", 0, 14, 10),
            Unit("euro.footman", 0, 15, 10),
            MapObject.AtTile("euro.castle", 1, 50, 36),
        };
        MapData map = ScenarioMaps.WithObjects(ScenarioMaps.Build("concealment", 64, 48, new byte[64 * 48], []), objects);
        using var sim = new Simulation(AiFixtures.Classic, map, new SimulationConfig { PlayerCount = 2, MatchSeed = 5, PopulationLimit = 200 });
        sim.World.SetCivilization(0, "euro");
        sim.World.SetCivilization(1, "euro");
        World world = sim.World;
        EntityId mage = Of(world, "euro.mage", 0);
        EntityId cloaked = Of(world, "euro.footman", 0, 0);
        EntityId boarder = Of(world, "euro.footman", 0, 1);
        EntityId castle = Of(world, "euro.castle", 0);
        Position at = world.Positions.Get(cloaked);

        var model = new AiRulesetModel(AiFixtures.Classic);
        var census = new AiCensus(model, 2);
        var context = new AiExecutorContext(census, new CommandList());
        AiBinding binding = AiBinding.Bind(AiFixtures.Skirmish, model, "euro");
        var state = new AiPlayerState(new AiPlayerSettings { Slot = 1, Program = AiFixtures.Skirmish, Civilization = "euro" }, AiFixtures.Skirmish.Flags.Length, AiFixtures.Skirmish.Rules.Length);
        EntityId Nearest()
        {
            census.Update(world);
            context.Begin(world, binding, state, AiOptions.Default);
            Assert.True(MilitaryExecutor.NearestEnemyTarget(context, at.X, at.Y, preferBuildings: false, out int player, out EntityId e, out _, out _));
            Assert.Equal(0, player);
            return e;
        }

        Assert.Equal(cloaked, Nearest());

        var commands = new CommandList();
        commands.Add(CommandKind.CastSpell, [mage], target: cloaked, arg0: world.Ruleset.SpellId("spell_cloak"), player: 0);
        commands.Add(CommandKind.Board, [boarder], target: castle, player: 0);
        sim.Tick(commands);
        var none = new CommandList();
        for (int t = 0; t < 2000 && !(world.IsCloaked(cloaked) && world.IsHeld(boarder)); t++)
        {
            sim.Tick(none);
        }

        Assert.True(world.IsCloaked(cloaked));
        Assert.True(world.IsHeld(boarder));
        EntityId now = Nearest();
        Assert.NotEqual(cloaked, now);
        Assert.NotEqual(boarder, now);
    }
}
