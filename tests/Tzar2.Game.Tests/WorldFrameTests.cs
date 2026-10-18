using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Core.Ids;
using Tzar2.Game.Input;
using Tzar2.Game.Session;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Snapshots;
using Tzar2.Sim.Systems;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>
/// The presentation reads the sim's world systems (ADRs 0101, 0105-0107): fog and last-seen structures in frames and in
/// the render snapshot, the day clock, scores, and the <c>hm*</c> chat codes.
/// </summary>
public sealed class WorldFrameTests
{
    private static readonly Lazy<Ruleset> Rules = new(() => SyntheticLegacyContent.CompileRuleset());

    [Fact]
    public void Frames_show_the_sims_fog_only_visible_units_and_remembered_structures()
    {
        using var session = new GameSession(SessionTests.Setup() with { CheatsAllowed = true }, Rules.Value, _ => new SessionTests.IdleController());
        session.Step(2);
        GameFrame frame = session.AcquireFrame(out _)!;
        World world = session.Simulation.World;
        var expected = new byte[world.Map.Width * world.Map.Height];
        world.Visibility.WriteStates(0, expected);
        Assert.Equal(expected, frame.Visibility);
        Assert.DoesNotContain(frame.Entities.ToArray(), e => e.Owner == 1 && Rules.Value.EntityTypes[e.TypeId].IsMobile);
        Assert.All(frame.Entities.ToArray(), e => Assert.True(e.Owner == 0 || e.Remembered || world.Visibility.CanSee(world, 0, e.Id)));
        Assert.Equal(world.DayMinute, frame.DayMinute);
        Assert.Equal(VictorySystem.Score(world.Ruleset.World, world.Kingdoms[0]), frame.Players[0].Score);

        // hmreveal through the chat line: the whole map is explored and the enemy castle is remembered.
        Assert.True(session.SubmitChat("hmreveal"));
        session.Step(1);
        frame = session.AcquireFrame(out _)!;
        Assert.Contains(frame.Entities.ToArray(), e => e.Owner == 1 && e.Remembered && Rules.Value.EntityTypes[e.TypeId].Traits.LegacyClass == "castle");
        Assert.DoesNotContain(frame.Visibility, v => v == (byte)TileVisibility.Unexplored);
    }

    [Fact]
    public void The_render_snapshot_carries_the_viewers_fog_and_the_clock()
    {
        Ruleset rules = Rules.Value;
        MatchSetup setup = SessionTests.Setup();
        MapData map = setup.Map.Create(rules, setup);
        using var sim = new Simulation(rules, map, new SimulationConfig { PlayerCount = 2, MatchSeed = 3 });
        sim.RenderOutput = new RenderSnapshotExchange { Viewer = 1 };
        sim.Tick(new CommandList());
        RenderSnapshot snapshot = sim.RenderOutput.AcquireLatest()!;
        Assert.Equal(1, snapshot.Viewer);
        Assert.Equal(map.Width * map.Height, snapshot.Visibility.Length);
        Assert.Contains(snapshot.Visibility.ToArray(), v => v == (byte)TileVisibility.Visible);
        Assert.All(snapshot.Entities.ToArray(), e => Assert.True(sim.World.Visibility.CanSee(sim.World, 1, e.Id)));
        Assert.Equal(sim.World.DayMinute, snapshot.DayMinute);

        sim.RenderOutput.Viewer = -1;
        sim.Tick(new CommandList());
        snapshot = sim.RenderOutput.AcquireLatest()!;
        Assert.Equal(-1, snapshot.Viewer);
        Assert.True(snapshot.Visibility.IsEmpty);
        Assert.Equal(sim.World.Identities.Count, snapshot.Entities.Length);
    }

    [Theory]
    [InlineData("hmreveal", CheatCode.Reveal)]
    [InlineData("HMNOFOG", CheatCode.NoFog)]
    [InlineData(" hmnotech ", CheatCode.NoTech)]
    [InlineData("hmgod", CheatCode.God)]
    [InlineData("hmbuildozer", CheatCode.Buildozer)]
    [InlineData("hmdvaleva", CheatCode.SmallResources)]
    [InlineData("hmpetleva", CheatCode.LargeResources)]
    [InlineData("hmnext", CheatCode.Next)]
    public void Chat_codes_become_cheat_commands(string text, CheatCode code)
    {
        var list = new CommandList();
        Assert.True(CheatCodes.TryParse(text, Rules.Value, list));
        Assert.Equal(CommandKind.Cheat, list[0].Kind);
        Assert.Equal((int)code, list[0].Arg0);
    }

    [Fact]
    public void Hmspawn_names_a_unit_and_unknown_names_are_ignored()
    {
        var list = new CommandList();
        Assert.True(CheatCodes.TryParse("hmspawn footman 3", Rules.Value, list, 4096, 6144));
        Assert.Equal(CommandKind.DebugSpawn, list[0].Kind);
        Assert.Equal(Rules.Value.EntityTypeId("euro.footman"), list[0].Arg0);
        Assert.Equal((3, 4096, 6144), (list[0].Arg1, list[0].X, list[0].Y));
        Assert.True(CheatCodes.TryParse("hmspawn euro.knight", Rules.Value, list));
        Assert.False(CheatCodes.TryParse("hmspawn magic", Rules.Value, list)); // the 1.10 crash: now ignored
        Assert.False(CheatCodes.TryParse("hmspawn euro.house", Rules.Value, list)); // not a unit
        Assert.False(CheatCodes.TryParse("hello", Rules.Value, list));
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void Cheats_typed_in_a_match_without_the_flag_are_refused_by_the_sim()
    {
        using var session = new GameSession(SessionTests.Setup(), Rules.Value, _ => new SessionTests.IdleController());
        int gold = Rules.Value.ResourceId("gold");
        int before = session.Simulation.World.Players[0].Resources[gold];
        Assert.True(session.SubmitChat("hmpetleva"));
        session.Step(1);
        Assert.Equal(before, session.Simulation.World.Players[0].Resources[gold]);

        using var sandbox = new GameSession(SessionTests.Setup() with { CheatsAllowed = true }, Rules.Value, _ => new SessionTests.IdleController());
        Assert.True(sandbox.SubmitChat("hmpetleva"));
        sandbox.Step(1);
        Assert.Equal(before + Rules.Value.World.CheatLargeResources, sandbox.Simulation.World.Players[0].Resources[gold]);
    }
}
