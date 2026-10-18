using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// Snapshots of the world-system state (ADRs 0101-0107): visibility stamps, explored bits, last-seen records and
/// temporary reveals, diplomacy, kingdom statistics, loans and cheats, trade routes and Nature homes round-trip and
/// continue identically; corrupt values are rejected (ADR 0019).
/// </summary>
public class WorldSnapshotTests
{
    private const byte Nature = MapObject.Nature;

    private static MapData Busy() => TestMap(
        62,
        14,
        Structure("euro.market", 0, 2, 2),
        Structure("euro.market", 0, 40, 2),
        Structure("euro.tavern", 0, 20, 10),
        Structure("euro.house", 1, 54, 2),
        Structure("comm.magicgateway", Nature, 26, 2),
        Unit("euro.trader", 0, 6, 8),
        Unit("euro.peon", 0, 50, 4),
        Unit("euro.footman", 0, 22, 4),
        Unit("comm.bear", Nature, 30, 11),
        Unit("comm.cow", Nature, 10, 12),
        Unit("euro.footman", 1, 58, 11));

    [Fact]
    public void A_busy_world_round_trips_and_continues_identically()
    {
        using var d = new SimDriver(Busy(), ruleset: Classic, configure: c => c with { Victory = VictoryCondition.Standard });
        World w = d.World;
        ResearchTests.Grant(d, 0, Classic, "te_traderoutes", "te_usury");
        EntityId[] markets = [.. OfType(w, "euro.market").OrderBy(m => w.Positions.Get(m).X)];
        Position b = w.Positions.Get(markets[1]);
        d.Order(0, CommandKind.Trade, [OfType(w, "euro.trader").Single()], b.X, b.Y, target: markets[0], arg0: Resource("wood") | (Resource("gold") << 8));
        d.Order(0, CommandKind.SetDiplomacy, [], x: 1, arg0: (int)Stance.Neutral, arg1: (int)Sharing.Vision);
        d.Order(0, CommandKind.Loan, [OfType(w, "euro.tavern").Single()], arg0: Resource("stone"));
        d.Order(0, CommandKind.Tribute, [], x: 1, arg0: Resource("food"), arg1: 50);
        d.Order(0, CommandKind.Cheat, [], arg0: (int)CheatCode.NoFog);
        d.MoveToTile(0, [OfType(w, "euro.footman", 0).Single()], 27, 5); // touches the gateway
        d.RunUntil(() => false, 150);
        w.IsLocked = false;
        w.Visibility.Reveal(w, 1, 5, 5, 4, w.Tick.Value + 300);
        w.IsLocked = true;
        d.MoveToTile(0, [OfType(w, "euro.peon").Single()], 30, 4); // leaves the enemy house: a last-seen record
        d.RunUntil(() => false, 120);
        Assert.True(w.TradeRoutes.Count == 1 && w.NatureHomes.Count >= 1);
        Assert.Equal(OfType(w, "euro.house", 1).Single().Value, w.Visibility.MemoryOf(0, OfType(w, "euro.house", 1).Single().Index).Entity);
        Assert.Equal(0, w.Identities.Get(OfType(w, "comm.magicgateway").Single()).Owner); // claimed by the footman

        byte[] snapshot = d.Sim.CaptureSnapshot();
        using Simulation restored = Simulation.Restore(snapshot, Classic, w.Map.Data, workerCount: 3);
        Assert.Equal(snapshot, restored.CaptureSnapshot());
        Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
        var empty = new CommandList();
        for (int t = 0; t < 400; t++)
        {
            Assert.Equal(d.Step().StateHash, restored.Tick(empty).StateHash);
            if (t % 50 == 0)
            {
                Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
            }
        }
    }

    public static TheoryData<string> Corruptions() =>
    [
        "negative statistics", "loan of no resource", "cheat without the flag", "unknown stance", "self stance", "unknown sharing",
        "trade route on a footman", "route between one dock", "nature home on a kingdom's unit", "record of own building",
        "record of a unit type", "explored beyond the map", "outcome in the future",
    ];

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void Invalid_world_state_is_rejected(string corruption)
    {
        var world = new World(Classic, Busy(), new SimulationConfig { PlayerCount = 2 });
        world.LoadMap();
        EntityId footman = OfType(world, "euro.footman", 0).Single();
        EntityId trader = OfType(world, "euro.trader").Single();
        EntityId market = OfType(world, "euro.market").First();
        EntityId house = OfType(world, "euro.house", 1).Single();
        switch (corruption)
        {
            case "negative statistics": world.Kingdoms[0].UnitsLost = -1; break;
            case "loan of no resource": world.Kingdoms[0].LoanResource = 9; world.Kingdoms[0].LoanDue = 5; break;
            case "cheat without the flag": world.Kingdoms[1].Cheats = CheatFlags.God; break;
            case "unknown stance": world.Diplomacy.Set(0, 1, (Stance)7, Sharing.None, 0); break;
            case "self stance": world.Diplomacy.Set(1, 1, Stance.Ally, Sharing.None, 0); break;
            case "unknown sharing": world.Diplomacy.Set(0, 1, Stance.Ally, (Sharing)0x40, 0); break;
            case "trade route on a footman":
                world.TradeRoutes.Set(footman, new TradeRoute { DockA = market.Value, DockB = house.Value, Amount = 1, Phase = TradePhase.ToA });
                break;
            case "route between one dock":
                world.TradeRoutes.Set(trader, new TradeRoute { DockA = market.Value, DockB = market.Value, Amount = 1, Phase = TradePhase.ToA });
                break;
            case "nature home on a kingdom's unit": world.NatureHomes.Set(footman, new NatureHome { X = 100, Y = 100 }); break;
            case "record of own building":
                world.Visibility.MemoryRef(1, house.Index) = new Ghost { Entity = house.Value, TypeId = world.Identities.Get(house).TypeId, Owner = 1, TileX = 54, TileY = 2 };
                break;
            case "record of a unit type":
                world.Visibility.MemoryRef(0, footman.Index) = new Ghost { Entity = footman.Value, TypeId = world.Identities.Get(footman).TypeId, Owner = 1, TileX = 1, TileY = 1 };
                break;
            case "explored beyond the map": world.Visibility.ExploredBits(0)[^1] = ulong.MaxValue; break;
            case "outcome in the future": world.Kingdoms[0].OutcomeTick = 99; break;
        }

        byte[] snapshot = Snapshots.WorldSnapshot.Capture(world);
        Assert.Throws<InvalidDataException>(() => Simulation.Restore(snapshot, Classic, world.Map.Data));
    }
}
