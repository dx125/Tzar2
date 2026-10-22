using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Regression tests for the PR #21 review (owner decisions on repair and gates; restore of derived layers; bounded
/// capture and gate work; conversions; refunds).
/// </summary>
public class M3ReviewRegressionTests
{
    // H1: 10 footmen against a house with N repairers that are re-ordered whenever idle.
    private static (bool Alive, int Ticks, int WoodSpent) HouseUnderAttack(int repairers, int wood = 500)
    {
        var objects = new List<MapObject> { Structure("euro.castle", 0, 2, 2), Structure("euro.house", 0, 20, 10) };
        for (int i = 0; i < repairers; i++)
        {
            objects.Add(Unit("euro.peon", 0, 18 + (i % 4), 14 + (i / 4)));
        }

        for (int i = 0; i < 10; i++)
        {
            objects.Add(Unit("euro.footman", 1, 26 + (i % 2), 8 + i));
        }

        using var d = new SimDriver(TestMap(40, 24, [.. objects]), ruleset: Classic);
        d.World.Players[0].Resources[Resource("wood")] = wood;
        EntityId house = OfType(d.World, "euro.house").Single();
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];
        d.Order(1, CommandKind.Attack, [.. OfType(d.World, "euro.footman")], target: house);
        int t = 0;
        for (; t < 12_000 && d.World.Entities.IsAlive(house); t++)
        {
            if (repairers > 0 && t % 10 == 0 && d.World.Healths.Get(house).Current < d.World.Healths.Get(house).Max)
            {
                EntityId[] idle = [.. peons.Where(p => d.World.Entities.IsAlive(p) && d.World.Workers.Get(p).Task != WorkerTask.Repair)];
                if (idle.Length > 0)
                {
                    d.Order(0, CommandKind.Repair, idle, target: house);
                }
            }

            d.Step();
        }

        return (d.World.Entities.IsAlive(house), t, wood - d.World.Players[0].Resources[Resource("wood")]);
    }

    [Fact]
    [Trait("Rule", "BLD-022")]
    public void Repairers_slow_ten_footmen_down_but_never_for_long_and_cost_nothing()
    {
        // Measured repairs (batch J) are a quarter of the build rate per repairer, so even eight cannot outlast ten
        // footmen; every further repairer still helps (no falloff), and the wood is never touched.
        (bool alive0, int ticks0, _) = HouseUnderAttack(0);
        (bool alive4, int ticks4, int spent) = HouseUnderAttack(4);
        (bool alive8, int ticks8, _) = HouseUnderAttack(8);
        Assert.False(alive0);
        Assert.False(alive4);
        Assert.False(alive8);
        Assert.True(ticks4 > ticks0 && ticks8 > ticks4, $"{ticks0} < {ticks4} < {ticks8}");
        Assert.InRange(ticks8, ticks0, 3_000);
        Assert.Equal(0, spent);
    }

    [Fact]
    [Trait("Rule", "BLD-022")]
    public void A_repair_comes_in_blows_of_a_quarter_of_the_build_rate_from_each_repairer_and_costs_nothing()
    {
        // Batch J (2026-10-21): a blow restores max / (4 x TIME) hit points every 1.25 s, rounded down (mill 6, barracks
        // 5, castle 16); seven repairers each struck full blows; the stockpiles did not move.
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Structure("euro.house", 0, 12, 4), Unit("euro.peon", 0, 11, 7), Unit("euro.peon", 0, 16, 7)), ruleset: Classic);
        EntityId house = OfType(d.World, "euro.house").Single();
        EntityId[] peons = [.. OfType(d.World, "euro.peon")];
        Assert.Equal(6, d.World.Ruleset.Gameplay.RepairBlowHp(420, d.World.TypeOf(house).Traits.BuildTimeMs)); // 420 / (4 x TIME 16)
        d.World.Healths.Get(house).Current = 220;
        int[] stock = [.. Enumerable.Range(0, Ruleset.MaxResourceTypes).Select(r => d.World.Players[0].Resources[r])];
        d.Order(0, CommandKind.Repair, [peons[0]], target: house);

        // One repairer: steps of 6, one every 25 ticks of 50 ms once it is there.
        var steps = new List<(int Tick, int Gain)>();
        int last = 220;
        for (int t = 0; t < 200; t++)
        {
            d.Step();
            int now = d.World.Healths.Get(house).Current;
            if (now != last)
            {
                steps.Add((d.World.Tick.Value, now - last));
                last = now;
            }
        }

        Assert.True(steps.Count >= 4, $"{steps.Count} blows");
        Assert.All(steps, s => Assert.Equal(6, s.Gain));
        Assert.All(steps.Zip(steps.Skip(1)), p => Assert.Equal(25, p.Second.Tick - p.First.Tick));

        // A second repairer strikes as hard: twice the rate.
        int before = d.World.Healths.Get(house).Current;
        d.Order(0, CommandKind.Repair, [peons[1]], target: house);
        d.RunUntil(() => false, 100); // it walks there
        before = d.World.Healths.Get(house).Current;
        d.RunUntil(() => false, 100);
        Assert.Equal(48, d.World.Healths.Get(house).Current - before); // 2 x 4 blows of 6 in 5 s
        Assert.Equal(stock, Enumerable.Range(0, Ruleset.MaxResourceTypes).Select(r => d.World.Players[0].Resources[r]));
    }

    [Fact]
    public void Gate_orders_are_rate_limited_and_gate_changes_per_tick_are_bounded()
    {
        using var d = new SimDriver(TestMap(256, 256, Structure("euro.castle", 0, 2, 2), Structure("euro.castle", 1, 240, 240)), ruleset: Classic);
        var gates = new List<EntityId>();
        for (int g = 0; g < 200; g++)
        {
            gates.Add(d.World.PlaceStructure(Id("comm.gate_wood"), 0, 20 + ((g % 20) * 10), 20 + ((g / 20) * 10)));
        }

        for (int u = 0; u < 1000; u++)
        {
            d.Spawn(Id("euro.footman"), 1, 30 + ((u % 50) * 4), 140 + ((u / 50) * 4));
        }

        d.RunUntil(() => false, 20);
        // Deterministic cost proxies (owner decision; review R12: 16 changes per tick cost 15 ms p99, 4 cost 1.1 ms):
        // passability changes and navigation clusters rebuilt per tick stay bounded whatever the orders.
        int maxChanges = 0;
        long maxClusters = 0;
        int cap = Classic.Gameplay.MaxGateChangesPerTick;
        Assert.Equal(4, cap);
        for (int i = 0; i < 400; i++)
        {
            d.Order(0, CommandKind.SetGate, [.. gates], arg0: i % 2 == 0 ? 1 : 0); // spam: open/close all 200 every tick
            int before = d.World.Map.PassabilityVersion;
            d.World.Navigation.EnsureUpToDate();
            long clusters = d.World.Navigation.ClustersRebuilt;
            d.Step();
            d.World.Navigation.EnsureUpToDate(); // the update the next tick's path requests would do
            maxChanges = Math.Max(maxChanges, d.World.Map.PassabilityVersion - before);
            maxClusters = Math.Max(maxClusters, d.World.Navigation.ClustersRebuilt - clusters);
        }

        Assert.True(maxChanges <= cap, $"{maxChanges} passability changes in one tick"); // one passage tile per gate
        Assert.True(maxClusters <= cap * d.World.Navigation.LayerCount, $"{maxClusters} clusters rebuilt in one tick ({d.World.Navigation.LayerCount} layers)");

        // Orders to gates that changed less than 1 s ago are rejected.
        d.RunUntil(() => false, 40);
        int open = d.World.Gates.Get(gates[0]).Open;
        d.Order(0, CommandKind.SetGate, [gates[0]], arg0: 1 - open);
        d.Step();
        Assert.Equal(1 - open, d.World.Gates.Get(gates[0]).Open);
        d.Order(0, CommandKind.SetGate, [gates[0]], arg0: open);
        d.Step();
        Assert.Equal(CommandRejection.GateBusy, d.Rejections().Last());
        Assert.Equal(1 - open, d.World.Gates.Get(gates[0]).Open);
    }

    [Fact]
    [Trait("Rule", "ORD-013")]
    public void An_automatic_gate_has_hysteresis()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Unit("euro.footman", 0, 20, 12)), ruleset: Classic);
        EntityId gate = d.World.PlaceStructure(Id("comm.gate_wood"), 0, 15, 8);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        d.Step();
        Assert.Equal(0, d.World.Gates.Get(gate).Open);
        d.MoveToTile(0, [footman], 16, 9); // right below the gate: opens
        d.RunUntil(() => d.World.Gates.Get(gate).Open == 1, 300);
        Assert.Equal(1, d.World.Gates.Get(gate).Open);
        d.MoveToTile(0, [footman], 16, 10); // 1.5 tiles away: beyond the open reach, within the close reach
        d.RunUntil(() => !d.IsMoving(footman), 300);
        d.RunUntil(() => false, 40);
        Assert.Equal(1, d.World.Gates.Get(gate).Open);
        d.MoveToTile(0, [footman], 16, 14);
        d.RunUntil(() => d.World.Gates.Get(gate).Open == 0, 300);
        Assert.Equal(0, d.World.Gates.Get(gate).Open);
    }

    [Fact]
    public void Restore_rejects_bridge_and_gate_layers_that_do_not_match_the_structures()
    {
        string[] river = ["................................", "..............~~~...............", "..............~~~...............", "..............~~~...............", "..............~~~...............", "..............~~~...............", "..............~~~...............", "................................"];
        var world = new World(Classic, AsciiMap(river, Structure("euro.castle", 0, 0, 0), Structure("comm.bridge", 0, 13, 2)), new SimulationConfig { PlayerCount = 2 });
        world.LoadMap();
        world.Map.AddBridge(15, 1); // a phantom deck
        Assert.Throws<InvalidDataException>(() => Simulation.Restore(Snapshots.WorldSnapshot.Capture(world), Classic, world.Map.Data));

        var gates = new World(Classic, TestMap(32, 16, Structure("euro.castle", 0, 2, 2)), new SimulationConfig { PlayerCount = 2 });
        gates.LoadMap();
        EntityId gate = gates.PlaceStructure(Id("comm.gate_wood"), 0, 20, 9);
        gates.Gates.Get(gate).Open = 1; // the flag only: the passage is still blocked
        Assert.Throws<InvalidDataException>(() => Simulation.Restore(Snapshots.WorldSnapshot.Capture(gates), Classic, gates.Map.Data));
    }

    [Fact]
    public void Capture_work_does_not_grow_with_the_number_of_trees()
    {
        var objects = new List<MapObject> { Structure("euro.castle", 0, 2, 2), Structure("euro.castle", 1, 240, 240) };
        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                objects.Add(Tree(100 + x, 20 + (y * 3)));
            }
        }

        for (int i = 0; i < 100; i++)
        {
            objects.Add(Structure("euro.house", 1, 10 + ((i % 10) * 6), 100 + ((i / 10) * 6)));
        }

        using var d = new SimDriver(TestMap(256, 256, [.. objects]), ruleset: Classic);
        foreach (EntityId house in OfType(d.World, "euro.house", 1))
        {
            d.World.CaptureStates.Set(house, new CaptureState { Player = 0, MilliPoints = 1 });
        }

        d.Step();
        var sw = Stopwatch.StartNew();
        d.RunUntil(() => false, 90);
        Assert.True(sw.Elapsed.TotalMilliseconds / 90 < 25, $"{sw.Elapsed.TotalMilliseconds / 90:F2} ms per tick"); // loose for shared CI; ~0.1 ms quiet, was 3.4 ms
    }

    [Fact]
    public void Conversions_between_types_with_different_stateful_traits_are_rejected()
    {
        RulesException ex = Assert.Throws<RulesException>(() => SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions
        {
            OverrideTexts = [new KeyValuePair<string, string>("test.yaml", "comm.tower_wood2: { -Burnable: }")],
        }));
        Assert.Contains("must agree on", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refunds_return_what_was_paid()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2)), ruleset: Classic);
        EntityId castle = OfType(d.World, "euro.castle").Single();
        int food = Resource("food");
        d.World.Players[0].Resources[food] = 10_000;
        d.Order(0, CommandKind.Train, [castle], arg0: Id("euro.peon"), arg1: 3);
        d.Step();
        int afterQueue = d.World.Players[0].Resources[food];
        ResearchTests.Grant(d, 0, Classic, "te_feudalism"); // peasants become cheaper after they were paid
        d.Order(0, CommandKind.Cancel, [castle], arg0: -1);
        d.Step();
        Assert.Equal((10_000 - afterQueue) / 3, d.World.Players[0].Resources[food] - afterQueue);
    }
}
