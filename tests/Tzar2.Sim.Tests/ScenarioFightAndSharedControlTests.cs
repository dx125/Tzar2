using System.Linq;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.CombatMagicFixtures;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// ADR 0135: a scenario's staged fight between allies (Object Order "attack" on an object), and the orders shared unit
/// control (GAME-13) covers beyond fighting — boarding, unloading and carrying items.
/// </summary>
public class ScenarioFightAndSharedControlTests
{
    private sealed class Probe(System.Action<World> action) : ISimSystem
    {
        public SimPhase Phase => SimPhase.Scripts;

        public string Name => "probe";

        public System.Action<World>? Action { get; set; } = action;

        public void Execute(SimContext context)
        {
            Action?.Invoke(context.World);
            Action = null;
        }
    }

    private static void Ally(SimDriver d, int a, int b, Sharing offers = Sharing.None)
    {
        d.Order(a, CommandKind.SetDiplomacy, [], x: b, arg0: (int)Stance.Ally, arg1: (int)offers);
        d.Order(b, CommandKind.SetDiplomacy, [], x: a, arg0: (int)Stance.Ally, arg1: (int)offers);
        d.Step();
    }

    private static int Damage(SimDriver d, EntityId target) => d.Events.Count(e => e.Event.Kind == SimEventKind.Damaged && e.Event.Entity == target);

    [Fact]
    public void A_scripted_attack_fights_an_ally_until_another_order_and_survives_a_snapshot()
    {
        using var d = new SimDriver(TestMap(32, 16, Unit("euro.footman", 0, 10, 8), Unit("euro.footman", 1, 12, 8)), ruleset: Classic);
        World w = d.World;
        EntityId mine = w.MapObjectEntities[0];
        EntityId ally = w.MapObjectEntities[1];
        var probe = new Probe(_ => { });
        d.Sim.Systems.Add(probe);
        Ally(d, 0, 1);
        d.Order(0, CommandKind.Attack, [mine], target: ally);
        d.Step();
        Assert.Contains(CommandRejection.NotAllied, d.Rejections()); // players cannot attack an ally

        probe.Action = world => Assert.True(ScenarioOperations.ScriptedAttack(world, mine, ally));
        Assert.Throws<System.InvalidOperationException>(() => ScenarioOperations.ScriptedAttack(w, mine, ally)); // between ticks
        d.RunUntil(() => Damage(d, ally) > 0, 400);
        Assert.True(Damage(d, ally) > 0);
        Assert.Equal(CombatMode.Scripted, w.Combats.Get(mine).Mode);
        Assert.Equal(Stance.Ally, w.Diplomacy.StanceOf(0, 1)); // the stances stay as they are

        byte[] snapshot = d.Sim.CaptureSnapshot();
        using (Simulation restored = Simulation.Restore(snapshot, Classic, w.Map.Data))
        {
            Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
        }

        d.MoveToTile(0, [mine], 2, 2); // a new order ends the fight
        d.Step();
        int hits = Damage(d, ally);
        d.RunUntil(() => false, 100);
        Assert.Equal(hits, Damage(d, ally));
        Assert.Equal(CombatMode.None, w.Combats.Get(mine).Mode);
    }

    [Fact]
    public void Shared_unit_control_boards_the_allys_units_into_own_and_their_holders_and_carries_items()
    {
        using var d = new SimDriver(TestMap(32, 16, Structure("euro.castle", 0, 14, 5), Unit("euro.footman", 1, 8, 5), Unit("euro.footman", 1, 8, 7), Unit("comm.item", Content.Compiled.MapObject.Nature, 6, 10)), ruleset: Classic);
        World w = d.World;
        EntityId castle = OfType(w, "euro.castle").Single();
        EntityId[] theirs = [.. OfType(w, "euro.footman", 1)];
        EntityId item = OfType(w, "comm.item").Single();
        d.Order(0, CommandKind.Board, theirs, target: castle);
        d.Step();
        Assert.Contains(CommandRejection.NoValidEntities, d.Rejections()); // no unit control yet

        Ally(d, 0, 1, Sharing.UnitControl);
        d.Order(0, CommandKind.Board, [theirs[0]], target: castle);
        d.RunUntil(() => w.IsHeld(theirs[0]), 400);
        Assert.True(w.IsHeld(theirs[0])); // the ally's unit in the commanding player's castle

        d.Order(0, CommandKind.Unload, [castle], 10 * Tile, 12 * Tile);
        d.Step();
        Assert.False(w.IsHeld(theirs[0]));

        d.Order(0, CommandKind.PickItem, [theirs[1]], target: item);
        d.RunUntil(() => w.IsHeld(item), 400);
        Assert.True(w.IsHeld(item));
        d.Order(0, CommandKind.DropItem, [theirs[1]], arg0: 0);
        d.Step();
        Assert.False(w.IsHeld(item));
    }
}
