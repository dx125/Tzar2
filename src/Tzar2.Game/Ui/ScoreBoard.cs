using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Tzar2.Game.Ui;

/// <summary>What F4 shows over the map (DIP-008): it steps through these and back to none.</summary>
public enum ScoreView
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>The scoreboard and the clock.</summary>
    ScoresAndClock,

    /// <summary>The clock only.</summary>
    Clock,
}

/// <summary>A kingdom's line of the scoreboard.</summary>
/// <param name="Kingdom">The kingdom (slot).</param>
/// <param name="Own">Its score.</param>
/// <param name="Team">Its team's score: the mean of the team's members (its own without a team).</param>
/// <param name="Defeated">Whether it is defeated (the line is struck through).</param>
public sealed record ScoreRow(int Kingdom, long Own, long Team, bool Defeated);

/// <summary>A kingdom as the scoreboard sees it.</summary>
/// <param name="Kingdom">The kingdom (slot).</param>
/// <param name="Score">Its score.</param>
/// <param name="Team">Its team (0 none).</param>
/// <param name="Defeated">Whether it is defeated.</param>
public readonly record struct ScoreKingdom(int Kingdom, long Score, int Team, bool Defeated);

/// <summary>
/// F4's scoreboard and clock (DIP-008), as ReTzar reads the original (0x4f2903, 0x4f5bce): F4 steps from nothing to the
/// scoreboard and the clock, to the clock alone, and back. The clock (h:mm:ss of game time) is at the map view's top
/// right; the scoreboard above its bottom right corner, one line per kingdom, "name  own/team", the best first.
/// </summary>
public static class ScoreBoard
{
    /// <summary>What F4 shows after <paramref name="view"/>.</summary>
    public static ScoreView Next(ScoreView view) => view switch
    {
        ScoreView.None => ScoreView.ScoresAndClock,
        ScoreView.ScoresAndClock => ScoreView.Clock,
        _ => ScoreView.None,
    };

    /// <summary>The clock of <paramref name="milliseconds"/> of game time: <c>h:mm:ss</c>.</summary>
    public static string Clock(long milliseconds)
    {
        long s = Math.Max(0, milliseconds) / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}");
    }

    /// <summary>The lines of <paramref name="kingdoms"/>: by score, the best first (then by kingdom).</summary>
    public static IReadOnlyList<ScoreRow> Rows(IReadOnlyList<ScoreKingdom> kingdoms)
    {
        ArgumentNullException.ThrowIfNull(kingdoms);
        var rows = new List<ScoreRow>(kingdoms.Count);
        foreach (ScoreKingdom k in kingdoms)
        {
            ScoreKingdom[] members = k.Team == 0 ? [k] : [.. kingdoms.Where(o => o.Team == k.Team)];
            long team = members.Sum(o => o.Score) / members.Length;
            rows.Add(new ScoreRow(k.Kingdom, k.Score, team, k.Defeated));
        }

        return [.. rows.OrderByDescending(r => r.Own).ThenBy(r => r.Kingdom)];
    }

    /// <summary>The text of a line: <c>name  own/team</c>.</summary>
    public static string Line(string name, ScoreRow row)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(row);
        return string.Create(CultureInfo.InvariantCulture, $"{name}  {row.Own}/{row.Team}");
    }
}
