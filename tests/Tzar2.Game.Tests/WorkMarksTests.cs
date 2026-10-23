using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Core.Ids;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>Marks of what the player's workers work on.</summary>
public class WorkMarksTests
{
    private static readonly Ruleset Rules = SyntheticLegacyContent.CompileRuleset();

    [Fact]
    [Trait("Rule", "HUD-025")]
    public void The_fish_patches_the_players_boats_fish_are_marked()
    {
        ushort boat = (ushort)Rules.EntityTypeId("comm.fishing_boat");
        ushort patch = (ushort)Rules.EntityTypeId("comm.fishpatch");
        ushort peon = (ushort)Rules.EntityTypeId("euro.peon");
        var frame = new GameFrame();
        FrameEntity[] buffer = frame.EntityBuffer(6);
        buffer[0] = new FrameEntity { Id = EntityId.FromValue(1), TypeId = boat, Owner = 0, WorkTarget = EntityId.FromValue(4) };
        buffer[1] = new FrameEntity { Id = EntityId.FromValue(2), TypeId = boat, Owner = 1, WorkTarget = EntityId.FromValue(5) }; // another kingdom's
        buffer[2] = new FrameEntity { Id = EntityId.FromValue(3), TypeId = peon, Owner = 0, WorkTarget = EntityId.FromValue(6) }; // a peasant, not a boat
        buffer[3] = new FrameEntity { Id = EntityId.FromValue(4), TypeId = patch, Owner = 0xFF };
        buffer[4] = new FrameEntity { Id = EntityId.FromValue(5), TypeId = patch, Owner = 0xFF };
        buffer[5] = new FrameEntity { Id = EntityId.FromValue(6), TypeId = patch, Owner = 0xFF };
        frame.EntityCount = 6;
        Assert.Equal([4u], WorkMarks.FishedPatches(frame, Rules, 0));
        Assert.Equal([5u], WorkMarks.FishedPatches(frame, Rules, 1));
    }
}
