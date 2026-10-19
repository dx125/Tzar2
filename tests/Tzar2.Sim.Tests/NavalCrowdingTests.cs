using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Xunit;
using Xunit.Abstractions;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Many ships and units at one shore (REQ-PATH-09, ADR 0146; campaign maps alex1, bigisland, vsaa): crowds of ships and
/// boarders must still dock, board and unload.
/// </summary>
public class NavalCrowdingTests(ITestOutputHelper output)
{
    // Land (columns 0-11), a sea (12-51), land again (52-63); 24 rows.
    private static readonly string[] Sea = [.. Enumerable.Repeat("............" + new string('~', 40) + "............", 24)];

    private static bool OnLand(SimDriver d, EntityId e) => d.World.Map.IsPassableAt(Locomotor.Foot, d.PositionOf(e).X, d.PositionOf(e).Y);

    [Fact]
    public void Four_full_ships_unloading_at_one_point_all_land_their_units()
    {
        var objects = Enumerable.Range(0, 4).Select(i => Unit("comm.galleon", 0, 14, 3 + (5 * i))).ToList();
        objects.AddRange(Enumerable.Range(0, 40).Select(i => Unit("euro.footman", 0, 2 + (i % 8), 1 + (i / 8 * 4) + (i % 3))));
        using var d = new SimDriver(AsciiMap(Sea, [.. objects]), ruleset: Classic);
        EntityId[] ships = [.. OfType(d.World, "comm.galleon")];
        EntityId[] footmen = [.. OfType(d.World, "euro.footman")];
        for (int s = 0; s < 4; s++)
        {
            d.Order(0, CommandKind.Board, footmen.Skip(10 * s).Take(10).ToArray(), target: ships[s]);
        }

        d.RunUntil(() => ships.All(s => d.World.Holders.Get(s).Count == 10), 3000);
        output.WriteLine($"aboard: {string.Join(",", ships.Select(s => d.World.Holders.Get(s).Count))} at tick {d.World.Tick.Value}");
        Assert.All(ships, s => Assert.Equal(10, d.World.Holders.Get(s).Count));

        d.Order(0, CommandKind.Unload, ships, 58 * Tile, 12 * Tile); // one landing point for all four
        int ticks = d.RunUntil(() => footmen.All(f => !d.World.IsHeld(f)), 6000);
        output.WriteLine($"landed {footmen.Count(f => !d.World.IsHeld(f))}/40 after {ticks} ticks; ships at {string.Join(" ", ships.Select(s => $"{d.PositionOf(s).X / Tile},{d.PositionOf(s).Y / Tile}"))}");
        Assert.All(footmen, f => Assert.False(d.World.IsHeld(f)));
        Assert.All(footmen, f => Assert.True(OnLand(d, f) && d.PositionOf(f).X / Tile >= 52));
    }

    [Fact]
    public void A_second_boarder_reaches_a_ship_that_is_already_docked()
    {
        using var d = new SimDriver(AsciiMap(Sea, Unit("comm.galleon", 0, 30, 12), Unit("euro.knight", 0, 6, 12), Unit("euro.knight", 0, 4, 20)), ruleset: Classic);
        EntityId ship = OfType(d.World, "comm.galleon").Single();
        EntityId[] heroes = [.. OfType(d.World, "euro.knight")];
        d.Order(0, CommandKind.Board, [heroes[0]], target: ship);
        d.RunUntil(() => d.World.IsHeld(heroes[0]), 2000);
        Assert.True(d.World.IsHeld(heroes[0]));
        d.RunUntil(() => false, 100); // docked, idle at the shore

        d.Order(0, CommandKind.Board, [heroes[1]], target: ship);
        int ticks = d.RunUntil(() => d.World.IsHeld(heroes[1]), 2000);
        output.WriteLine($"second boarder: held {d.World.IsHeld(heroes[1])} after {ticks} ticks at {d.PositionOf(heroes[1]).X / Tile},{d.PositionOf(heroes[1]).Y / Tile}; ship at {d.PositionOf(ship).X / Tile},{d.PositionOf(ship).Y / Tile}");
        Assert.True(d.World.IsHeld(heroes[1]));
    }

    [Fact]
    public void Boarders_reach_a_ship_through_a_crowd_of_own_ships_at_the_shore()
    {
        // Own ships lie side by side along the shore in front of the heroes; the ship they are told to board is among them.
        var objects = Enumerable.Range(0, 20).Select(i => Unit(i % 2 == 0 ? "comm.hulk" : "comm.xebec", 0, 13 + (i / 10), 2 + (2 * (i % 10)))).ToList();
        objects.AddRange(Enumerable.Range(0, 3).Select(i => Unit("euro.knight", 0, 7, 9 + i)));
        using var d = new SimDriver(AsciiMap(Sea, [.. objects]), ruleset: Classic);
        EntityId target = OfType(d.World, "comm.xebec").ElementAt(4);
        EntityId[] heroes = [.. OfType(d.World, "euro.knight")];
        d.Order(0, CommandKind.Board, heroes, target: target);
        int ticks = d.RunUntil(() => heroes.All(h => d.World.IsHeld(h)), 3000);
        output.WriteLine($"aboard {heroes.Count(h => d.World.IsHeld(h))}/3 after {ticks} ticks; target at {d.PositionOf(target).X / Tile},{d.PositionOf(target).Y / Tile}");
        Assert.All(heroes, h => Assert.True(d.World.IsHeld(h)));
    }

    [Fact]
    public void Boarders_reach_a_ship_behind_a_wall_of_own_ships()
    {
        // vsaa: two packed columns of the player's ships along the shore; the ship to board lies behind them.
        var objects = Enumerable.Range(0, 24).Select(i => Unit(i % 3 == 0 ? "comm.hulk" : i % 3 == 1 ? "comm.xebec" : "comm.fishing_boat", 0, 13 + (i / 12), 4 + (i % 12))).ToList();
        objects.Add(Unit("comm.xebec", 0, 16, 10));
        objects.AddRange(Enumerable.Range(0, 3).Select(i => Unit("euro.knight", 0, 9, 9 + i)));
        using var d = new SimDriver(AsciiMap(Sea, [.. objects]), ruleset: Classic);
        EntityId target = OfType(d.World, "comm.xebec").Last();
        EntityId[] heroes = [.. OfType(d.World, "euro.knight")];
        d.Order(0, CommandKind.Board, heroes, target: target);
        int ticks = d.RunUntil(() => heroes.All(h => d.World.IsHeld(h)), 4000);
        output.WriteLine($"behind the wall: aboard {heroes.Count(h => d.World.IsHeld(h))}/3 after {ticks} ticks; target at {d.PositionOf(target).X / Tile},{d.PositionOf(target).Y / Tile}");
        Assert.All(heroes, h => Assert.True(d.World.IsHeld(h)));
    }

    [Fact]
    public void Units_unloaded_at_their_own_landing_spot_stop_there()
    {
        // bigisland: the landing point is the tile the unit comes out on; its move there must end (not stay Moving).
        using var d = new SimDriver(AsciiMap(Sea, Unit("comm.galleon", 0, 13, 12), Unit("euro.pikeman", 0, 10, 12), Unit("euro.knight", 0, 10, 13)), ruleset: Classic);
        EntityId ship = OfType(d.World, "comm.galleon").Single();
        EntityId[] units = [.. OfType(d.World, "euro.pikeman"), .. OfType(d.World, "euro.knight")];
        d.Order(0, CommandKind.Board, units, target: ship);
        d.RunUntil(() => units.All(u => d.World.IsHeld(u)), 1000);
        d.Order(0, CommandKind.Move, [ship], 49 * Tile, 12 * Tile);
        d.RunUntil(() => !d.IsMoving(ship), 2000);
        d.Order(0, CommandKind.Unload, [ship], (52 * Tile) + (Tile / 2), (12 * Tile) + (Tile / 2));
        d.RunUntil(() => units.All(u => !d.World.IsHeld(u)), 1000);
        Assert.All(units, u => Assert.False(d.World.IsHeld(u)));
        d.RunUntil(() => units.All(u => !d.IsMoving(u)), 400);
        foreach (EntityId u in units)
        {
            Motion m = d.MotionOf(u);
            output.WriteLine($"{d.World.TypeOf(u).Key} at {d.PositionOf(u).X / Tile},{d.PositionOf(u).Y / Tile} {m.State} order {m.Order} to {m.OrderX / Tile},{m.OrderY / Tile} mode {m.Mode}");
        }

        Assert.All(units, u => Assert.NotEqual(MotionState.Moving, d.MotionOf(u).State));
    }

    [Fact]
    public void A_unit_sent_again_and_again_to_a_spot_where_another_stands_arrives()
    {
        // bigisland: the landing point is taken by another kingdom's idle peasant; the move is re-issued every 38 ticks
        // (as a script does), and must still end instead of staying Moving.
        using var d = new SimDriver(TestMap(24, 12, Unit("euro.pikeman", 0, 4, 5), Unit("arab.peon", 1, 12, 5)), players: 3, ruleset: Classic);
        d.World.Diplomacy.Set(0, 1, Kingdoms.Stance.Ally, Kingdoms.Sharing.None, 0);
        d.World.Diplomacy.Set(1, 0, Kingdoms.Stance.Ally, Kingdoms.Sharing.None, 0);
        EntityId pikeman = OfType(d.World, "euro.pikeman").Single();
        EntityId peon = OfType(d.World, "arab.peon").Single();
        Position spot = d.PositionOf(peon);
        bool idle = false;
        for (int round = 0; round < 30 && !idle; round++)
        {
            d.Order(0, CommandKind.Move, [pikeman], spot.X, spot.Y);
            for (int t = 0; t < 38 && !idle; t++)
            {
                d.Step();
                idle = round > 1 && d.MotionOf(pikeman).State != MotionState.Moving;
            }
        }

        output.WriteLine($"pikeman at {d.PositionOf(pikeman).X},{d.PositionOf(pikeman).Y}, spot {spot.X},{spot.Y}, {d.MotionOf(pikeman).State}");
        Assert.True(idle, "the pikeman kept Moving next to the taken spot");
    }

    [Fact]
    public void A_ship_sails_past_idle_ships_in_a_narrow_bay()
    {
        // A 6-tile-wide bay with four idle allied ships in it; the player's ship crosses it.
        string[] bay = [.. Enumerable.Range(0, 20).Select(y => y is >= 7 and < 13 ? "......" + new string('~', 40) + "......" : new string('.', 52))];
        var objects = new[] { Unit("comm.xebec", 0, 8, 9) }.Concat(Enumerable.Range(0, 4).Select(i => Unit("comm.hulk", 1, 22 + (4 * i), 8 + (i % 2 * 3)))).ToArray();
        using var d = new SimDriver(AsciiMap(bay, objects), ruleset: Classic, configure: c => c);
        d.World.Diplomacy.Set(0, 1, Kingdoms.Stance.Ally, Kingdoms.Sharing.None, 0);
        d.World.Diplomacy.Set(1, 0, Kingdoms.Stance.Ally, Kingdoms.Sharing.None, 0);
        EntityId ship = OfType(d.World, "comm.xebec").Single();
        d.Order(0, CommandKind.Move, [ship], 42 * Tile, 10 * Tile);
        int ticks = d.RunUntil(() => !d.IsMoving(ship) && d.World.Tick.Value > 5, 3000);
        output.WriteLine($"ship at {d.PositionOf(ship).X / Tile},{d.PositionOf(ship).Y / Tile} state {d.MotionOf(ship).State} after {ticks}");
        Assert.True(d.PositionOf(ship).X / Tile >= 40, $"ship stuck at tile {d.PositionOf(ship).X / Tile}");
    }
}
