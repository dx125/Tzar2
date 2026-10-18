using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Systems;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>The sim's scenario hooks (ADR 0130): locks, player status, forced outcomes, replies and the map-object table.</summary>
public class ScenarioHookTests
{
    private sealed class FakeController : IScenarioController
    {
        public HashSet<uint> Locked { get; } = [];

        public bool NoWin { get; set; }

        public bool NoLose { get; set; }

        public List<(byte Player, int Arg0, int Arg1)> Replies { get; } = [];

        public bool IsLocked(EntityId entity) => Locked.Contains(entity.Value);

        public bool CannotWin(int player) => NoWin && player == 0;

        public bool CannotLose(int player) => NoLose && player == 1;

        public CommandRejection? Reply(World world, in Command command)
        {
            if (command.Arg0 != 7)
            {
                return CommandRejection.NoScenarioPrompt;
            }

            Replies.Add((command.Player, command.Arg0, command.Arg1));
            return null;
        }
    }

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

    [Fact]
    public void Map_objects_are_listed_by_index_at_match_start()
    {
        using var d = new SimDriver(TestMap(32, 16, Unit("euro.peon", 0, 3, 3), Structure("euro.house", 1, 10, 4), Tree(20, 8)), ruleset: Classic);
        World w = d.World;
        Assert.Equal(3, w.MapObjectEntities.Length);
        Assert.Equal("euro.peon", w.TypeOf(w.MapObjectEntities[0]).Key);
        Assert.Equal("euro.house", w.TypeOf(w.MapObjectEntities[1]).Key);
        Assert.Equal("comm.tree", w.TypeOf(w.MapObjectEntities[2]).Key);
    }

    [Fact]
    public void Locked_units_ignore_the_players_commands_but_not_others()
    {
        using var d = new SimDriver(TestMap(32, 16, Unit("euro.peon", 0, 3, 3), Unit("euro.peon", 0, 3, 6)), ruleset: Classic);
        World w = d.World;
        var controller = new FakeController();
        w.Scenario = controller;
        EntityId locked = w.MapObjectEntities[0];
        EntityId free = w.MapObjectEntities[1];
        controller.Locked.Add(locked.Value);
        d.Order(0, CommandKind.Move, [locked, free], SimDriver.TileCenter(20), SimDriver.TileCenter(3));
        d.Step();
        Assert.True(d.IsMoving(free));
        Assert.False(d.IsMoving(locked));

        d.Order(0, CommandKind.Move, [locked], SimDriver.TileCenter(20), SimDriver.TileCenter(3));
        d.Step();
        Assert.Contains(CommandRejection.NoValidEntities, d.Rejections());

        // Deletion is a command too.
        d.Order(0, CommandKind.Delete, [locked]);
        d.Step();
        Assert.True(w.Entities.IsAlive(locked));
    }

    [Fact]
    public void Cannot_lose_keeps_an_empty_kingdom_in_the_game_and_cannot_win_blocks_the_automatic_victory()
    {
        MapData map = TestMap(48, 16, Unit("euro.footman", 0, 10, 8), Unit("euro.peon", 1, 12, 8));
        using (var d = new SimDriver(map, ruleset: Classic, configure: c => c with { Victory = VictoryCondition.Standard }))
        {
            d.World.Scenario = new FakeController { NoLose = true };
            d.Order(0, CommandKind.DebugKill, [d.World.MapObjectEntities[1]]);
            d.RunUntil(() => false, 200);
            Assert.False(VictorySystem.IsOver(d.World));
            Assert.Equal(PlayerFlags.Active, d.World.Players[1].Flags);
        }

        using (var d = new SimDriver(map, ruleset: Classic, configure: c => c with { Victory = VictoryCondition.Standard }))
        {
            d.World.Scenario = new FakeController { NoWin = true };
            d.Order(0, CommandKind.DebugKill, [d.World.MapObjectEntities[1]]);
            d.RunUntil(() => false, 200);
            Assert.True((d.World.Players[1].Flags & PlayerFlags.Defeated) != 0);
            Assert.False(VictorySystem.IsOver(d.World));
        }
    }

    [Fact]
    public void Forced_outcomes_and_scripted_diplomacy_run_inside_a_tick()
    {
        using var d = new SimDriver(TestMap(48, 16, Unit("euro.peon", 0, 5, 5), Unit("euro.peon", 1, 20, 5), Unit("euro.peon", 2, 40, 5)), players: 3, ruleset: Classic);
        World w = d.World;
        var probe = new Probe(_ => { });
        d.Sim.Systems.Add(probe);
        d.Step();
        Assert.Throws<System.InvalidOperationException>(() => ScenarioOperations.ForceVictory(w, 1)); // between ticks
        probe.Action = world =>
        {
            ScenarioOperations.SetDiplomacy(world, 0, 1, Stance.Ally, Sharing.Vision);
            ScenarioOperations.ForceDefeat(world, 1u << 2);
        };
        d.Step();
        Assert.Equal(Stance.Ally, w.Diplomacy.StanceOf(0, 1));
        Assert.Equal(Sharing.Vision, w.Diplomacy.OffersOf(0, 1));
        Assert.True((w.Players[2].Flags & PlayerFlags.Defeated) != 0);
        Assert.False(VictorySystem.IsOver(w));

        probe.Action = world => ScenarioOperations.ForceVictory(world, 1u << 1);
        d.Step();
        Assert.True((w.Players[1].Flags & PlayerFlags.Victorious) != 0);
        Assert.True((w.Players[0].Flags & PlayerFlags.Defeated) != 0);
        Assert.Equal(1, d.Count(SimEventKind.PlayerVictorious));
    }

    [Fact]
    public void Scenario_replies_reach_the_controller_or_are_refused()
    {
        using var d = new SimDriver(TestMap(16, 16, Unit("euro.peon", 0, 3, 3)), ruleset: Classic);
        d.Order(0, CommandKind.ScenarioReply, [], arg0: 7, arg1: 2);
        d.Step();
        Assert.Contains(CommandRejection.NoScenarioPrompt, d.Rejections()); // no controller

        var controller = new FakeController();
        d.World.Scenario = controller;
        d.Order(1, CommandKind.ScenarioReply, [], arg0: 7, arg1: 1);
        d.Order(0, CommandKind.ScenarioReply, [], arg0: 3, arg1: 1);
        d.Step();
        Assert.Equal([(1, 7, 1)], controller.Replies.Select(r => ((int)r.Player, r.Arg0, r.Arg1)));
        Assert.Equal(2, d.Rejections().Count(r => r == CommandRejection.NoScenarioPrompt));
    }
}
