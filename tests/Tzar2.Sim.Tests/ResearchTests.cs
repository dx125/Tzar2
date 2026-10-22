using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// GAME-06 (ADR 0040): research and upgrades on synthetic tables — queues, costs, times, per-civ prerequisites, one
/// research per technology, effects through the modifier tables, conversions and levels.
/// </summary>
public class ResearchTests
{
    private static int Gold => Resource("gold");

    private static int Tech(string key) => Classic.TechnologyId(key) is >= 0 and var id ? id : throw new ArgumentException(key);

    private static int UpgradeId(string key) => Classic.UpgradeId(key) is >= 0 and var id ? id : throw new ArgumentException(key);

    private static MapData Base(params MapObject[] extra) => TestMap(
        40,
        24,
        [Structure("euro.castle", 0, 2, 2), Structure("euro.blacksmith", 0, 10, 2), Structure("euro.blacksmith", 0, 14, 2), .. extra]);

    [Fact]
    [Trait("Rule", "TECH-001")]
    public void Research_is_paid_queued_timed_and_then_known()
    {
        using var d = new SimDriver(Base(), ruleset: Classic);
        EntityId smith = OfType(d.World, "euro.blacksmith").First();
        TechnologyDefinition temper = Classic.Technologies[Tech("te_temperiron")];
        d.Order(0, CommandKind.Research, [smith], arg0: temper.Id);
        d.Step();
        Assert.Equal(500 - temper.Cost[Gold], d.World.Players[0].Resources[Gold]);
        Assert.Equal(QueueItems.Research(temper.Id), d.World.Productions.Get(smith).Queue[0].Item);
        Assert.False(d.World.Players[0].HasTechnology(temper.Id));

        int ticks = d.RunUntil(() => d.Count(SimEventKind.ResearchCompleted) == 1, 2000);
        int tickMs = d.World.Config.TickLengthMilliseconds;
        Assert.Equal((temper.TimeMs + tickMs - 1) / tickMs, ticks + 1); // progress starts the tick it is queued
        Assert.True(d.World.Players[0].HasTechnology(temper.Id));
        Assert.Equal(0, d.World.Productions.Get(smith).Count);
        SimEvent done = d.Events.Single(e => e.Event.Kind == SimEventKind.ResearchCompleted).Event;
        Assert.Equal((smith, temper.Id), (done.Entity, done.Arg));
    }

    [Fact]
    [Trait("Rule", "TECH-001")]
    public void A_technology_is_researched_once_per_player()
    {
        using var d = new SimDriver(Base(), ruleset: Classic);
        EntityId[] smiths = [.. OfType(d.World, "euro.blacksmith")];
        int temper = Tech("te_temperiron");
        d.Order(0, CommandKind.Research, [smiths[0]], arg0: temper);
        d.Order(0, CommandKind.Research, [smiths[1]], arg0: temper); // same tick, another building (1.10 fix)
        d.Step();
        Assert.Equal([CommandRejection.AlreadyResearched], d.Rejections());
        Assert.Equal(0, d.World.Productions.Get(smiths[1]).Count);
        d.RunUntil(() => d.World.Players[0].HasTechnology(temper), 2000);
        d.Order(0, CommandKind.Research, [smiths[1]], arg0: temper);
        d.Step();
        Assert.Equal(CommandRejection.AlreadyResearched, d.Rejections().Last());
    }

    [Fact]
    [Trait("Rule", "PROD-006")]
    [Trait("Rule", "TECH-002")]
    public void Prerequisites_follow_the_civilization_tree_and_unlock_units()
    {
        using var d = new SimDriver(Base(Structure("euro.barrack", 0, 20, 2)), ruleset: Classic);
        EntityId smith = OfType(d.World, "euro.blacksmith").First();
        EntityId barracks = OfType(d.World, "euro.barrack").Single();

        // Halberd needs Scale Armor (synthetic euro tree); pikemen need Halberd (table).
        d.Order(0, CommandKind.Research, [smith], arg0: Tech("te_halberd"));
        d.Order(0, CommandKind.Train, [barracks], arg0: Id("euro.pikeman"), arg1: 1);
        d.Step();
        Assert.Equal([CommandRejection.RequirementNotMet, CommandRejection.RequirementNotMet], d.Rejections());

        d.Order(0, CommandKind.Research, [smith], arg0: Tech("te_scalearmor"));
        d.RunUntil(() => d.World.Players[0].HasTechnology(Tech("te_scalearmor")), 3000);
        d.Order(0, CommandKind.Research, [smith], arg0: Tech("te_halberd"));
        d.RunUntil(() => d.World.Players[0].HasTechnology(Tech("te_halberd")), 3000);
        d.Order(0, CommandKind.Train, [barracks], arg0: Id("euro.pikeman"), arg1: 1);
        d.RunUntil(() => d.Count(SimEventKind.UnitTrained) == 1, 1000);
        Assert.Single(OfType(d.World, "euro.pikeman"));
    }

    [Fact]
    public void Effects_change_attack_defence_cost_gathering_population_and_hit_points()
    {
        Ruleset rules = ClassicWith("""
            te_temperiron: { Technology: { effects: [{ stat: attack, add: 3, classes: [footman] }] } }
            te_scalearmor: { Technology: { effects: [{ stat: defence, add: 2, groups: [infantry_units] }] } }
            te_feudalism: { Technology: { effects: [{ stat: cost, percent: -40, classes: [peon] }] } }
            te_supplystorage: { Technology: { effects: [{ stat: populationProvided, add: 5, classes: [house] }] } }
            te_greatwall: { Technology: { effects: [{ stat: maxHealth, percent: 50, classes: [house] }] } }
            """);
        using var d = new SimDriver(TestMap(40, 24, Structure("euro.castle", 0, 2, 2), Structure("euro.house", 0, 20, 2), Structure("euro.house", 1, 20, 8)), ruleset: rules);
        World world = d.World;
        EntityId house = OfType(world, "euro.house", 0).Single();
        world.Healths.Get(house).Current = 300; // 120 damage taken before the research
        Grant(d, 0, rules, "te_temperiron", "te_scalearmor", "te_feudalism", "te_supplystorage", "te_greatwall");
        d.Step();

        ushort footman = Id(rules, "euro.footman");
        Assert.Equal(9 + 3, world.Stats.Apply(0, ModifierStat.Attack, footman, rules.EntityTypes[footman].Traits.Armament!.Attack));
        Assert.Equal(9, world.Stats.Apply(1, ModifierStat.Attack, footman, 9)); // player 1 has not researched it
        Assert.Equal(2 + 2, world.Stats.Apply(0, ModifierStat.Defence, footman, 2));
        ushort peon = Id(rules, "euro.peon");
        Assert.Equal(27, Systems.ResearchRules.CostOf(world, 0, rules.EntityTypes[peon], Resource("food"))); // 45 x 0.6
        Assert.Equal(24 + 12 + 5, world.Players[0].PopulationCap); // castle 24 + house 12 + 5
        Assert.Equal((630, 630 - 120), (world.Healths.Get(house).Max, world.Healths.Get(house).Current)); // 420 x 1.5, damage kept
        Assert.Equal(420, world.Healths.Get(OfType(world, "euro.house", 1).Single()).Max);

        // A cheaper peasant is paid at the new price.
        EntityId castle = OfType(world, "euro.castle").Single();
        d.Order(0, CommandKind.Train, [castle], arg0: peon, arg1: 1);
        d.Step();
        Assert.Equal(500 - 27, world.Players[0].Resources[Resource("food")]);
    }

    [Fact]
    public void Gather_technologies_speed_up_gathering_from_their_nodes()
    {
        Ruleset rules = ClassicWith("te_woodplus: { Technology: { effects: [{ stat: gatherRate, percent: 100, classes: [tree] }] } }");
        // Ticks from the first chop until the whole load appears in the peasant's hands (ADR 0180), and the load.
        (int Ticks, int Load) Gathered(bool researched)
        {
            using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Tree(12, 4), Tree(12, 5), Unit("euro.peon", 0, 11, 4)), ruleset: rules);
            if (researched)
            {
                Grant(d, 0, rules, "te_woodplus");
            }

            EntityId peon = OfType(d.World, "euro.peon").Single();
            d.Order(0, CommandKind.Harvest, [peon], target: OfType(d.World, "comm.tree").First());
            d.RunUntil(() => d.World.Workers.Get(peon).Phase == WorkerPhase.Gathering, 200);
            int start = d.World.Tick.Value;
            d.RunUntil(() => d.World.Workers.Get(peon).CarryAmount > 0, 2000);
            return (d.World.Tick.Value - start, d.World.Workers.Get(peon).CarryAmount);
        }

        (int plain, int load) = Gathered(false);
        (int faster, int fasterLoad) = Gathered(true);
        Assert.Equal(GameplayCommandsCapacity(), load); // the whole load at once
        Assert.Equal(load, fasterLoad);
        Assert.InRange(faster * 2, plain - 2, plain + 2); // twice as fast

        int GameplayCommandsCapacity() => rules.EntityTypes[rules.EntityTypeId("euro.peon")].Traits.Harvester!.Capacity;
    }

    [Fact]
    [Trait("Rule", "BLD-003")]
    [Trait("Rule", "TECH-035")]
    public void Upgrades_convert_towers_in_place_in_order_and_need_their_technology()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 2, 2), Structure("comm.tower_wood", 0, 12, 4)), ruleset: Classic);
        EntityId tower = OfType(d.World, "comm.tower_wood").Single();
        d.World.Healths.Get(tower).Current = 300; // damage carries over
        d.Order(0, CommandKind.Upgrade, [tower], arg0: UpgradeId("to_wtower2"));
        d.Order(0, CommandKind.Upgrade, [tower], arg0: UpgradeId("to_wtower2")); // bought once
        d.Step();
        Assert.Equal([CommandRejection.AlreadyResearched], d.Rejections());
        d.RunUntil(() => d.Count(SimEventKind.UpgradeCompleted) == 1, 2000);
        Assert.Equal(Id("comm.tower_wood2"), d.World.Identities.Get(tower).TypeId);
        Assert.Equal(990 - 90, d.World.Healths.Get(tower).Current);
        Assert.Equal(11, Classic.EntityTypes[d.World.Identities.Get(tower).TypeId].Traits.Armament!.Attack);
        Assert.False(d.World.Map.IsPassable(Locomotor.Foot, 12, 4)); // the footprint stays blocked

        // The keep tower needs Architecture (euro tree).
        d.Order(0, CommandKind.Upgrade, [tower], arg0: UpgradeId("to_stower"));
        d.Step();
        Assert.Equal(CommandRejection.RequirementNotMet, d.Rejections().Last());
        Grant(d, 0, Classic, "te_architecture");
        d.Order(0, CommandKind.Upgrade, [tower], arg0: UpgradeId("to_stower"));
        d.RunUntil(() => d.Count(SimEventKind.UpgradeCompleted) == 2, 2000);
        Assert.Equal(Id("comm.tower_stone"), d.World.Identities.Get(tower).TypeId);
    }

    [Fact]
    [Trait("Rule", "TECH-035")]
    [Trait("Rule", "TECH-036")]
    public void Ship_cannon_levels_are_bought_in_order_and_make_that_ships_cannons_fire_more_often()
    {
        using var d = new SimDriver(
            AsciiMap(["......~~~~~~~~~~", "......~~~~~~~~~~", "......~~~~~~~~~~", "......~~~~~~~~~~", "......~~~~~~~~~~", "......~~~~~~~~~~", "......~~~~~~~~~~", "......~~~~~~~~~~"], Structure("euro.castle", 0, 0, 0), Unit("comm.galleon", 0, 9, 1), Unit("comm.galleon", 0, 12, 4)),
            ruleset: Classic);
        EntityId[] ships = [.. OfType(d.World, "comm.galleon")];
        d.Order(0, CommandKind.Upgrade, [ships[0]], arg0: UpgradeId("to_ship_level3"));
        d.Step();
        Assert.Equal(CommandRejection.RequirementNotMet, d.Rejections().Last()); // level 2 first
        d.Order(0, CommandKind.Upgrade, [ships[0]], arg0: UpgradeId("to_ship_level2"));
        d.Order(0, CommandKind.Upgrade, [ships[0]], arg0: UpgradeId("to_ship_level3")); // queued after level 2
        d.RunUntil(() => d.Count(SimEventKind.UpgradeCompleted) == 2, 3000);
        Assert.Equal((1u << UpgradeId("to_ship_level2")) | (1u << UpgradeId("to_ship_level3")), d.World.UpgradeLevels.Get(ships[0]).Mask);
        Assert.False(d.World.UpgradeLevels.Has(ships[1]));
        // Help HT_UPGRADE_SHIPS: the cannons "fire more frequently": the period 3000 -> 2400 -> 1900 (ReTzar, 0x4e8f00); the
        // attack stays.
        Assert.Equal(158, Systems.ResearchRules.LevelBonus(d.World, ships[0], ModifierStat.FireRate, 100));
        Assert.Equal(100, Systems.ResearchRules.LevelBonus(d.World, ships[1], ModifierStat.FireRate, 100));
        Assert.Equal(33, Systems.ResearchRules.LevelBonus(d.World, ships[0], ModifierStat.Attack, 33));
    }

    [Fact]
    public void Cancelling_research_refunds_it_and_snapshots_keep_research_state()
    {
        using var d = new SimDriver(Base(), ruleset: Classic);
        EntityId smith = OfType(d.World, "euro.blacksmith").First();
        d.Order(0, CommandKind.Research, [smith], arg0: Tech("te_scalearmor"));
        d.Order(0, CommandKind.Research, [smith], arg0: Tech("te_temperiron"));
        d.RunUntil(() => false, 40);
        d.Order(0, CommandKind.Cancel, [smith], arg0: 1);
        d.Step();
        Assert.Equal(500 - Classic.Technologies[Tech("te_scalearmor")].Cost[Gold], d.World.Players[0].Resources[Gold]);
        Grant(d, 1, Classic, "te_fields");

        byte[] snapshot = d.Sim.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, Classic, d.World.Map.Data);
        Assert.True(restored.World.Players[1].HasTechnology(Tech("te_fields")));
        Assert.Equal(d.World.Stats.Apply(1, ModifierStat.GatherRate, Id("comm.field"), 1000), restored.World.Stats.Apply(1, ModifierStat.GatherRate, Id("comm.field"), 1000));
        var empty = new CommandList();
        for (int t = 0; t < 500; t++)
        {
            Assert.Equal(d.Step().StateHash, restored.Tick(empty).StateHash);
        }

        Assert.True(restored.World.Players[0].HasTechnology(Tech("te_scalearmor")));
    }

    /// <summary>Marks technologies researched for a player as a completed research would (test setup).</summary>
    internal static void Grant(SimDriver d, byte player, Ruleset rules, params string[] keys)
    {
        foreach (string key in keys)
        {
            d.World.IsLocked = false;
            Systems.ResearchRules.CompleteResearch(d.World, player, rules.TechnologyId(key), EntityId.None);
            d.World.IsLocked = true;
        }
    }
}
