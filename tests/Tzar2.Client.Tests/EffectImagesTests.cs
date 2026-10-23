using Tzar2.Client.Logic;
using Xunit;

namespace Tzar2.Client.Tests;

/// <summary>The original's pictures of shots, explosions and fire (owner play-test 2026-10-20; PRES-002, ReTzar's reading).</summary>
[Trait("Rule", "PRES-002")]
public class EffectImagesTests
{
    [Theory]
    [InlineData("arrow", "archer", "arrow", true, null)]
    [InlineData("arrow", "longbowman", "arrow_big", true, null)]
    [InlineData("arrow", "xebec", "arrow_big", true, null)]
    [InlineData("arrow", "tower_stone", "arrow_big", true, null)]
    [InlineData("arrow", "tower_wood", "arrow", true, null)]
    [InlineData("magic", "mage", "flame_bolt", false, "flame_bolt_expl")]
    [InlineData("magic", "priest", "exorcism", false, "flame_bolt_expl")]
    [InlineData("stone", "catapult", "cb_stone", false, EffectImages.Hole)]
    [InlineData("cannon", "galleon", "cb_metal", false, EffectImages.Hole)]
    [InlineData("cannon", "tower_stone2", "cb_fire", false, EffectImages.Hole)]
    [InlineData("rocket_arrow", "tower_rocket", "arrow_rocket", true, EffectImages.Hole)]
    [InlineData("bolt", "ballista", "arrow_ballista", true, EffectImages.Hole)]
    [InlineData("freeze_bolt", "mage", "freeze_ball_trail", false, "freeze_bolt_expl")]
    [InlineData("flame_ball", "dragon", "flame_ball_trail", false, "flame_bolt_expl")]
    public void Each_shot_looks_like_the_originals_for_its_kind_and_shooter(string kind, string shooter, string body, bool turns, string? impact)
    {
        ShotLook look = EffectImages.Look(kind, shooter)!;
        Assert.Equal((body, turns, impact), (look.Body, look.Turns, look.Impact));
        Assert.Contains(body, EffectImages.All);
        if (look.Shadow is { } shadow)
        {
            Assert.Contains(shadow, EffectImages.All);
        }
    }

    [Fact]
    public void Shells_arc_as_the_original_draws_them_and_ships_flash()
    {
        ShotLook stone = EffectImages.Look("stone", "catapult")!;
        Assert.Equal((192, 48, "cb_metal_shadow"), (stone.Peak, stone.Start, stone.Shadow));
        Assert.Equal((64, 24), (EffectImages.Look("cannon", "hulk")!.Peak, EffectImages.Look("cannon", "hulk")!.Start));
        Assert.Equal((64, 16), (EffectImages.Look("bolt", "ballista")!.Peak, EffectImages.Look("bolt", "ballista")!.Start));
        Assert.True(EffectImages.Look("cannon", "galleon")!.Muzzle);
        Assert.False(EffectImages.Look("cannon", "tower_stone2")!.Muzzle);
        Assert.Null(EffectImages.Look("unknown", null));
    }

    [Fact]
    public void Shells_leave_a_hole_a_splash_or_a_blast()
    {
        ShotLook shell = EffectImages.Look("cannon", "galleon")!, bolt = EffectImages.Look("bolt", "ballista")!;
        Assert.Equal(EffectImages.Hole, shell.ImpactOn(water: false, building: false));
        Assert.Equal(EffectImages.Splash, shell.ImpactOn(water: true, building: false));
        Assert.Equal(EffectImages.Blast, shell.ImpactOn(water: false, building: true));
        Assert.Equal(EffectImages.Hole, bolt.ImpactOn(water: false, building: true)); // bolts never blast
        Assert.Equal("freeze_bolt_expl", EffectImages.Look("freeze_bolt", "mage")!.ImpactOn(water: true, building: false));
    }

    [Theory]
    [InlineData(0, -1, 0)]     // north
    [InlineData(1, 0, 6)]      // east
    [InlineData(0, 1, 12)]     // south
    [InlineData(-1, 0, 18)]    // west
    [InlineData(1, -1, 3)]     // north-east
    [InlineData(-1, -1, 21)]   // north-west
    public void Arrows_turn_with_their_flight_clockwise_from_north(double dx, double dy, int frame)
        => Assert.Equal(frame, EffectImages.TurnFrame(dx, dy, 24));

    [Fact]
    public void The_arc_starts_at_its_height_rises_and_lands_at_zero()
    {
        // h = (a·s + b)·s + h0 over the Chebyshev distance (0x4335e7).
        Assert.Equal(48, EffectImages.ArcHeight(192, 48, 300, 0));
        Assert.Equal(0, EffectImages.ArcHeight(192, 48, 300, 300));
        int middle = EffectImages.ArcHeight(192, 48, 300, 150);
        Assert.Equal(96, middle); // a = (2·48 − 2·192) / 300², b = (2·192 − 3·48) / 300; the top, 98, comes at 125
        Assert.Equal(0, EffectImages.ArcHeight(0, 0, 300, 150));
        Assert.Equal(0, EffectImages.ArcHeight(192, 48, 0, 0));
    }
}
