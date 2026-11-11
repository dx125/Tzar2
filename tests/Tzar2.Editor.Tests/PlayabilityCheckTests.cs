using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Game.Editing;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>The editor's playability check: the acceptance's bot plays a map or a campaign (ADR 0290).</summary>
public class PlayabilityCheckTests
{
    private const int Tile = MapData.UnitsPerTile;

    // A kingdom against a computer kingdom; the rule decides when the player wins.
    private static MapDocument Map(string win)
    {
        MapDocument doc = MapDocument.CreateEmpty("Check", 32, 20, Fixtures.RulesFor(null));
        doc.Apply("Objects", s => s
            .Add(new MapObject("euro.footman", 0, (4 * Tile) + 1024, (10 * Tile) + 1024), null, out _)
            .Add(new MapObject("euro.peon", 1, (28 * Tile) + 1024, (10 * Tile) + 1024), null, out _));
        doc.Apply("Scenario", s => s.WithScenario(m => (m with { Properties = m.Properties with { Victory = VictoryCondition.NotSpecified } }) // only the rule wins
            .WithKingdom(ScenarioModel.DefaultKingdom(1) with { StartTileX = 4, StartTileY = 10 })
            .WithKingdom(ScenarioModel.DefaultKingdom(2) with { StartTileX = 28, StartTileY = 10 })
            .AddAction(new PlayerWinAction("won", PlayerMasks.Of(1), null), out int won)
            .AddRule(new RuleDef("win", ScenarioModel.Source(win), new[] { won }), out _)));
        return doc;
    }

    [Fact]
    public void A_map_the_player_wins_is_completed_by_play_alone()
    {
        var lines = new List<string>();
        PlayabilityReport report = PlayabilityCheck.Map(Map("1"), Fixtures.Classic, progress: lines.Add);
        PlayabilityRow row = Assert.Single(report.Rows);
        Assert.True(report.Completed);
        Assert.True(row.Completed && row.ByPlay);
        Assert.StartsWith("completed (Won) by play alone", row.How, StringComparison.Ordinal);
        Assert.Equal("Completable: 1 of 1 map(s), 1 by play alone", report.Summary);
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void A_map_that_cannot_be_won_reports_each_attempt_and_where_the_bot_got_to()
    {
        PlayabilityReport report = PlayabilityCheck.Map(Map("$never == 1"), Fixtures.Classic, maxMinutesPerMission: 1);
        Assert.False(report.Completed);
        Assert.Equal(3, report.Rows.Length); // the first attempt and two retries with more help
        Assert.All(report.Rows, r => Assert.False(r.Completed));
        Assert.Contains("units:", report.Rows[0].Trace, StringComparison.Ordinal);
        Assert.StartsWith("Not completed: 0 of 1 map(s)", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stopped_check_says_so()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        PlayabilityReport report = PlayabilityCheck.Map(Map("$never == 1"), Fixtures.Classic, stop.Token);
        Assert.False(report.Completed);
        Assert.Equal("cancelled", report.Note);
    }

    [Fact]
    public void A_campaign_check_plays_every_map_from_the_entry()
    {
        CampaignDocument saga = CampaignEditorTests.Saga();
        PlayabilitySubject subject = PlayabilityCheck.Prepare(saga);
        saga.UpdateMap("ford", m => m with { Description = "Edited after the check started." }); // the check plays what was built
        PlayabilityReport report = PlayabilityCheck.Run(subject, Fixtures.Classic);
        Assert.True(report.Completed, report.Summary);
        Assert.Equal(["ford", "mill", "watch"], report.Rows.Select(r => r.MapId).Distinct().ToArray());
        Assert.All(report.Rows, r => Assert.True(r.ByPlay));
    }
}
