using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Assets;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Editing;

/// <summary>
/// A group of decoration pictures the editor offers together (TDD §18.2 "Decorations"; ADR 0281): one
/// <c>displayname</c> of a section, with its pictures in file order. Random-within-group and TAB pick among them.
/// </summary>
/// <param name="Section">The palette section (<see cref="DecorationCatalog.Patches"/>, …).</param>
/// <param name="Class">The class decorations of the group are placed as (<c>ground_patch</c>, <c>ground_object</c>, <c>ruin</c>).</param>
/// <param name="Name">The group's name (the pictures' <c>IMAGES.INI</c> <c>displayname</c>).</param>
/// <param name="OnWater">Whether its pictures lie on water (<c>terrains = water</c>), else on land.</param>
/// <param name="Images">The pictures' names.</param>
public sealed record DecorationGroup(string Section, string Class, string Name, bool OnWater, ImmutableArray<string> Images);

/// <summary>
/// The decoration palette of a terrain set, read from its and the common <c>IMAGES.INI</c> as the original's editor offers
/// them (ADR 0281): <c>gp_</c> pictures are ground patches, <c>go_</c> ones ground objects (on land, or on water when
/// their <c>terrains</c> say so), <c>gr_</c> ones the special locations (ruins); groups are their <c>displayname</c>.
/// </summary>
public static class DecorationCatalog
{
    /// <summary>Ground patches: flat pictures that stack anywhere on land.</summary>
    public const string Patches = "Ground patches";

    /// <summary>Ground objects on land (rocks, holes, skeletons…): they close the cells under them.</summary>
    public const string Objects = "Ground objects";

    /// <summary>Ground objects on water (lake rocks, water cliffs).</summary>
    public const string WaterObjects = "Water objects";

    /// <summary>Special locations (statues, altars: the original's ruins).</summary>
    public const string Special = "Special locations";

    /// <summary>The sections in palette order.</summary>
    public static IReadOnlyList<string> Sections { get; } = [Patches, Objects, WaterObjects, Special];

    /// <summary>The groups of terrain set <paramref name="terrainSet"/>, by section, then name.</summary>
    public static ImmutableArray<DecorationGroup> Build(SpriteIndex index, string? terrainSet)
    {
        ArgumentNullException.ThrowIfNull(index);
        string common = SpriteIndex.TerrainIni(SceneryTable.CommonSet);
        string? own = terrainSet is null ? null : SpriteIndex.TerrainIni(terrainSet);
        var groups = new Dictionary<(string Section, string Name), (string Class, bool Water, List<string> Images)>(); // lookup only (T2D004)
        var order = new List<(string Section, string Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? ini in new[] { own, common })
        {
            if (ini is null)
            {
                continue;
            }

            foreach (SpriteIndexEntry e in index.Entries)
            {
                if (!string.Equals(e.Ini, ini, StringComparison.OrdinalIgnoreCase) || e.Asset is null || index.IsCompanion(e) || !seen.Add(e.Name))
                {
                    continue;
                }

                bool water = string.Equals(e.Get("terrains"), "water", StringComparison.OrdinalIgnoreCase);
                (string section, string cls)? kind = e.Name.Length < 3 ? null : e.Name[..3].ToLowerInvariant() switch
                {
                    "gp_" => (Patches, MapDecoration.GroundPatch),
                    "go_" => (water ? WaterObjects : Objects, MapDecoration.GroundObject),
                    "gr_" => (Special, MapDecoration.Ruin),
                    _ => null,
                };
                if (kind is not { } k)
                {
                    continue;
                }

                string name = e.Get("displayname") is { Length: > 0 } shown ? shown : "Other";
                if (!groups.TryGetValue((k.section, name), out var group))
                {
                    group = (k.cls, water, []);
                    groups[(k.section, name)] = group;
                    order.Add((k.section, name));
                }

                group.Images.Add(e.Name);
            }
        }

        order.Sort(static (a, b) =>
        {
            int c = IndexOf(a.Section).CompareTo(IndexOf(b.Section));
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });
        var result = ImmutableArray.CreateBuilder<DecorationGroup>(order.Count);
        foreach ((string section, string name) in order)
        {
            (string cls, bool water, List<string> images) = groups[(section, name)];
            result.Add(new DecorationGroup(section, cls, name, water, [.. images]));
        }

        return result.MoveToImmutable();

        static int IndexOf(string section) => section switch
        {
            Patches => 0,
            Objects => 1,
            WaterObjects => 2,
            _ => 3,
        };
    }
}
