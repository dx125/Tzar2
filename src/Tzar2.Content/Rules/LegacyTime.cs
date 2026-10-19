namespace Tzar2.Content.Rules;

/// <summary>
/// The unit of the legacy <c>TIME</c> column of the unit and building tables in real time at the default game speed.
/// Measured in the original (GOG v1.10, batch A, 2026-10-18): a peasant (TIME 8) is trained every 9.99 s and one
/// builder raises a lumber mill (TIME 30) at a rate that completes it in 37.45 s, both 1.25 s per unit (UNK-20,
/// UNK-18; ADR 0169). Research times (<c>TECHNOLOGIES.INI</c>/<c>UPGRADES.INI</c> <c>time</c>) are written in
/// milliseconds and stay unscaled until a research timing is measured (UNK-21): if the factor is the original's game
/// clock rather than the column's unit, they need it too.
/// </summary>
public static class LegacyTime
{
    /// <summary>Milliseconds per <c>TIME</c> unit.</summary>
    public const int MsPerTimeUnit = 1250;
}
