using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Core.Ids;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>A whirlpool on the water is in the frame of a kingdom that sees it, so the client can draw it (PRES-020).</summary>
public class SpellAreaFrameTests
{
    [Fact]
    [Trait("Rule", "PRES-020")]
    public void A_whirlpool_is_in_the_frame_of_a_kingdom_that_sees_it_and_not_of_one_that_does_not()
    {
        Ruleset rules = SyntheticLegacyContent.CompileRuleset("classic");
        const int Tile = MapState.UnitsPerTile;
        static int At(int tile) => (tile * Tile) + (Tile / 2);
        byte[] water = new byte[32 * 12];
        for (int i = 0; i < water.Length; i++)
        {
            water[i] = (byte)(i % 32 >= 4 ? 1 : 0); // a shore of four tiles, then the sea
        }

        MapData map = Tzar2.Sim.Scenarios.ScenarioMaps.WithObjects(Tzar2.Sim.Scenarios.ScenarioMaps.Build("whirlpool", 32, 12, water, []),
            [new MapObject("euro.mage", 0, At(2), At(5)), new MapObject("comm.galleon", 1, At(28), At(5))]);
        using var sim = new Simulation(rules, map, new SimulationConfig { PlayerCount = 2, MatchSeed = 5 });
        World world = sim.World;
        ushort whirlpool = (ushort)rules.SpellId("spell_whirlpool");
        EntityId mage = world.Identities.Entities.ToArray().First(e => world.TypeOf(e).Key == "euro.mage");
        var cast = new CommandList();
        cast.Add(CommandKind.CastSpell, [mage], At(8), At(5), EntityId.None, arg0: whirlpool);
        sim.Tick(cast);
        for (int i = 0; i < 400 && world.AreaEffects.Count == 0; i++)
        {
            sim.Tick(new CommandList());
        }

        Assert.Equal(1, world.AreaEffects.Count);

        var frame = new GameFrame();
        FrameBuilder.Fill(frame, world, 0, MatchOutcome.None);
        FrameArea seen = Assert.Single(frame.Areas.ToArray());
        Assert.Equal(whirlpool, seen.Spell);
        Assert.Equal(8, seen.X / Tile);

        FrameBuilder.Fill(frame, world, 1, MatchOutcome.None);
        Assert.Empty(frame.Areas.ToArray()); // far from kingdom 1's ship, in its fog
    }
}
