using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// The original's choice of targets and its calls for help (ReTzar's reading of the original; the sixth play-test:
/// troops stood by while enemies were near, and units froze when their target could not be reached).
/// </summary>
public class TargetChoiceTests
{
    [Fact]
    [Trait("Rule", "WAR-015")]
    public void An_idle_fighter_takes_on_an_enemy_fighter_before_a_nearer_peasant()
    {
        using var d = new SimDriver(
            TestMap(32, 20, Unit("euro.footman", 0, 10, 10), Unit("euro.peon", 1, 12, 10), Unit("euro.archer", 1, 14, 10)),
            ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId archer = OfType(d.World, "euro.archer").Single();
        d.Order(1, CommandKind.SetStance, [archer], arg0: (int)CombatStance.HoldFire);
        d.RunUntil(() => d.World.Combats.Get(footman).Target != 0, 40);

        Assert.Equal(archer.Value, d.World.Combats.Get(footman).Target); // a fighter (4000) before a peasant (3000)
    }

    [Fact]
    [Trait("Rule", "WAR-015")]
    public void Walls_are_never_taken_on_by_a_units_own_choice_but_other_buildings_are()
    {
        using var d = new SimDriver(
            TestMap(40, 20, Unit("euro.footman", 0, 6, 10), Structure("comm.wall_wood", 1, 8, 10), Unit("euro.footman", 0, 26, 10), Structure("euro.house", 1, 28, 10)),
            ruleset: Classic);
        EntityId nearWall = OfType(d.World, "euro.footman").Single(f => d.PositionOf(f).X < 16 * Tile);
        EntityId nearHouse = OfType(d.World, "euro.footman").Single(f => d.PositionOf(f).X > 16 * Tile);
        d.RunUntil(() => false, 200);

        Assert.Equal(0u, d.World.Combats.Get(nearWall).Target);
        Assert.Equal(OfType(d.World, "euro.house").Single().Value, d.World.Combats.Get(nearHouse).Target);
    }

    [Fact]
    [Trait("Rule", "WAR-016")]
    public void A_unit_whose_target_falls_takes_on_the_next_one_at_once()
    {
        using var d = new SimDriver(
            TestMap(32, 20, Unit("euro.footman", 0, 10, 10), Unit("euro.peon", 1, 11, 10), Unit("euro.peon", 1, 11, 12)),
            ruleset: Classic);
        EntityId footman = OfType(d.World, "euro.footman").Single();
        EntityId first = OfType(d.World, "euro.peon").Single(p => d.PositionOf(p).Y < 11 * Tile);
        EntityId second = OfType(d.World, "euro.peon").Single(p => d.PositionOf(p).Y > 11 * Tile);
        d.Order(0, CommandKind.Attack, [footman], target: first);
        d.RunUntil(() => !d.World.Entities.IsAlive(first), 400);
        Assert.False(d.World.Entities.IsAlive(first));

        d.Step(); // the tick after the blow: no wait for the next scan
        Assert.Equal(second.Value, d.World.Combats.Get(footman).Target);
    }

    [Fact]
    [Trait("Rule", "WAR-017")]
    public void A_unit_hit_from_beyond_its_neighbours_sight_calls_them_to_help()
    {
        // The archer shoots the near footman from 7 tiles; the far footman stands 3 tiles behind it, 10 tiles from the
        // archer and beyond its own sight of 6: only the call for help brings it.
        using var d = new SimDriver(
            TestMap(40, 20, Unit("euro.footman", 0, 10, 10), Unit("euro.footman", 0, 7, 10), Unit("euro.archer", 1, 17, 10)),
            ruleset: Classic);
        EntityId near = OfType(d.World, "euro.footman").Single(f => d.PositionOf(f).X > 9 * Tile);
        EntityId far = OfType(d.World, "euro.footman").Single(f => d.PositionOf(f).X < 9 * Tile);
        EntityId archer = OfType(d.World, "euro.archer").Single();
        d.Order(1, CommandKind.Attack, [archer], target: near);
        d.RunUntil(() => d.Events.Any(e => e.Event.Kind == SimEventKind.Damaged && e.Event.Entity == near), 400);
        d.Step();

        Assert.Equal(archer.Value, d.World.Combats.Get(near).Target);
        Assert.Equal(archer.Value, d.World.Combats.Get(far).Target);
    }

    [Fact]
    [Trait("Rule", "ORD-001")]
    public void Guards_stand_within_three_tiles_of_what_they_guard_not_against_it()
    {
        using var d = new SimDriver(
            TestMap(40, 20, Structure("euro.house", 0, 20, 10), Unit("euro.footman", 0, 6, 10), Unit("euro.footman", 0, 23, 10)),
            ruleset: Classic);
        EntityId house = OfType(d.World, "euro.house").Single();
        EntityId walker = OfType(d.World, "euro.footman").Single(f => d.PositionOf(f).X < 16 * Tile);
        EntityId close = OfType(d.World, "euro.footman").Single(f => d.PositionOf(f).X > 16 * Tile);
        Position before = d.PositionOf(close);
        d.Order(0, CommandKind.Follow, [walker, close], target: house);
        d.RunUntil(() => false, 400);

        int away = Systems.Interaction.TilesFrom(d.World, d.PositionOf(walker), house);
        Assert.InRange(away, 2, 3); // it stopped once within three tiles, short of the house
        Assert.Equal(before, d.PositionOf(close)); // already within three tiles: it guards from where it stands
        Assert.Equal(MotionOrder.Follow, d.MotionOf(walker).Order);
    }
}
