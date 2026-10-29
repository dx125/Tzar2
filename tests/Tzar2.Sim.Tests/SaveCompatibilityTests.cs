using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Snapshots;
using Xunit;
using static Tzar2.Sim.Tests.GameplayFixtures;

namespace Tzar2.Sim.Tests;

/// <summary>
/// A player's save outlives the version that wrote it (ADR 0271; the owner, 2026-10-28: a save of an older version would
/// not load). The fixture is a snapshot taken on this synthetic map by the format 21 code (commit 435495d), with that
/// version's rules.
/// </summary>
public class SaveCompatibilityTests
{
    private static MapData FixtureMap()
        => TestMap(40, 24, Structure("euro.castle", 0, 2, 2), Structure("euro.house", 0, 12, 4), Structure("euro.castle", 1, 30, 16),
            Unit("euro.peon", 0, 10, 10), Unit("euro.peon", 0, 11, 10), Unit("euro.footman", 0, 14, 12), Unit("euro.footman", 1, 26, 14));

    private static byte[] Fixture(string name)
    {
        using Stream stream = typeof(SaveCompatibilityTests).Assembly.GetManifestResourceStream("Fixtures/" + name)!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    [Fact]
    public void A_save_of_an_older_format_and_other_rules_goes_on_under_the_current_version()
    {
        byte[] old = Fixture("save-format21.t2snap");

        // A replay or a rejoin needs the very state: refused.
        Assert.Throws<InvalidDataException>(() => Simulation.Restore(old, Classic, FixtureMap()));

        // A save is upgraded: the tavern exchange's bytes are added, the store of the cell steps starts empty.
        using Simulation sim = Simulation.Restore(old, Classic, FixtureMap(), tolerance: SnapshotTolerance.SaveGame);
        Assert.False(sim.RestoredExactly);
        Assert.Equal(120, sim.World.Tick.Value);
        Assert.Equal(2, OfType(sim.World, "euro.house").Count()); // the house and the site the peasant began
        for (int t = 0; t < 200; t++)
        {
            sim.Tick(new CommandList());
        }

        // A save of this version under rules changed since (another setting) goes on; with other tables (an entity type
        // more) it is refused.
        byte[] now = WorldSnapshot.Capture(sim.World);
        Ruleset tuned = SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions { OverrideTexts = [new("tuned.yaml", "gameplay:\n  dismantleRefundPercent: 50\n")] });
        Assert.NotEqual(Classic.ContentHash, tuned.ContentHash);
        Assert.Equal(Classic.TablesHash, tuned.TablesHash);
        using (Simulation again = Simulation.Restore(now, tuned, FixtureMap(), tolerance: SnapshotTolerance.SaveGame))
        {
            Assert.False(again.RestoredExactly);
        }

        Ruleset more = SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions { OverrideTexts = [new("more.yaml", "comm.extra_marker:\n  new: true\n  Health: { max: 1 }\n")] });
        Assert.NotEqual(Classic.TablesHash, more.TablesHash);
        Assert.Throws<InvalidDataException>(() => Simulation.Restore(now, more, FixtureMap(), tolerance: SnapshotTolerance.SaveGame));

        // The same version and rules: exactly as saved.
        using Simulation same = Simulation.Restore(now, Classic, FixtureMap(), tolerance: SnapshotTolerance.SaveGame);
        Assert.True(same.RestoredExactly);
    }

    [Fact]
    [Trait("Rule", "PROD-016")]
    public void A_building_whose_type_has_become_a_producer_since_the_save_gets_its_queue()
    {
        // The eighth play-test: a church built before churches trained priests had no queue, so its priest button did
        // nothing in the save continued under the rules that give it priests.
        using var before = new SimDriver(FixtureMap(), ruleset: Classic);
        before.RunUntil(() => false, 4);
        byte[] save = WorldSnapshot.Capture(before.World);
        Ruleset trains = SyntheticLegacyContent.CompileRuleset("classic", new RulesetCompileOptions
        {
            OverrideTexts = [new("trains.yaml", "euro.house:\n  Producer: { queue: 5 }\neuro.priest:\n  Producible: { by: [euro.castle, euro.house] }\n")],
        });
        Assert.Equal(Classic.TablesHash, trains.TablesHash);
        using Simulation sim = Simulation.Restore(save, trains, FixtureMap(), tolerance: SnapshotTolerance.SaveGame);
        Core.Ids.EntityId house = OfType(sim.World, "euro.house").Single();
        Assert.True(sim.World.Productions.Has(house));
        var train = new CommandList();
        train.Add(CommandKind.Train, [house], arg0: trains.EntityTypeId("euro.priest"), arg1: 1, player: 0);
        sim.Tick(train);
        Assert.Equal(0, sim.World.Events.Events.ToArray().Count(e => e.Kind == Events.SimEventKind.CommandRejected));
    }
}
