using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Hashing;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>Combat (GAME-07; ADR 0038): damage, swing cycle, acquisition, deaths and corpses, determinism.</summary>
public class CombatTests
{
    private static MapData Battlefield() => TestMap(
        40,
        20,
        [
            .. Enumerable.Range(0, 6).Select(i => Unit("euro.footman", 0, 6 + (i % 2), 7 + (i / 2) * 2)),
            .. Enumerable.Range(0, 3).Select(i => Unit("euro.archer", 0, 4, 8 + (i * 2))),
            .. Enumerable.Range(0, 7).Select(i => Unit("euro.footman", 1, 30 + (i % 2), 6 + (i / 2) * 2)),
            .. Enumerable.Range(0, 2).Select(i => Unit("euro.maceman", 1, 33, 8 + (i * 2))),
        ]);

    [Fact]
    public void Damage_is_attack_times_bonus_minus_defence_with_a_minimum()
    {
        // archer (7) vs footman (defence 2), BONUS.INI arrow_unit vs footman -25%: 7 x 0.75 = 5 - 2 = 3.
        // footman (9) vs knight (defence 5), footman vs mounted +55%: 9 x 1.55 = 13 - 5 = 8. peon (3) vs knight: 3 - 5 -> 1 (minimum).
        using var d = new SimDriver(
            TestMap(24, 12, Unit("euro.archer", 0, 4, 4), Unit("euro.footman", 1, 8, 4), Unit("euro.footman", 0, 4, 8), Unit("euro.knight", 1, 5, 8), Unit("euro.peon", 0, 6, 9)),
            ruleset: Classic);
        EntityId archer = OfType(d.World, "euro.archer").Single();
        EntityId enemyFootman = OfType(d.World, "euro.footman", 1).Single();
        EntityId footman = OfType(d.World, "euro.footman", 0).Single();
        EntityId knight = OfType(d.World, "euro.knight").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Attack, [archer], target: enemyFootman);
        d.Order(0, CommandKind.Attack, [footman, peon], target: knight);
        d.RunUntil(() => false, 200);

        var byAttacker = d.Events.Where(e => e.Event.Kind == SimEventKind.Damaged).GroupBy(e => e.Event.X).ToDictionary(g => g.Key, g => g.Select(x => x.Event.Arg).Distinct().ToArray());
        Assert.Equal([3], byAttacker[archer.Index]);
        Assert.Equal([8], byAttacker[footman.Index]);
        Assert.Equal([1], byAttacker[peon.Index]);
    }

    [Fact]
    public void Hits_follow_the_swing_cycle()
    {
        // A footman next to an idle peasant: one hit per 1,000 ms (RATE 6), the first after the 500 ms hit delay.
        using var d = new SimDriver(TestMap(16, 8, Unit("euro.footman", 0, 4, 4), Unit("euro.house", 1, 0, 0) with { TypeKey = "euro.peon", X = (4 * Tile) + 1700, Y = (4 * Tile) + 1024 }), ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Attack, [footman], target: peon);
        d.RunUntil(() => !d.World.Entities.IsAlive(peon), 400);
        int[] hitTicks = [.. d.Events.Where(e => e.Event.Kind == SimEventKind.Damaged && e.Event.X == footman.Index).Select(e => e.Tick)];
        Assert.True(hitTicks.Length >= 5);
        for (int i = 1; i < hitTicks.Length; i++)
        {
            Assert.Equal(20, hitTicks[i] - hitTicks[i - 1]);
        }

        // 44 HP, defence 1, 9 - 1 = 8 per hit: 6 hits; a corpse decal that never blocks (ORIG-01).
        Assert.Equal(6, hitTicks.Length);
        Assert.Equal(1, d.Count(SimEventKind.Killed));
        Assert.Equal(1, d.World.Decals.Count);
        Assert.Equal(0, d.World.Map.ObstacleVersion);
    }

    [Fact]
    public void Idle_soldiers_acquire_enemies_in_sight_and_peasants_do_not()
    {
        using var d = new SimDriver(
            TestMap(32, 12, Unit("euro.footman", 0, 4, 4), Unit("euro.peon", 0, 4, 8), Unit("euro.footman", 1, 8, 4), Unit("euro.peon", 1, 8, 8)),
            ruleset: Classic);
        d.RunUntil(() => false, 20);
        EntityId footman = OfType(d.World, "euro.footman", 0).Single();
        EntityId peon = OfType(d.World, "euro.peon", 0).Single();
        Assert.Equal(OfType(d.World, "euro.footman", 1).Single().Value, d.World.Combats.Get(footman).Target);
        Assert.Equal(CombatMode.Auto, d.World.Combats.Get(footman).Mode);
        Assert.Equal(0u, d.World.Combats.Get(peon).Target);
    }

    [Fact]
    public void A_move_advance_fights_on_the_way_and_then_reaches_its_destination()
    {
        using var d = new SimDriver(TestMap(40, 12, Unit("euro.footman", 0, 3, 5), Unit("euro.peon", 1, 14, 5)), ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.Order(0, CommandKind.Move, [footman], (34 * Tile) + (Tile / 2), (5 * Tile) + (Tile / 2), flags: CommandFlags.Ctrl);
        d.RunUntil(() => !d.World.Entities.IsAlive(peon), 1500);
        Assert.False(d.World.Entities.IsAlive(peon));
        d.RunUntil(() => d.World.Combats.Get(footman).Mode == CombatMode.None, 1500);
        Position at = d.PositionOf(footman);
        Assert.InRange(at.X / Tile, 32, 36);
        Assert.Equal(CombatMode.None, d.World.Combats.Get(footman).Mode);

        // A plain move ignores enemies it passes (the peasant two tiles beside the way is in sight).
        using var e = new SimDriver(TestMap(40, 12, Unit("euro.footman", 0, 3, 5), Unit("euro.peon", 1, 14, 7)), ruleset: Classic);
        EntityId walker = OfType(e.World, "euro.footman").Single();
        e.MoveToTile(0, [walker], 34, 5);
        e.RunUntil(() => !e.IsMoving(walker) || e.World.Combats.Get(walker).Target != 0, 1500);
        Assert.Equal(0u, e.World.Combats.Get(walker).Target);
        Assert.InRange(e.PositionOf(walker).X / Tile, 32, 36);
    }

    [Fact]
    public void A_plain_move_goes_around_an_enemy_in_its_way()
    {
        // Owner decision: a plain move blocked by an enemy tries to get around it first (ADR 0025 amended).
        using var d = new SimDriver(TestMap(40, 12, Unit("euro.footman", 0, 3, 5), Unit("euro.peon", 1, 14, 5)), ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId peon = OfType(d.World, "euro.peon").Single();
        d.MoveToTile(0, [footman], 34, 5);
        d.RunUntil(() => !d.IsMoving(footman), 1500);
        Assert.InRange(d.PositionOf(footman).X / Tile, 32, 36);
        Assert.True(d.World.Entities.IsAlive(peon));
        Assert.Equal(0, d.Count(SimEventKind.Blocked));
    }

    [Fact]
    public void A_unit_with_no_way_past_an_enemy_stops_and_fights_it()
    {
        // A one-tile corridor through a cliff wall, held by an enemy: no alternative, so the soldier stops (Blocked)
        // and, idle, acquires the blocker; a move-advance engages it at once.
        string wall = "####################.###################";
        string open = "........................................";
        string[] rows = [open, open, open, open, wall, wall, wall, open, open, open, open, open];
        foreach (bool advance in new[] { false, true })
        {
            using var d = new SimDriver(AsciiMap(rows, Unit("euro.footman", 0, 20, 1), Unit("euro.peon", 1, 20, 5)), ruleset: Classic);
            EntityId footman = OfType(d.World, "euro.footman").Single();
            EntityId peon = OfType(d.World, "euro.peon").Single();
            d.Order(0, CommandKind.Move, [footman], (20 * Tile) + (Tile / 2), (10 * Tile) + (Tile / 2), flags: advance ? CommandFlags.Ctrl : CommandFlags.None);
            d.RunUntil(() => !d.World.Entities.IsAlive(peon), 2000);
            Assert.False(d.World.Entities.IsAlive(peon));
            Assert.Equal(advance ? 0 : 1, d.Count(SimEventKind.Blocked));
            Assert.Equal(1, d.Events.Count(x => x.Event.Kind == SimEventKind.Killed && x.Event.X == footman.Index));
        }
    }

    [Fact]
    public void Soldiers_destroy_a_building_which_frees_its_tiles_and_leaves_no_corpse()
    {
        using var d = new SimDriver(
            TestMap(24, 12, [Structure("euro.house", 1, 12, 4), .. Enumerable.Range(0, 4).Select(i => Unit("euro.footman", 0, 4, 3 + i))]),
            ruleset: Classic);
        EntityId house = OfType(d.World, "euro.house").Single();
        EntityId[] squad = [.. OfType(d.World, "euro.footman")];
        d.Order(0, CommandKind.Attack, squad, target: house);
        d.RunUntil(() => !d.World.Entities.IsAlive(house), 2000);
        Assert.False(d.World.Entities.IsAlive(house));
        // RESISTANCE 40 (manual ch. 4, scale UNVERIFIED): 9 x 100 / 140 = 6 per hit.
        Assert.Equal([6], d.Events.Where(e => e.Event.Kind == SimEventKind.Damaged).Select(e => e.Event.Arg).Distinct());
        Assert.True(d.World.Map.IsPassable(Locomotor.Foot, 12, 4));
        Assert.Equal(0, d.World.Decals.Count);
        d.Step();
        Assert.All(squad, s => Assert.Equal(0u, d.World.Combats.Get(s).Target));
    }

    [Fact]
    public void Two_squads_fight_to_a_deterministic_outcome_on_any_worker_count()
    {
        (ulong stream1, string outcome1) = Fight(workers: 1);
        (ulong stream4, string outcome4) = Fight(workers: 4);
        Assert.Equal(stream1, stream4);
        Assert.Equal(outcome1, outcome4);
        Assert.Contains("winner", outcome1, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fight_restored_from_a_snapshot_continues_identically()
    {
        using var reference = NewFight(1);
        var hashes = new List<ulong>();
        int firstKill = -1;
        for (int t = 0; t < 1500; t++)
        {
            hashes.Add(reference.Step().StateHash);
            if (firstKill < 0 && reference.Count(SimEventKind.Killed) > 0)
            {
                firstKill = t;
            }
        }

        Assert.True(firstKill > 0, "nobody died");

        // Snapshot in the middle of the fight (swings and chases in progress), restore with another worker count.
        using var original = NewFight(1);
        int split = firstKill + 5;
        for (int t = 0; t <= split; t++)
        {
            original.Step();
        }

        byte[] snapshot = original.Sim.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, Classic, original.World.Map.Data, workerCount: 3);
        Assert.Equal(original.Sim.ComputeFullHash(), restored.ComputeFullHash());
        Assert.Contains(restored.World.Combats.Components.ToArray(), c => c.HitInMs >= 0 || c.Target != 0);
        var empty = new CommandList();
        for (int t = split + 1; t < 1500; t++)
        {
            Assert.Equal(hashes[t], restored.Tick(empty).StateHash);
        }
    }

    private static SimDriver NewFight(int workers)
    {
        var d = new SimDriver(Battlefield(), workers: workers, ruleset: Classic);
        // Both sides move-advance to the middle.
        d.Order(0, CommandKind.Move, [.. OfType(d.World, "euro.footman", 0), .. OfType(d.World, "euro.archer", 0)], 20 * Tile, 10 * Tile, flags: CommandFlags.Ctrl);
        d.Order(1, CommandKind.Move, [.. OfType(d.World, "euro.footman", 1), .. OfType(d.World, "euro.maceman", 1)], 20 * Tile, 10 * Tile, flags: CommandFlags.Ctrl);
        return d;
    }

    private static (ulong Stream, string Outcome) Fight(int workers)
    {
        using SimDriver d = NewFight(workers);
        var stream = new StateHasher();
        int alive0 = 0;
        int alive1 = 0;
        for (int t = 0; t < 3000; t++)
        {
            TickResult r = d.Step();
            stream.Add(r.StateHash).Add(r.FullHash ?? 0UL);
            alive0 = d.World.Combats.Entities.ToArray().Count(e => d.World.Identities.Get(e).Owner == 0);
            alive1 = d.World.Combats.Entities.ToArray().Count(e => d.World.Identities.Get(e).Owner == 1);
            if (alive0 == 0 || alive1 == 0)
            {
                break;
            }
        }

        Assert.True(alive0 == 0 || alive1 == 0, $"the fight did not end: {alive0} vs {alive1}");
        Assert.Equal(d.Count(SimEventKind.Killed), d.World.Decals.Count); // one corpse per fallen unit
        string outcome = $"winner {(alive0 > 0 ? 0 : 1)} with {Math.Max(alive0, alive1)} left after tick {d.World.Tick.Value}, hash {d.Sim.ComputeFullHash():X16}";
        return (stream.GetCurrentHash(), outcome);
    }
}
