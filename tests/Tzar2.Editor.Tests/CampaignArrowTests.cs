using System;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>The campaign graph's arrows make and change transitions (ADR 0292).</summary>
public class CampaignArrowTests
{
    private const int Tile = MapData.UnitsPerTile;

    private static MapDocument Map(string name)
    {
        MapDocument doc = MapDocument.CreateEmpty(name, 24, 24, Fixtures.RulesFor(null));
        doc.Apply("Objects", s => s
            .Add(new MapObject("euro.footman", 0, (4 * Tile) + 1024, (4 * Tile) + 1024), null, out _)
            .Add(new MapObject("euro.peon", 1, (18 * Tile) + 1024, (18 * Tile) + 1024), null, out _));
        return doc;
    }

    private static CampaignDocument Campaign()
    {
        CampaignDocument c = CampaignDocument.New("Saga");
        c.AddMap(Map("One"), "One", entry: true);
        c.AddMap(Map("Two"), "Two");
        c.AddMap(Map("Three"), "Three");
        return c;
    }

    [Fact]
    public void A_victory_arrow_gives_a_map_without_a_win_the_destroy_the_enemies_goal_leading_on()
    {
        CampaignDocument c = Campaign();
        string done = CampaignTransitions.Link(c, "one", "two", CampaignEdgeKind.Win);
        Assert.Contains("destroying the enemies", done, StringComparison.Ordinal);
        CampaignEdge edge = Assert.Single(c.Build().Edges.Items);
        Assert.Equal(("one", "two", CampaignEdgeKind.Win), (edge.FromMapId, edge.ToMapId, edge.Kind));
        Assert.True(c.Graph().Nodes.Single(n => n.Id == "two").Reachable);
        Assert.Contains(c.Find("one")!.Document.State.Scenario.Rules, r => r.Name == "Goal 1 done");

        // The map's own history undoes it.
        c.Find("one")!.Document.Undo();
        Assert.Empty(c.Build().Edges.Items);
    }

    [Fact]
    public void An_arrow_first_sends_the_maps_own_wins_and_losses_that_end_it()
    {
        CampaignDocument c = Campaign();
        MapDocument one = c.Find("one")!.Document;
        one.Apply("Own ending", s => s.WithScenario(m => m
            .AddAction(new PlayerWinAction("Won", PlayerMasks.Of(1), null), out _)
            .AddAction(new PlayerLoseAction("Lost", PlayerMasks.Of(1), null), out _)));
        int rules = one.State.Scenario.Rules.Length;

        CampaignTransitions.Link(c, "one", "three", CampaignEdgeKind.Win);
        CampaignTransitions.Link(c, "one", "two", CampaignEdgeKind.Lose);
        Assert.Equal(rules, one.State.Scenario.Rules.Length); // nothing added: the endings were retargeted
        Assert.Equal("Three", ((PlayerWinAction)one.State.Scenario.Actions[0]).Map);
        Assert.Equal("Two", ((PlayerLoseAction)one.State.Scenario.Actions[1]).Map);
        Assert.Equal(2, c.Build().Edges.Length);
    }

    [Fact]
    public void A_defeat_arrow_on_a_map_without_a_loss_loses_into_the_target_when_the_player_is_defeated()
    {
        CampaignDocument c = Campaign();
        CampaignTransitions.Link(c, "two", "three", CampaignEdgeKind.Lose);
        ScenarioModel m = c.Find("two")!.Document.State.Scenario;
        PlayerDefeatedEvent defeated = Assert.IsType<PlayerDefeatedEvent>(Assert.Single(m.Events.Items));
        Assert.Equal(PlayerMasks.Of(1), defeated.Players);
        PlayerLoseAction lose = Assert.IsType<PlayerLoseAction>(Assert.Single(m.Actions.Items));
        Assert.Equal("Three", lose.Map);
        Assert.Equal(defeated.Name, Assert.Single(m.Rules.Items).Condition.Source);
        Assert.Equal(CampaignEdgeKind.Lose, Assert.Single(c.Build().Edges.Items).Kind);
        Assert.Throws<ArgumentException>(() => CampaignTransitions.Link(c, "two", "three", CampaignEdgeKind.LoadMap));
        Assert.Throws<ArgumentException>(() => CampaignTransitions.Link(c, "two", "nowhere", CampaignEdgeKind.Win));
    }

    [Fact]
    public void A_transition_is_retargeted_or_cut_from_the_maps_page()
    {
        CampaignDocument c = Campaign();
        CampaignTransitions.Link(c, "one", "two", CampaignEdgeKind.Win);
        CampaignEdge edge = Assert.Single(c.Build().Edges.Items);
        CampaignTransitions.Retarget(c, "one", edge.ActionIndex, "Three");
        Assert.Equal("three", Assert.Single(c.Build().Edges.Items).ToMapId);
        CampaignTransitions.Retarget(c, "one", edge.ActionIndex, null);
        Assert.Empty(c.Build().Edges.Items);

        c.Find("two")!.Document.Apply("Load", s => s.WithScenario(m => m.AddAction(new LoadMapAction("Go", "Three"), out _)));
        Assert.Throws<ArgumentException>(() => CampaignTransitions.Retarget(c, "two", 0, null));
        CampaignTransitions.Retarget(c, "two", 0, "One");
        Assert.Equal("one", Assert.Single(c.Build().Edges.Items).ToMapId);
    }
}
