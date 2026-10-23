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

/// <summary>A hypnotized unit keeps its kingdom's colours on everyone's screen (MAG-008, ADR 0250).</summary>
public class HypnosisFrameTests
{
    [Fact]
    [Trait("Rule", "MAG-008")]
    public void A_hypnotized_unit_wears_its_own_kingdoms_colours()
    {
        Ruleset rules = SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions { OverrideTexts = [new("test.yaml", "comm.item_magic: { Item: { spell: spell_hypnotize } }")] });
        const int Tile = MapState.UnitsPerTile;
        static int At(int tile) => (tile * Tile) + (Tile / 2);
        MapData map = Tzar2.Sim.Scenarios.ScenarioMaps.WithObjects(Tzar2.Sim.Scenarios.ScenarioMaps.Build("hypnosis", 24, 12, new byte[24 * 12], []),
            [new MapObject("euro.footman", 0, At(4), At(4)), new MapObject("comm.item_magic", MapObject.Nature, At(5), At(4)),
             new MapObject("euro.archer", 1, At(9), At(4)), new MapObject("euro.peon", 1, At(12), At(4))]); // kingdom 1 keeps eyes on it
        using var sim = new Simulation(rules, map, new SimulationConfig { PlayerCount = 2, MatchSeed = 5 });
        World world = sim.World;
        EntityId reader = world.Identities.Entities.ToArray().First(e => world.TypeOf(e).Key == "euro.footman");
        EntityId book = world.Identities.Entities.ToArray().First(e => world.TypeOf(e).Key == "comm.item_magic");
        EntityId archer = world.Identities.Entities.ToArray().First(e => world.TypeOf(e).Key == "euro.archer");
        var orders = new CommandList();
        orders.Add(CommandKind.SetStance, [reader], arg0: (int)CombatStance.HoldFire);
        orders.Add(CommandKind.SetStance, [archer], arg0: (int)CombatStance.HoldFire, player: 1);
        orders.Add(CommandKind.PickItem, [reader], target: book);
        sim.Tick(orders);
        for (int i = 0; i < 400 && !world.IsHeld(book); i++)
        {
            sim.Tick(new CommandList());
        }

        var read = new CommandList();
        read.Add(CommandKind.UseItem, [reader], At(9), At(4), archer);
        sim.Tick(read);
        for (int i = 0; i < 400 && world.Identities.Get(archer).Owner != 0; i++)
        {
            sim.Tick(new CommandList());
        }

        Assert.Equal(0, world.Identities.Get(archer).Owner);
        foreach (byte viewer in new byte[] { 0, 1 })
        {
            var frame = new GameFrame();
            FrameBuilder.Fill(frame, world, viewer, MatchOutcome.None);
            FrameEntity seen = frame.Entities.ToArray().Single(e => e.Id == archer);
            Assert.Equal(((byte)0, 1), (seen.Owner, seen.SeemsOwner));
        }
    }
}
