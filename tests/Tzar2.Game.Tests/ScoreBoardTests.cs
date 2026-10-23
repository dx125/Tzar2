using System.Linq;
using Tzar2.Game.Ui;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>F4's scoreboard and clock.</summary>
public class ScoreBoardTests
{
    [Fact]
    [Trait("Rule", "DIP-008")]
    public void F4_steps_from_nothing_to_the_scores_and_clock_to_the_clock_and_back()
    {
        Assert.Equal(ScoreView.ScoresAndClock, ScoreBoard.Next(ScoreView.None));
        Assert.Equal(ScoreView.Clock, ScoreBoard.Next(ScoreView.ScoresAndClock));
        Assert.Equal(ScoreView.None, ScoreBoard.Next(ScoreView.Clock));
        Assert.Equal("0:00:00", ScoreBoard.Clock(999));
        Assert.Equal("1:02:03", ScoreBoard.Clock(((3600 + 120 + 3) * 1000) + 500));
    }

    [Fact]
    [Trait("Rule", "DIP-008")]
    public void Each_kingdom_shows_its_score_and_its_teams_the_best_first()
    {
        var rows = ScoreBoard.Rows([new(0, 300, 1, false), new(1, 500, 0, false), new(2, 100, 1, true), new(3, 500, 2, false)]);
        Assert.Equal([1, 3, 0, 2], rows.Select(r => r.Kingdom));
        Assert.Equal((300L, 200L), (rows[2].Own, rows[2].Team)); // team 1: (300 + 100) / 2
        Assert.Equal((500L, 500L), (rows[0].Own, rows[0].Team)); // no team: its own
        Assert.True(rows[3].Defeated);
        Assert.Equal("Blue  300/200", ScoreBoard.Line("Blue", rows[2]));
    }
}
