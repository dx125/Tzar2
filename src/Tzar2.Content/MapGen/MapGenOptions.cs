using System;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.MapGen;

/// <summary>Random map types of GAME-18, in the order of the original lobby list (<c>m_type_0…5</c>).</summary>
public enum RandomMapType : byte
{
    /// <summary>One of the other five types, drawn from the seed (⚠ UNVERIFIED reading, see the placeholders register).</summary>
    Random = 0,

    /// <summary>Every kingdom on its own island among small islands; kingdoms meet by sea.</summary>
    Archipelagos = 1,

    /// <summary>One large island in the middle of an ocean; all kingdoms on it.</summary>
    Island = 2,

    /// <summary>A continent that fills the map, ocean along the borders.</summary>
    Continental = 3,

    /// <summary>Open land with lakes.</summary>
    Lakes = 4,

    /// <summary>Land without water.</summary>
    Dry = 5,
}

/// <summary>Map sizes (editor: Tiny 64 … Real World 512; the game lobby offers Medium and up).</summary>
public enum RandomMapSize : byte
{
    /// <summary>64 × 64 (editor only).</summary>
    Tiny = 0,

    /// <summary>96 × 96 (editor only).</summary>
    Small = 1,

    /// <summary>128 × 128.</summary>
    Medium = 2,

    /// <summary>192 × 192.</summary>
    Large = 3,

    /// <summary>256 × 256.</summary>
    Huge = 4,

    /// <summary>384 × 384.</summary>
    Enormous = 5,

    /// <summary>512 × 512.</summary>
    RealWorld = 6,
}

/// <summary>The four original terrain sets (<c>IMAGES/TERRAIN/&lt;SET&gt;</c>).</summary>
public enum MapTerrainSet : byte
{
    /// <summary>Grassland (<c>grass</c>).</summary>
    Grass = 0,

    /// <summary>Summer (<c>summer</c>).</summary>
    Summer = 1,

    /// <summary>Desert (<c>desert</c>).</summary>
    Desert = 2,

    /// <summary>Winter (<c>snow</c>).</summary>
    Winter = 3,
}

/// <summary>The lobby's MAP RESOURCES level.</summary>
public enum MapResourceLevel : byte
{
    /// <summary>Low.</summary>
    Low = 0,

    /// <summary>Normal.</summary>
    Normal = 1,

    /// <summary>High.</summary>
    High = 2,
}

/// <summary>Optional map contents (the lobby's and the editor's check boxes, GAME-18).</summary>
[Flags]
public enum MapFeatures : ushort
{
    /// <summary>Nothing optional.</summary>
    None = 0,

    /// <summary>Forests and single trees beyond each kingdom's starting wood (editor: Trees).</summary>
    Trees = 1 << 0,

    /// <summary>Ground objects: decorative rocks that block a tile (editor: Rocks).</summary>
    Rocks = 1 << 1,

    /// <summary>Ground patches: decorations without a footprint (editor: Patches).</summary>
    Patches = 1 << 2,

    /// <summary>Cows near the castles and in the wild, bears in the wild (Animals).</summary>
    Animals = 1 << 3,

    /// <summary>Cliff ridges (Cliffs).</summary>
    Cliffs = 1 << 4,

    /// <summary>Rivers with fords (editor: Rivers).</summary>
    Rivers = 1 << 5,

    /// <summary>Guarded spots with neutral towers, the most central one a small town (lobby: Guarded Town / Towers).</summary>
    GuardedTowns = 1 << 6,

    /// <summary>Magic gateways (Gateways).</summary>
    Gateways = 1 << 7,

    /// <summary>Magic items on the ground (Magic Items).</summary>
    MagicItems = 1 << 8,

    /// <summary>⚠ UNVERIFIED defaults of the original dialogs: trees, rocks, patches and animals.</summary>
    Default = Trees | Rocks | Patches | Animals,

    /// <summary>Everything.</summary>
    All = Trees | Rocks | Patches | Animals | Cliffs | Rivers | GuardedTowns | Gateways | MagicItems,
}

/// <summary>
/// Input of <see cref="MapGenerator"/> (GAME-18, ADR 0060). The same options and seed give a byte-identical
/// <see cref="MapData"/> on every platform.
/// </summary>
public sealed record MapGenOptions
{
    /// <summary>Most kingdoms a random map places (REQ-PATH-00, the <c>classic</c> UI's 8).</summary>
    public const int MaxPlayers = 8;

    /// <summary>Map type.</summary>
    public RandomMapType Type { get; init; } = RandomMapType.Continental;

    /// <summary>Terrain set.</summary>
    public MapTerrainSet TerrainSet { get; init; } = MapTerrainSet.Grass;

    /// <summary>Map size.</summary>
    public RandomMapSize Size { get; init; } = RandomMapSize.Medium;

    /// <summary>Match seed; the generator draws only from its <c>mapgen</c> stream (REQ-SIM-06).</summary>
    public ulong Seed { get; init; }

    /// <summary>Number of kingdoms, 1…<see cref="MaxPlayers"/>.</summary>
    public int Players { get; init; } = 2;

    /// <summary>Team of each player (0…7), or empty for every player on a team of its own. Teammates start next to each other.</summary>
    public ImmutableArray<int> Teams { get; init; } = [];

    /// <summary>Civilization of each player (a <see cref="MapGenCatalog.Civilizations"/> entry or <c>random</c>), or empty for all random.</summary>
    public ImmutableArray<string> Civilizations { get; init; } = [];

    /// <summary>Map resources level.</summary>
    public MapResourceLevel Resources { get; init; } = MapResourceLevel.Normal;

    /// <summary>Optional contents.</summary>
    public MapFeatures Features { get; init; } = MapFeatures.Default;

    /// <summary>Entity types the generator places.</summary>
    public MapGenCatalog Catalog { get; init; } = MapGenCatalog.Classic;

    /// <summary>Ruleset terrain id written to <see cref="MapData.TerrainCorners"/> for land corners (classic: <c>grass</c> = 0).</summary>
    public byte LandTerrainId { get; init; }

    /// <summary>Ruleset terrain id written for water corners (classic: <c>water</c> = 1).</summary>
    public byte WaterTerrainId { get; init; } = 1;

    /// <summary>Attempts before the generator gives up (each attempt is validated; ≥ 1).</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>Map name, or null for one derived from the options.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// These options for <paramref name="ruleset"/>: the terrain ids of its <c>grass</c> and <c>water</c> types, and the
    /// catalog restricted to the types it defines (<see cref="MapGenCatalog.RestrictTo"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The ruleset has no <c>water</c> type or no land type.</exception>
    public MapGenOptions BindTo(Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        int water = -1, land = -1;
        foreach (TerrainTypeDefinition t in ruleset.TerrainTypes)
        {
            if (water < 0 && t.IsWater)
            {
                water = t.Id;
            }

            if (land < 0 && !t.IsWater && t.Passable.Allows(Locomotor.Foot))
            {
                land = t.Id;
            }
        }

        if (water < 0 || land < 0)
        {
            throw new ArgumentException($"Ruleset '{ruleset.Name}' needs a water terrain and a walkable land terrain.", nameof(ruleset));
        }

        return this with { WaterTerrainId = (byte)water, LandTerrainId = (byte)land, Catalog = Catalog.RestrictTo(ruleset) };
    }
}

/// <summary>Names and sizes of the option enums (CLI and lobby spelling).</summary>
public static class RandomMapNames
{
    /// <summary>Side in tiles of <paramref name="size"/>.</summary>
    public static int Side(RandomMapSize size) => size switch
    {
        RandomMapSize.Tiny => 64,
        RandomMapSize.Small => 96,
        RandomMapSize.Medium => 128,
        RandomMapSize.Large => 192,
        RandomMapSize.Huge => 256,
        RandomMapSize.Enormous => 384,
        RandomMapSize.RealWorld => 512,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    /// <summary>The terrain set id (folder name in lower case) of <paramref name="set"/>.</summary>
    public static string SetId(MapTerrainSet set) => set switch
    {
        MapTerrainSet.Grass => "grass",
        MapTerrainSet.Summer => "summer",
        MapTerrainSet.Desert => "desert",
        MapTerrainSet.Winter => "snow",
        _ => throw new ArgumentOutOfRangeException(nameof(set)),
    };

    /// <summary>Lower-case name of <paramref name="type"/>.</summary>
    public static string Name(RandomMapType type) => type switch
    {
        RandomMapType.Random => "random",
        RandomMapType.Archipelagos => "archipelagos",
        RandomMapType.Island => "island",
        RandomMapType.Continental => "continental",
        RandomMapType.Lakes => "lakes",
        RandomMapType.Dry => "dry",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Lower-case name of <paramref name="size"/> (<c>real-world</c>).</summary>
    public static string Name(RandomMapSize size) => size switch
    {
        RandomMapSize.Tiny => "tiny",
        RandomMapSize.Small => "small",
        RandomMapSize.Medium => "medium",
        RandomMapSize.Large => "large",
        RandomMapSize.Huge => "huge",
        RandomMapSize.Enormous => "enormous",
        RandomMapSize.RealWorld => "real-world",
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    /// <summary>Lower-case name of <paramref name="set"/> (<c>winter</c>).</summary>
    public static string Name(MapTerrainSet set) => set switch
    {
        MapTerrainSet.Grass => "grass",
        MapTerrainSet.Summer => "summer",
        MapTerrainSet.Desert => "desert",
        MapTerrainSet.Winter => "winter",
        _ => throw new ArgumentOutOfRangeException(nameof(set)),
    };

    /// <summary>Parses a type name (case-insensitive).</summary>
    public static bool TryParse(string? text, out RandomMapType type)
    {
        for (type = RandomMapType.Random; type <= RandomMapType.Dry; type++)
        {
            if (string.Equals(text, Name(type), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        type = default;
        return false;
    }

    /// <summary>Parses a size name (case-insensitive; <c>realworld</c> and the side in tiles are accepted too).</summary>
    public static bool TryParse(string? text, out RandomMapSize size)
    {
        for (size = RandomMapSize.Tiny; size <= RandomMapSize.RealWorld; size++)
        {
            if (string.Equals(text, Name(size), StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, Name(size).Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, Side(size).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                return true;
            }
        }

        size = default;
        return false;
    }

    /// <summary>Parses a terrain set name (case-insensitive; <c>snow</c> = winter).</summary>
    public static bool TryParse(string? text, out MapTerrainSet set)
    {
        for (set = MapTerrainSet.Grass; set <= MapTerrainSet.Winter; set++)
        {
            if (string.Equals(text, Name(set), StringComparison.OrdinalIgnoreCase) || string.Equals(text, SetId(set), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        set = default;
        return false;
    }
}
