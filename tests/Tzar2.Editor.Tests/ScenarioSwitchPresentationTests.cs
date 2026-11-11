using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Core.Ids;
using Tzar2.Game.Editing;
using Tzar2.Game.Scenario;
using Tzar2.Game.Session;
using Tzar2.Sim.Commands;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>What the game shows of the scenario's switches and selection (SCN-006 to SCN-008, ADR 0287).</summary>
public class ScenarioSwitchPresentationTests
{
    private const int Tile = MapData.UnitsPerTile;

    private static MapDocument Map(string rule)
    {
        MapDocument doc = MapDocument.CreateEmpty("Switches", 32, 24, Fixtures.RulesFor(null));
        doc.Apply("Objects", s => s
            .Add(new MapObject("euro.footman", 0, (4 * Tile) + 1024, (10 * Tile) + 1024), null, out _)
            .Add(new MapObject("euro.peon", 1, (26 * Tile) + 1024, (10 * Tile) + 1024), null, out _));
        doc.Apply("Scenario", s => s.WithScenario(m => m
            .WithKingdom(ScenarioModel.DefaultKingdom(1) with { StartTileX = 4, StartTileY = 10 })
            .WithKingdom(ScenarioModel.DefaultKingdom(2) with { StartTileX = 26, StartTileY = 10 })
            .AddAction(new ExpressionAction("switch", ScenarioModel.Source(rule)), out int action)
            .AddRule(new RuleDef("start", ScenarioModel.Source("1"), new[] { action }), out _)));
        return doc;
    }

    [Fact]
    [Trait("Rule", "SCN-007")]
    public void The_view_says_when_diplomacy_and_deleting_are_forbidden()
    {
        using GameSession session = Playtest.Start(Map("$canusediplomacy = 0; $candelobjects = 0"), Fixtures.Classic, new PlaytestOptions());
        Assert.True(session.AcquireFrame(out _)!.Scenario! is { CanDelete: true, CanUseDiplomacy: true });
        session.Step(30);
        ScenarioView view = session.AcquireFrame(out _)!.Scenario!;
        Assert.False(view.CanUseDiplomacy);
        Assert.False(view.CanDelete);
        Assert.False(session.Scenario!.UsesSelection);
    }

    [Fact]
    [Trait("Rule", "SCN-008")]
    public void Only_a_scenario_that_reads_the_selection_is_told_it()
    {
        using GameSession session = Playtest.Start(Map("$seen = $isselected"), Fixtures.Classic, new PlaytestOptions());
        Assert.True(session.Scenario!.UsesSelection);
        var batch = new CommandList();
        ScenarioPresenter.Select(batch, [EntityId.FromValue(5)]);
        Assert.Equal(CommandKind.ScenarioSelect, batch[0].Kind);
    }

    [Fact]
    [Trait("Rule", "SCN-009")]
    public void A_map_played_as_it_is_gives_its_computers_the_ai_its_kingdom_table_sets()
    {
        MapDocument doc = Map("$x = 1");
        doc.Apply("AI", s => s.WithScenario(m => m.WithKingdom(m.Kingdoms.Single(k => k.Number == 2) with { EconomyAi = false, WarAi = true })));
        const Sim.PlayerFlags Halves = Sim.PlayerFlags.AiHalves | Sim.PlayerFlags.AssistEconomy | Sim.PlayerFlags.AssistWar;
        using (GameSession campaign = Playtest.Start(doc, Fixtures.Classic, new PlaytestOptions()))
        {
            Assert.Equal(Sim.PlayerFlags.AiHalves | Sim.PlayerFlags.AssistWar, campaign.Simulation.World.Players[1].Flags & Halves);
            Assert.Equal(Sim.PlayerFlags.None, campaign.Simulation.World.Players[0].Flags & Halves);
        }

        // A custom map's setup chose computer opponents: they play whole.
        MatchSetup custom = Tzar2.Game.Campaigns.ScenarioMatch.Setup(Playtest.Mission(doc), Fixtures.Classic, custom: new Tzar2.Game.Campaigns.CustomMapSettings());
        using var match = new GameSession(custom, Fixtures.Classic);
        Assert.Equal(Sim.PlayerFlags.None, match.Simulation.World.Players[1].Flags & Halves);
    }
}
