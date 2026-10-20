using Tzar2.Client.Logic;
using Xunit;

namespace Tzar2.Client.Tests;

/// <summary>The original's pictures of projectiles, explosions and fire (owner play-test 2026-10-20).</summary>
public class EffectImagesTests
{
    [Theory]
    [InlineData("arrow", "arrow", true, null)]
    [InlineData("bolt", "arrow_ballista", true, null)]
    [InlineData("stone", "cb_stone", false, "explosion_small")]
    [InlineData("cannon", "cb_metal", false, "explosion_small")]
    [InlineData("rocket_arrow", "arrow_rocket", true, "explosion_small")]
    [InlineData("freeze_bolt", "freeze_bolt", false, "freeze_bolt_expl")]
    [InlineData("flame_ball", "flame_bolt", false, "flame_bolt_expl")]
    public void Every_projectile_kind_has_a_picture(string kind, string image, bool turns, string? impact)
    {
        Assert.Equal(image, EffectImages.Projectile(kind));
        Assert.Equal(turns, EffectImages.Turns(kind));
        Assert.Equal(impact, EffectImages.Impact(kind));
        Assert.Contains(image, EffectImages.All);
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
    public void Arcs_rise_to_the_middle_and_land()
    {
        Assert.Equal(0, EffectImages.ArcHeight("stone", 300, 0));
        Assert.Equal(0, EffectImages.ArcHeight("stone", 300, 1));
        Assert.Equal(90, EffectImages.ArcHeight("stone", 300, 0.5));
        Assert.True(EffectImages.ArcHeight("arrow", 300, 0.5) < EffectImages.ArcHeight("stone", 300, 0.5));
        Assert.Equal(0, EffectImages.ArcHeight("magic", 300, 0.5));
        Assert.Equal(96, EffectImages.ArcHeight("stone", 2000, 0.5)); // capped
    }
}
