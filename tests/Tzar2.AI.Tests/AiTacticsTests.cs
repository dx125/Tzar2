using System;
using System.Collections.Generic;
using System.Linq;
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
using Xunit.Abstractions;

namespace Tzar2.AI.Tests;

/// <summary>
/// The AI's tactics (ADR 0178; owner play-test 2026-10-20: "the AI ignores towers shooting its soldiers and enemy
/// buildings placed next to it"): threats at home are answered when the army is strong enough, waves wait for enough
/// power, gatherers avoid towers; Easy weighs nothing.
/// </summary>
public class AiTacticsTests(ITestOutputHelper output)
{
    private const int Tile = MapState.UnitsPerTile;

    private static MapObject Unit(string key, int owner, int tx, int ty) => new(key, (byte)owner, (tx * Tile) + (Tile / 2), (ty * Tile) + (Tile / 2));

    // Player 0 is passive (no AI); player 1 is the AI with a script that asks for nothing.
    private static Simulation Sim(List<MapObject> objects, AiDifficulty difficulty, out LegacyAiSystem ai, AiOptions? options = null)
    {
        MapData map = ScenarioMaps.WithObjects(ScenarioMaps.Build("tactics", 64, 48, new byte[64 * 48], []), objects);
        var sim = new Simulation(AiFixtures.Classic, map, new SimulationConfig { PlayerCount = 2, MatchSeed = 11, PopulationLimit = 200 });
        sim.World.SetCivilization(0, "euro");
        sim.World.SetCivilization(1, "euro");
        ai = LegacyAiSystem.Attach(sim, [new AiPlayerSettings { Slot = 1, Program = AiFixtures.Compile("rule trigger A end"), Civilization = "euro", Difficulty = difficulty }], options);
        return sim;
    }

    private static List<EntityId> All(World world, string key, int owner)
    {
        ushort type = (ushort)world.Ruleset.EntityTypeId(key);
        var list = new List<EntityId>();
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            if (world.Identities.Components[i].TypeId == type && world.Identities.Components[i].Owner == owner)
            {
                list.Add(ids[i]);
            }
        }

        return list;
    }

    private static void Run(Simulation sim, int ticks)
    {
        var none = new CommandList();
        for (int t = 0; t < ticks; t++)
        {
            sim.Tick(none);
        }
    }

    [Theory]
    [InlineData(AiDifficulty.Easy)]
    [InlineData(AiDifficulty.Normal)]
    [InlineData(AiDifficulty.Hard)]
    public void A_tower_built_next_to_the_town_is_destroyed(AiDifficulty difficulty)
    {
        // The AI's castle with ten footmen beside it; an enemy wooden tower just outside the town, out of their sight.
        var objects = new List<MapObject> { MapObject.AtTile("euro.castle", 0, 2, 2), MapObject.AtTile("euro.castle", 1, 40, 30) };
        objects.AddRange(Enumerable.Range(0, 10).Select(i => Unit("euro.footman", 1, 30 + (i % 5), 40 + (i / 5))));
        objects.Add(MapObject.AtTile("comm.tower_wood", 0, 50, 22));
        using Simulation sim = Sim(objects, difficulty, out _);
        EntityId tower = All(sim.World, "comm.tower_wood", 0).Single();
        Run(sim, 3000);
        output.WriteLine($"{difficulty}: tower alive {sim.World.Entities.IsAlive(tower)}, footmen {All(sim.World, "euro.footman", 1).Count}");
        Assert.False(sim.World.Entities.IsAlive(tower));
    }

    [Theory]
    [InlineData(AiDifficulty.Easy, true)]
    [InlineData(AiDifficulty.Hard, false)]
    public void Two_footmen_attack_a_stone_tower_only_on_easy(AiDifficulty difficulty, bool attacks)
    {
        // A stone tower whose reach comes into the town; the two footmen stand out of its reach.
        var objects = new List<MapObject> { MapObject.AtTile("euro.castle", 0, 2, 2), MapObject.AtTile("euro.castle", 1, 40, 30) };
        objects.AddRange(Enumerable.Range(0, 2).Select(i => Unit("euro.footman", 1, 30 + i, 44)));
        objects.Add(MapObject.AtTile("comm.tower_stone", 0, 58, 20));
        using Simulation sim = Sim(objects, difficulty, out _);
        EntityId tower = All(sim.World, "comm.tower_stone", 0).Single();
        bool ordered = false;
        var none = new CommandList();
        for (int t = 0; t < 600 && !ordered; t++)
        {
            sim.Tick(none);
            foreach (EntityId f in All(sim.World, "euro.footman", 1))
            {
                ordered |= sim.World.Combats.Get(f).Target == tower.Value;
            }
        }

        Assert.Equal(attacks, ordered);
    }

    [Theory]
    [InlineData(AiDifficulty.Easy, true)]
    [InlineData(AiDifficulty.Normal, false)]
    public void Gatherers_avoid_a_gold_mine_in_reach_of_an_enemy_tower(AiDifficulty difficulty, bool used)
    {
        // Two gold mines at the same distance from the castle; an enemy stone tower covers the first one (it is too strong
        // for the AI, which has no soldiers).
        var objects = new List<MapObject>
        {
            MapObject.AtTile("euro.castle", 0, 2, 2),
            MapObject.AtTile("euro.castle", 1, 30, 24),
            MapObject.AtTile("comm.res_gold", MapObject.Nature, 30, 14),
            MapObject.AtTile("comm.res_gold", MapObject.Nature, 30, 34),
            MapObject.AtTile("comm.tower_stone", 0, 30, 4),
        };
        objects.AddRange(Enumerable.Range(0, 10).Select(i => Unit("euro.peon", 1, 26 + (i % 5), 29 + (i / 5))));
        using Simulation sim = Sim(objects, difficulty, out _);
        World world = sim.World;
        EntityId covered = EntityId.None;
        foreach (EntityId g in All(world, "comm.res_gold", MapObject.Nature))
        {
            if (world.Positions.Get(g).Y < 20 * Tile)
            {
                covered = g;
            }
        }

        int on = 0;
        var none = new CommandList();
        for (int t = 0; t < 400; t++)
        {
            sim.Tick(none);
            foreach (EntityId w in All(world, "euro.peon", 1))
            {
                Worker state = world.Workers.Get(w);
                on = Math.Max(on, state.Task == WorkerTask.Harvest && state.Target == covered.Value ? 1 : 0);
            }
        }

        output.WriteLine($"{difficulty}: covered mine used {on}");
        Assert.Equal(used, on > 0);
    }

    [Theory]
    [InlineData(AiDifficulty.Easy, true)]
    [InlineData(AiDifficulty.Hard, false)]
    public void A_wave_waits_for_enough_power_against_a_defended_town(AiDifficulty difficulty, bool launches)
    {
        // Three footmen at home may launch at once; the enemy castle is covered by two stone towers.
        var objects = new List<MapObject>
        {
            MapObject.AtTile("euro.castle", 0, 4, 4),
            MapObject.AtTile("comm.tower_stone", 0, 10, 4),
            MapObject.AtTile("comm.tower_stone", 0, 4, 10),
            MapObject.AtTile("euro.castle", 1, 50, 36),
        };
        objects.AddRange(Enumerable.Range(0, 3).Select(i => Unit("euro.footman", 1, 44 + i, 32)));
        var options = new AiOptions { Military = new MilitaryOptions { FirstWaveMs = 0, FirstWaveSize = 3, MaxWaveSize = 10 } };
        using Simulation sim = Sim(objects, difficulty, out LegacyAiSystem ai, options);
        Run(sim, 200);
        Assert.Equal(launches, ai.Players[0].Waves > 0);
    }

    [Fact]
    public void The_gather_point_is_on_reachable_ground()
    {
        // Second play-test log: soldiers and peasants sent to a gather point inside a forest reported unreachable goals.
        // The town at (40, 30) gathers 7 tiles towards the map centre: around (34, 26), here a dense forest.
        var objects = new List<MapObject> { MapObject.AtTile("euro.castle", 0, 2, 2), MapObject.AtTile("euro.castle", 1, 40, 30), MapObject.AtTile("euro.barrack", 1, 46, 30) };
        objects.Add(Unit("euro.footman", 1, 44, 36));
        for (int x = 30; x <= 38; x++)
        {
            for (int y = 22; y <= 29; y++)
            {
                objects.Add(MapObject.AtTile("comm.tree", MapObject.Nature, x, y));
            }
        }

        using Simulation sim = Sim(objects, AiDifficulty.Normal, out _);
        World world = sim.World;
        Run(sim, 100);
        EntityId barracks = All(world, "euro.barrack", 1).Single();
        Production q = world.Productions.Get(barracks);
        Assert.Equal(1, q.HasRally);
        var layer = world.Navigation.ForType((ushort)world.Ruleset.EntityTypeId("euro.footman"))!;
        Assert.True(layer.IsWalkablePoint(q.RallyX, q.RallyY), $"rally at tile {q.RallyX / Tile},{q.RallyY / Tile}");
    }

    [Fact]
    public void Power_grows_with_health_and_damage_and_is_zero_unarmed()
    {
        var objects = new List<MapObject>
        {
            Unit("euro.footman", 0, 4, 4),
            Unit("euro.peon", 0, 6, 4),
            MapObject.AtTile("comm.tower_stone", 0, 10, 10),
            MapObject.AtTile("euro.castle", 1, 40, 30),
        };
        using Simulation sim = Sim(objects, AiDifficulty.Normal, out _);
        World world = sim.World;
        int footman = AiThreats.PowerOf(world, All(world, "euro.footman", 0).Single());
        int tower = AiThreats.PowerOf(world, All(world, "comm.tower_stone", 0).Single());
        output.WriteLine($"footman {footman}, peon {AiThreats.PowerOf(world, All(world, "euro.peon", 0).Single())}, stone tower {tower}, castle {AiThreats.PowerOf(world, All(world, "euro.castle", 1).Single())}");
        Assert.True(footman > 0);
        Assert.True(tower > 2 * footman);
        Assert.Equal(0, AiThreats.PowerOf(world, All(world, "euro.castle", 1).Single()));
    }
}
