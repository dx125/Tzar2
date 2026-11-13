using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Content.Editing;

/// <summary>One kingdom's start on a multiplayer map (ADR 0292).</summary>
/// <param name="Kingdom">The kingdom, 1-8.</param>
/// <param name="TileX">Its starting tile.</param>
/// <param name="TileY">Its starting tile.</param>
/// <param name="Gold">Tiles walked to the nearest gold, or null when no gold is reached by land.</param>
/// <param name="Stone">Tiles walked to the nearest stone, or null.</param>
/// <param name="Wood">Tiles walked to the nearest wood, or null.</param>
/// <param name="Reaches">The other kingdoms whose start it reaches by land.</param>
public sealed record FairStart(int Kingdom, int TileX, int TileY, int? Gold, int? Stone, int? Wood, ImmutableArray<int> Reaches);

/// <summary>The fair-starts check of a map (ADR 0292).</summary>
/// <param name="Starts">The kingdoms with a starting location, by number.</param>
/// <param name="Findings">What looks unfair, in words; empty when nothing does.</param>
public sealed record FairnessReport(ImmutableArray<FairStart> Starts, ImmutableArray<string> Findings);

/// <summary>
/// Checks that a multiplayer map gives every kingdom a like start (ADR 0292): how far each start walks by land to the
/// nearest gold, stone and wood, and which other starts it reaches. A distance more than half again the nearest
/// kingdom's (and at least <see cref="MinGap"/> tiles longer) is a finding, and so are a resource a start never reaches
/// and kingdoms that cannot walk to one another (fine on an island map, worth knowing on any other).
/// </summary>
/// <remarks>
/// Walking is on tiles that are not all water, not cliffs and not under a structure (walls included; gates and bridges
/// let units through), one step to each of the eight neighbours. A start under its own building walks from that
/// building's tiles. A resource is reached from a tile next to it.
/// </remarks>
public static class FairStarts
{
    /// <summary>Fewest extra tiles before a longer walk is a finding.</summary>
    public const int MinGap = 5;

    /// <summary>Checks <paramref name="document"/> under <paramref name="ruleset"/>.</summary>
    public static FairnessReport Check(MapDocument document, Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ruleset);
        int w = document.Width, h = document.Height;
        var occupancy = new Occupancy(document, ruleset);
        int gold = ruleset.ResourceId("gold"), stone = ruleset.ResourceId("stone"), wood = ruleset.ResourceId("wood");
        var resource = new int[w * h];
        var walkable = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x;
                resource[i] = -1;
                bool open = !document.IsWaterTile(x, y) && !document.IsCliff(x, y);
                if (occupancy.StructureAt(x, y) is > 0 and var id && document.State.Find(id) is { } o && ruleset.EntityTypeId(o.TypeKey) is >= 0 and var t)
                {
                    EntityTraits traits = ruleset.EntityTypes[t].Traits;
                    open = open && (traits.Gate is not null || traits.WalkableFootprint);
                    open = open || traits.IsBridge;
                    if (traits.ResourceNode is { } node)
                    {
                        resource[i] = node.Resource;
                    }
                }

                walkable[i] = open;
            }
        }

        var starts = new List<KingdomDef>();
        foreach (KingdomDef k in document.State.Scenario.Kingdoms)
        {
            if (k.Active && k.StartTileX >= 0 && k.StartTileY >= 0 && document.ContainsTile(k.StartTileX, k.StartTileY))
            {
                starts.Add(k);
            }
        }

        var regions = new List<int[]>();
        var results = ImmutableArray.CreateBuilder<FairStart>();
        foreach (KingdomDef k in starts)
        {
            int[] distance = Walk(document, occupancy, walkable, k.StartTileX, k.StartTileY);
            regions.Add(distance);
            results.Add(new FairStart(k.Number, k.StartTileX, k.StartTileY, Nearest(distance, resource, gold, w, h), Nearest(distance, resource, stone, w, h), Nearest(distance, resource, wood, w, h), []));
        }

        for (int a = 0; a < starts.Count; a++)
        {
            var reaches = ImmutableArray.CreateBuilder<int>();
            for (int b = 0; b < starts.Count; b++)
            {
                if (a != b && regions[a][(starts[b].StartTileY * w) + starts[b].StartTileX] >= 0)
                {
                    reaches.Add(starts[b].Number);
                }
            }

            results[a] = results[a] with { Reaches = reaches.ToImmutable() };
        }

        ImmutableArray<FairStart> all = results.ToImmutable();
        var findings = ImmutableArray.CreateBuilder<string>();
        if (all.Length < 2)
        {
            findings.Add("Fewer than two kingdoms have a starting location: set them with the keys 1-8 on the map.");
        }

        Compare(all, "gold", s => s.Gold, findings);
        Compare(all, "stone", s => s.Stone, findings);
        Compare(all, "wood", s => s.Wood, findings);
        foreach (FairStart s in all)
        {
            if (all.Length > 1 && s.Reaches.Length < all.Length - 1)
            {
                findings.Add(string.Create(CultureInfo.InvariantCulture, $"Kingdom {s.Kingdom} cannot walk to {Join(all, s)} (water, cliffs or walls between)."));
            }
        }

        return new FairnessReport(all, findings.ToImmutable());
    }

    private static string Join(ImmutableArray<FairStart> all, FairStart s)
    {
        var missing = new List<string>();
        foreach (FairStart o in all)
        {
            if (o.Kingdom != s.Kingdom && !s.Reaches.Contains(o.Kingdom))
            {
                missing.Add("kingdom " + o.Kingdom.ToString(CultureInfo.InvariantCulture));
            }
        }

        return string.Join(", ", missing);
    }

    private static void Compare(ImmutableArray<FairStart> all, string what, Func<FairStart, int?> of, ImmutableArray<string>.Builder findings)
    {
        FairStart? nearest = null;
        foreach (FairStart s in all)
        {
            if (of(s) is null)
            {
                findings.Add(string.Create(CultureInfo.InvariantCulture, $"Kingdom {s.Kingdom} reaches no {what} by land."));
            }
            else if (nearest is null || of(s) < of(nearest))
            {
                nearest = s;
            }
        }

        if (nearest is null)
        {
            return;
        }

        int best = of(nearest)!.Value;
        foreach (FairStart s in all)
        {
            if (of(s) is int d && d - best >= MinGap && d * 2 > best * 3)
            {
                findings.Add(string.Create(CultureInfo.InvariantCulture, $"{char.ToUpperInvariant(what[0])}{what[1..]}: kingdom {s.Kingdom} walks {d} tiles, kingdom {nearest.Kingdom} only {best}."));
            }
        }
    }

    // Steps from the start (or from the building on it) to every tile walkable from there; -1 where it does not reach.
    private static int[] Walk(MapDocument doc, Occupancy occupancy, bool[] walkable, int sx, int sy)
    {
        int w = doc.Width, h = doc.Height;
        var distance = new int[w * h];
        Array.Fill(distance, -1);
        var queue = new Queue<int>();
        int own = occupancy.StructureAt(sx, sy);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if ((x == sx && y == sy) || (own > 0 && occupancy.StructureAt(x, y) == own))
                {
                    distance[(y * w) + x] = 0;
                    queue.Enqueue((y * w) + x);
                }
            }
        }

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % w, y = i / w;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || (uint)nx >= (uint)w || (uint)ny >= (uint)h)
                    {
                        continue;
                    }

                    int n = (ny * w) + nx;
                    if (distance[n] < 0 && walkable[n])
                    {
                        distance[n] = distance[i] + 1;
                        queue.Enqueue(n);
                    }
                }
            }
        }

        return distance;
    }

    // The fewest steps to a tile next to a resource of kind `kind`, or null.
    private static int? Nearest(int[] distance, int[] resource, int kind, int w, int h)
    {
        if (kind < 0)
        {
            return null;
        }

        int? best = null;
        for (int i = 0; i < resource.Length; i++)
        {
            if (resource[i] != kind)
            {
                continue;
            }

            int x = i % w, y = i / w;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx < (uint)w && (uint)ny < (uint)h && distance[(ny * w) + nx] is >= 0 and var d && (best is null || d + 1 < best))
                    {
                        best = d + 1;
                    }
                }
            }
        }

        return best;
    }
}
