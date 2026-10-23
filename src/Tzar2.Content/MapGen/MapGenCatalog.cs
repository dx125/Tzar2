using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.MapGen;

/// <summary>What a placed object is for; the catalog maps each role to an entity type.</summary>
public enum MapObjectRole : byte
{
    /// <summary>A kingdom's castle (key template with <c>{civ}</c>).</summary>
    Castle = 0,

    /// <summary>A kingdom's starting peasant (key template with <c>{civ}</c>).</summary>
    Peasant,

    /// <summary>A tree (one per tile, §9.1).</summary>
    Tree,

    /// <summary>A gold rock.</summary>
    Gold,

    /// <summary>A stone rock.</summary>
    Stone,

    /// <summary>A fish patch (on water).</summary>
    FishPatch,

    /// <summary>A cow (food).</summary>
    Cow,

    /// <summary>A bear (hostile animal).</summary>
    Bear,

    /// <summary>A magic gateway.</summary>
    Gateway,

    /// <summary>A neutral tower guarding a spot.</summary>
    Tower,

    /// <summary>A neutral house of a guarded town.</summary>
    TownHouse,

    /// <summary>A magic item on the ground.</summary>
    Item,

    /// <summary>A decorative ground object that blocks its tile.</summary>
    GroundObject,

    /// <summary>A decorative ground patch without a footprint.</summary>
    GroundPatch,

    /// <summary>A weapon or shield on the ground (the original's <c>item_plusplus</c>; ADR 0264).</summary>
    Weapon,

    /// <summary>A spell book on the ground (the original's <c>item_magic</c>; ADR 0264).</summary>
    Book,

    /// <summary>A potion on the ground (the original's <c>item_health</c>; ADR 0264).</summary>
    Potion,
}

/// <summary>An entity type the generator places for a role.</summary>
/// <param name="KeyTemplate">Ruleset key; <c>{civ}</c> is replaced by the civilization (<c>euro.castle</c>).</param>
/// <param name="Width">Footprint width in tiles, 0 for objects without one (units, fish, items, patches).</param>
/// <param name="Height">Footprint height in tiles.</param>
public readonly record struct MapObjectKind(string KeyTemplate, int Width, int Height)
{
    /// <summary>Whether the object blocks the tiles under it.</summary>
    public bool HasFootprint => Width > 0 && Height > 0;

    /// <summary>The ruleset key for <paramref name="civilization"/>.</summary>
    public string KeyFor(string civilization) => KeyTemplate.Replace("{civ}", civilization, StringComparison.Ordinal);
}

/// <summary>
/// The entity types a random map uses, per <see cref="MapObjectRole"/> (ADR 0060). A role without a kind is not placed.
/// <see cref="Classic"/> names the <c>classic</c> ruleset's keys with ⚠ UNVERIFIED placeholder footprints, so maps can be
/// generated and hashed without original data; <see cref="RestrictTo"/> takes the footprints a compiled ruleset defines
/// and drops the roles it does not.
/// </summary>
public sealed class MapGenCatalog
{
    /// <summary>The number of roles.</summary>
    public const int RoleCount = (int)MapObjectRole.Potion + 1;

    private readonly MapObjectKind?[] _kinds;

    /// <summary>Creates a catalog.</summary>
    /// <param name="civilizations">Civilizations a kingdom may have (non-empty; <c>random</c> picks among them).</param>
    /// <param name="kinds">Kind per role, indexed by <see cref="MapObjectRole"/> (missing or null = not placed).</param>
    /// <param name="startingPeasants">Peasants next to each castle.</param>
    /// <exception cref="ArgumentException">No civilization, or a footprint outside 0…<see cref="EntityTypeDefinition.MaxFootprintTiles"/>.</exception>
    public MapGenCatalog(IReadOnlyList<string> civilizations, IReadOnlyList<MapObjectKind?> kinds, int startingPeasants)
    {
        ArgumentNullException.ThrowIfNull(civilizations);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentOutOfRangeException.ThrowIfNegative(startingPeasants);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startingPeasants, 50);
        if (civilizations.Count == 0)
        {
            throw new ArgumentException("A catalog needs at least one civilization.", nameof(civilizations));
        }

        Civilizations = [.. civilizations];
        _kinds = new MapObjectKind?[RoleCount];
        for (int i = 0; i < Math.Min(kinds.Count, RoleCount); i++)
        {
            if (kinds[i] is { } k)
            {
                if (string.IsNullOrEmpty(k.KeyTemplate) || (uint)k.Width > EntityTypeDefinition.MaxFootprintTiles || (uint)k.Height > EntityTypeDefinition.MaxFootprintTiles)
                {
                    throw new ArgumentException($"Invalid kind for role {(MapObjectRole)i}.", nameof(kinds));
                }

                _kinds[i] = k;
            }
        }

        StartingPeasants = startingPeasants;
    }

    /// <summary>
    /// The <c>classic</c> ruleset's types. Footprints are ⚠ UNVERIFIED placeholders (castle 5 × 5, rocks 2 × 2, towers and
    /// houses 2 × 2, gateway 3 × 3; docs/mechanics/mapgen-placeholders.md); five starting peasants (UNVERIFIED).
    /// </summary>
    public static MapGenCatalog Classic { get; } = new(
        ["euro", "arab", "asia"], // the classic ruleset's order (ADR 0211); bound to a ruleset, its own list replaces it
        [
            new MapObjectKind("{civ}.castle", 5, 5),
            new MapObjectKind("{civ}.peon", 0, 0),
            new MapObjectKind("comm.tree", 1, 1),
            new MapObjectKind("comm.res_gold", 2, 2),
            new MapObjectKind("comm.res_stone", 2, 2),
            new MapObjectKind("comm.fishpatch", 0, 0),
            new MapObjectKind("comm.cow", 0, 0),
            new MapObjectKind("comm.bear", 0, 0),
            new MapObjectKind("comm.magicgateway", 3, 3),
            new MapObjectKind("comm.tower_stone", 2, 2),
            new MapObjectKind("euro.house", 2, 2),
            new MapObjectKind("comm.item", 0, 0),
            new MapObjectKind("comm.ground_object", 1, 1),
            new MapObjectKind("comm.ground_patch", 0, 0),
            new MapObjectKind("comm.item_plusplus", 0, 0),
            new MapObjectKind("comm.item_magic", 0, 0),
            new MapObjectKind("comm.item_health", 0, 0),
        ],
        startingPeasants: 5);

    /// <summary>Civilizations a kingdom may have.</summary>
    public ImmutableArray<string> Civilizations { get; }

    /// <summary>Peasants placed next to each castle.</summary>
    public int StartingPeasants { get; }

    /// <summary>The kind placed for <paramref name="role"/>, or null when the role is not placed.</summary>
    public MapObjectKind? Kind(MapObjectRole role) => (uint)role < RoleCount ? _kinds[(int)role] : null;

    /// <summary>
    /// This catalog for <paramref name="ruleset"/>: civilizations whose castle and peasant types exist, roles whose type
    /// exists (for every kept civilization), footprints from the ruleset's types.
    /// </summary>
    /// <exception cref="ArgumentException">No civilization has both a castle and a peasant type in the ruleset.</exception>
    public MapGenCatalog RestrictTo(Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        // The ruleset's playable civilizations in its order (ADR 0211; a fork's own ones too); the catalog's list for a
        // ruleset that lists none.
        IEnumerable<string> candidates = ruleset.Civilizations.Count > 0 ? ruleset.Civilizations.Where(ruleset.IsPlayableCivilization) : Civilizations;
        var civs = new List<string>();
        foreach (string civ in candidates)
        {
            if (Exists(ruleset, MapObjectRole.Castle, civ) && Exists(ruleset, MapObjectRole.Peasant, civ))
            {
                civs.Add(civ);
            }
        }

        if (civs.Count == 0)
        {
            throw new ArgumentException($"Ruleset '{ruleset.Name}' defines no castle and peasant of any catalog civilization.", nameof(ruleset));
        }

        var kinds = new MapObjectKind?[RoleCount];
        for (int r = 0; r < RoleCount; r++)
        {
            if (_kinds[r] is not { } kind)
            {
                continue;
            }

            int width = 0, height = 0;
            bool all = true;
            foreach (string civ in civs)
            {
                int id = ruleset.EntityTypeId(kind.KeyFor(civ));
                if (id < 0)
                {
                    all = false;
                    break;
                }

                // One footprint per role for every civilization: the envelope (widest × tallest) covers each of them.
                EntityTypeDefinition t = ruleset.EntityTypes[id];
                width = Math.Max(width, t.FootprintWidth);
                height = Math.Max(height, t.FootprintHeight);
            }

            if (all)
            {
                kinds[r] = kind with { Width = width, Height = height };
            }
        }

        return new MapGenCatalog(civs, kinds, StartingPeasants);
    }

    private bool Exists(Ruleset ruleset, MapObjectRole role, string civ)
        => Kind(role) is { } kind && ruleset.EntityTypeId(kind.KeyFor(civ)) >= 0;
}
