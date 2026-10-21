using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Sim.Snapshots;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>The sim side of multiplayer: the drop policy's SetController (ADR 0163) and the snapshot diff (ADR 0165).</summary>
public class MultiplayerSimTests
{
    [Fact]
    public void SetController_hands_a_kingdom_to_the_computer_and_back_and_survives_a_snapshot()
    {
        using var d = new SimDriver(TestMap(48, 16, Unit("euro.peon", 0, 5, 8), Unit("euro.peon", 1, 30, 8)), ruleset: Classic);
        d.Order(1, CommandKind.SetController, [], arg0: 1);
        d.Step();
        Assert.True((d.World.Players[1].Flags & PlayerFlags.AiControlled) != 0);
        Assert.True((d.World.Players[0].Flags & PlayerFlags.AiControlled) == 0);

        // The flag is state: it is hashed and restored.
        byte[] snapshot = d.Sim.CaptureSnapshot();
        using (Simulation restored = Simulation.Restore(snapshot, d.World.Ruleset, d.World.Map.Data))
        {
            Assert.True((restored.World.Players[1].Flags & PlayerFlags.AiControlled) != 0);
            Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
        }

        d.Order(1, CommandKind.SetController, [], arg0: 0);
        d.Order(1, CommandKind.SetController, [], arg0: 2); // invalid
        d.Step();
        Assert.True((d.World.Players[1].Flags & PlayerFlags.AiControlled) == 0);
        Assert.Equal([CommandRejection.InvalidArgument], d.Rejections());
    }

    [Fact]
    [Trait("Rule", "AI-004")]
    public void SetAiAssist_sets_the_halves_the_kingdom_asks_for_and_survives_a_snapshot()
    {
        using var d = new SimDriver(TestMap(48, 16, Unit("euro.peon", 0, 5, 8), Unit("euro.peon", 1, 30, 8)), ruleset: Classic);
        const PlayerFlags Both = PlayerFlags.AssistEconomy | PlayerFlags.AssistWar;
        d.Order(0, CommandKind.SetAiAssist, [], arg0: 3);
        d.Order(1, CommandKind.SetAiAssist, [], arg0: 2);
        d.Step();
        Assert.Equal(Both, d.World.Players[0].Flags & Both);
        Assert.Equal(PlayerFlags.AssistWar, d.World.Players[1].Flags & Both);

        byte[] snapshot = d.Sim.CaptureSnapshot();
        using (Simulation restored = Simulation.Restore(snapshot, d.World.Ruleset, d.World.Map.Data))
        {
            Assert.Equal(Both, restored.World.Players[0].Flags & Both);
            Assert.Equal(d.Sim.ComputeFullHash(), restored.ComputeFullHash());
        }

        d.Order(0, CommandKind.SetAiAssist, [], arg0: 1);
        d.Order(1, CommandKind.SetAiAssist, [], arg0: 4); // invalid
        d.Step();
        Assert.Equal(PlayerFlags.AssistEconomy, d.World.Players[0].Flags & Both);
        Assert.Equal(PlayerFlags.AssistWar, d.World.Players[1].Flags & Both);
        Assert.Equal([CommandRejection.InvalidArgument], d.Rejections());
    }

    [Fact]
    public void The_snapshot_diff_names_the_entity_component_and_player_field_that_differ()
    {
        MapData map = TestMap(48, 16, Unit("euro.peon", 0, 5, 8), Unit("euro.footman", 1, 30, 8));
        using var a = new SimDriver(map, ruleset: Classic);
        using var b = new SimDriver(map, ruleset: Classic);
        a.Step();
        b.Step();
        Assert.Empty(SnapshotDiff.Compare(a.World, b.World));

        // Only b moves its footman and gets resources.
        EntityId footman = b.World.Identities.Entities.ToArray().First(e => b.World.Identities.Get(e).Owner == 1);
        b.Order(1, CommandKind.Move, [footman], x: SimDriver.TileCenter(20), y: SimDriver.TileCenter(8));
        a.Step();
        b.Step();
        for (int i = 0; i < 20; i++)
        {
            a.Step();
            b.Step();
        }

        b.World.Players[1].Resources[0] += 5;
        IReadOnlyList<SnapshotDifference> diff = SnapshotDiff.Compare(a.World, b.World);
        Assert.Contains(diff, x => x.Section == "store:core.position" && x.Entity == footman && x.Detail.Contains("euro.footman of player 1", StringComparison.Ordinal));
        Assert.Contains(diff, x => x.Section == "player" && x.Detail.StartsWith("player 1 resource ", StringComparison.Ordinal));
        Assert.DoesNotContain(diff, x => x.Section == "world");
        string text = SnapshotDiff.Format(diff);
        Assert.Contains("store:core.position entity", text, StringComparison.Ordinal);
        Assert.True(SnapshotDiff.Compare(a.World, b.World, max: 1).Count <= 2);
    }
}
