using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Editing;

/// <summary>One row of the read-only rules browser.</summary>
/// <param name="Key">Type, technology or upgrade key.</param>
/// <param name="Kind">"unit", "building", "resource", "other", "technology" or "upgrade".</param>
/// <param name="Civilization">Civilization, or empty.</param>
/// <param name="Summary">One-line summary of the compiled values.</param>
public sealed record RulesRow(string Key, string Kind, string Civilization, string Summary);

/// <summary>
/// "Mod Studio" at its 1.0 minimum (TDD §18.3 SHOULD: "at least read-only browsing"): the compiled ruleset's entity types,
/// technologies and upgrades as rows with their main values, filtered by text. The values come from the compiled ruleset,
/// so mods that change rules show their effect.
/// </summary>
public static class RulesBrowser
{
    /// <summary>All rows of <paramref name="ruleset"/> that contain <paramref name="filter"/> (key, kind, civilization; ignoring case).</summary>
    public static ImmutableArray<RulesRow> Rows(Ruleset ruleset, string filter = "")
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(filter);
        var rows = new List<RulesRow>();
        foreach (EntityTypeDefinition t in ruleset.EntityTypes)
        {
            EntityTraits tr = t.Traits;
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"health {t.MaxHealth}");
            if (t.HasFootprint)
            {
                sb.Append(CultureInfo.InvariantCulture, $", footprint {t.FootprintWidth}×{t.FootprintHeight}");
            }

            if (t.IsMobile)
            {
                sb.Append(CultureInfo.InvariantCulture, $", {t.Locomotor.ToString().ToLowerInvariant()} speed {t.SpeedUnitsPerSecond}");
            }

            if (tr.Armament is { } a)
            {
                sb.Append(CultureInfo.InvariantCulture, $", attack {a.Attack} range {a.RangeUnits} every {a.CooldownMs} ms");
            }

            if (tr.Defence != 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $", defence {tr.Defence}");
            }

            if (!tr.Cost.IsDefaultOrEmpty)
            {
                sb.Append(", cost ").Append(string.Join("/", tr.Cost));
            }

            if (tr.BuildTimeMs > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $", build {tr.BuildTimeMs} ms");
            }

            if (tr.ResourceNode is { } n)
            {
                sb.Append(CultureInfo.InvariantCulture, $", resource {n.Resource} amount {n.Amount}");
            }

            if (tr.LegacyClass.Length > 0)
            {
                sb.Append(", class ").Append(tr.LegacyClass);
            }

            Add(rows, filter, new RulesRow(t.Key, tr.Category.ToString().ToLowerInvariant(), tr.Civilization, sb.ToString()));
        }

        foreach (TechnologyDefinition t in ruleset.Technologies)
        {
            Add(rows, filter, new RulesRow(t.Key, "technology", string.Empty, $"{t.Name}: cost {string.Join("/", t.Cost)}, {t.TimeMs} ms, {t.Effects.Length} effect(s)"));
        }

        foreach (UpgradeDefinition u in ruleset.Upgrades)
        {
            Add(rows, filter, new RulesRow(u.Key, "upgrade", string.Empty, $"{u.Name}: cost {string.Join("/", u.Cost)}, {u.TimeMs} ms"));
        }

        return [.. rows];
    }

    private static void Add(List<RulesRow> rows, string filter, RulesRow row)
    {
        if (filter.Length == 0
            || row.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || row.Civilization.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            rows.Add(row);
        }
    }
}
