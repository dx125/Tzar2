using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Terrain;

namespace Tzar2.Content.Editing;

/// <summary>One paintable terrain of the palette.</summary>
/// <param name="Index">Value painted into the corners: a terrain-set index with a set, else a ruleset terrain id.</param>
/// <param name="Name">Display name.</param>
/// <param name="IsWater">Water (ships, no buildings).</param>
public readonly record struct PaletteTerrain(int Index, string Name, bool IsWater);

/// <summary>
/// What may be painted and how a painted corner maps to the simulation (TDD §18.2 "Terrain"). With a terrain set
/// (<c>&lt;SET&gt;.TERRAIN.INI</c>) the palette is the set's terrains, index 0 is water, every other index is land
/// (the converter's mapping, ADR 0121), and a tile whose corners need a transition the set lacks is <b>invalid</b>
/// (drawn red, reported by the validator). Without a set the palette is the ruleset's terrain types and corners are
/// painted as ruleset ids directly (maps made in code, the demo campaign).
/// </summary>
public sealed class TerrainRules
{
    private readonly TerrainSet? _set;
    private readonly byte _water;
    private readonly byte _land;
    private readonly bool[] _waterIds;

    private TerrainRules(string? setId, TerrainSet? set, IReadOnlyList<PaletteTerrain> palette, byte water, byte land, bool[] waterIds)
    {
        SetId = setId;
        _set = set;
        Palette = palette;
        _water = water;
        _land = land;
        _waterIds = waterIds;
    }

    /// <summary>The terrain set id, or null for direct ruleset painting.</summary>
    public string? SetId { get; }

    /// <summary>The paintable terrains.</summary>
    public IReadOnlyList<PaletteTerrain> Palette { get; }

    /// <summary>Whether corners carry terrain-set indices.</summary>
    public bool HasSet => SetId is not null;

    /// <summary>Rules for a terrain set; <paramref name="set"/> null means the set's art is not available (no transition checks).</summary>
    /// <param name="setId">Set id (<c>grass</c>, <c>summer</c>…).</param>
    /// <param name="set">The set's manifest entry, or null.</param>
    /// <param name="ruleset">The ruleset (water and land ids), or null for the classic defaults.</param>
    public static TerrainRules ForSet(string setId, TerrainSet? set, Ruleset? ruleset)
    {
        ArgumentException.ThrowIfNullOrEmpty(setId);
        (byte water, byte land, bool[] waterIds) = Ids(ruleset);
        var palette = new List<PaletteTerrain>();
        if (set is not null)
        {
            foreach (TerrainType t in set.Terrains)
            {
                if (t.Index is >= 0 and < 8)
                {
                    palette.Add(new PaletteTerrain(t.Index, t.Name, t.Index == 0));
                }
            }
        }

        if (palette.Count == 0)
        {
            palette.Add(new PaletteTerrain(0, "Water", true));
            palette.Add(new PaletteTerrain(1, "Land", false));
        }

        palette.Sort((a, b) => a.Index.CompareTo(b.Index));
        return new TerrainRules(setId, set, palette, water, land, waterIds);
    }

    /// <summary>Rules for maps without a terrain set: the ruleset's terrain types are painted directly.</summary>
    public static TerrainRules WithoutSet(Ruleset? ruleset)
    {
        (byte water, byte land, bool[] waterIds) = Ids(ruleset);
        var palette = new List<PaletteTerrain>();
        if (ruleset is not null)
        {
            foreach (TerrainTypeDefinition t in ruleset.TerrainTypes)
            {
                palette.Add(new PaletteTerrain(t.Id, t.Key, t.IsWater));
            }
        }
        else
        {
            palette.Add(new PaletteTerrain(0, "grass", false));
            palette.Add(new PaletteTerrain(1, "water", true));
        }

        return new TerrainRules(null, null, palette, water, land, waterIds);
    }

    /// <summary>The ruleset terrain id of a painted corner value.</summary>
    public byte SimTerrainOf(int painted) => HasSet ? (painted == 0 ? _water : _land) : (byte)painted;

    /// <summary>Whether a painted corner value is water.</summary>
    public bool IsWater(int painted) => HasSet ? painted == 0 : (uint)painted < (uint)_waterIds.Length && _waterIds[painted];

    /// <summary>Whether <paramref name="painted"/> is a value of the palette.</summary>
    public bool IsPaintable(int painted)
    {
        foreach (PaletteTerrain t in Palette)
        {
            if (t.Index == painted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The default painted value of a new map (the first land terrain).</summary>
    public int DefaultLand
    {
        get
        {
            foreach (PaletteTerrain t in Palette)
            {
                if (!t.IsWater)
                {
                    return t.Index;
                }
            }

            return Palette[0].Index;
        }
    }

    /// <summary>
    /// Whether a tile with these four painted corners can be drawn: one terrain, or two with a transition in the set.
    /// Always true without a set or without the set's art.
    /// </summary>
    public bool IsDrawable(int tl, int tr, int br, int bl)
    {
        if (_set is null)
        {
            return true;
        }

        int a = tl, b = -1;
        foreach (int c in (ReadOnlySpan<int>)[tr, br, bl])
        {
            if (c == a || c == b)
            {
                continue;
            }

            if (b >= 0)
            {
                return false;
            }

            b = c;
        }

        return b < 0 || _set.TransitionOf(a, b) is { IsUnresolved: false };
    }

    private static (byte Water, byte Land, bool[] WaterIds) Ids(Ruleset? ruleset)
    {
        if (ruleset is null)
        {
            return (1, 0, [false, true]);
        }

        byte water = 1, land = 0;
        bool foundWater = false, foundLand = false;
        var waterIds = new bool[ruleset.TerrainTypes.Count];
        for (int i = 0; i < ruleset.TerrainTypes.Count; i++)
        {
            TerrainTypeDefinition t = ruleset.TerrainTypes[i];
            waterIds[i] = t.IsWater;
            if (t.Key == "water" || (!foundWater && t.IsWater))
            {
                water = (byte)i;
                foundWater |= t.Key == "water";
            }

            if (t.Key == "grass" || (!foundLand && !t.IsWater))
            {
                land = (byte)i;
                foundLand |= t.Key == "grass";
            }
        }

        return (water, land, waterIds);
    }
}
