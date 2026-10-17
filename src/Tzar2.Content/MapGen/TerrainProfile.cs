using System;
using System.Collections.Immutable;

namespace Tzar2.Content.MapGen;

/// <summary>
/// One painted terrain of a set: <see cref="Terrain"/> is painted on corners that hold <see cref="Parent"/>, on about
/// <see cref="CoverPermille"/> ‰ of them, in blobs of roughly <see cref="CellTiles"/> tiles.
/// </summary>
/// <param name="Terrain">Terrain-set index painted.</param>
/// <param name="Parent">Terrain-set index it is painted on (its only transition partner besides its own children).</param>
/// <param name="CoverPermille">Share of the parent's corners, ‰.</param>
/// <param name="CellTiles">Noise cell size in tiles (feature size).</param>
public readonly record struct TerrainLayer(int Terrain, int Parent, int CoverPermille, int CellTiles);

/// <summary>
/// What the generator knows about a terrain set (ADR 0060): its terrain indices, the pairs that have a transition strip
/// (the corner/transition model of TDD §9.1/§13.5, docs/formats/bmp.md §3.2), the terrains trees grow on, and the
/// painting layers. Index 0 is water and index 1 the coast terrain: in all four sets water has a transition only to 1
/// (besides 0–4/0–2 shores the generator does not use).
/// </summary>
/// <remarks>
/// <para>The transition pairs and tree terrains restate structural facts of <c>&lt;SET&gt;.TERRAIN.INI</c>
/// (<c>[terrainfiles]</c>, <c>[treeterrain]</c>); pairs with the cliff pseudo-terrains 8/9 are left out (UNK-10).</para>
/// <para>The layers form a tree rooted at water: 0 → 1 → painted terrains. A corner may border (8-neighbourhood) only its
/// own terrain, its parent and its children, so every tile has at most two corner terrains and they are a parent/child
/// pair — a pair that has a transition (<see cref="Tzar2.Content.Terrain.TerrainComposer.IsDrawnByRule"/>). Which
/// terrains the original paints and how much (<c>[randomterrainset]</c>, semantics unknown) is ⚠ UNVERIFIED; the layer
/// values are ours.</para>
/// </remarks>
public sealed class TerrainProfile
{
    /// <summary>Terrain index of water.</summary>
    public const int Water = 0;

    /// <summary>Terrain index of the coast terrain (the land terrain next to water).</summary>
    public const int Coast = 1;

    private readonly int[] _parent;
    private readonly int[] _depth;
    private readonly uint _pairs;

    private TerrainProfile(MapTerrainSet set, int terrainCount, string pairs, int treeMask, TerrainLayer[] layers)
    {
        Set = set;
        Id = RandomMapNames.SetId(set);
        TerrainCount = terrainCount;
        TreeTerrainMask = treeMask;
        foreach (string p in pairs.Split(' '))
        {
            _pairs |= PairBit(p[0] - '0', p[1] - '0');
        }

        _parent = new int[terrainCount];
        _depth = new int[terrainCount];
        Array.Fill(_parent, -1);
        _parent[Coast] = Water;
        _depth[Coast] = 1;
        foreach (TerrainLayer layer in layers)
        {
            if (!HasTransition(layer.Parent, layer.Terrain) || _depth[layer.Parent] == 0)
            {
                throw new InvalidOperationException($"Layer {layer} of set {Id} has no transition to its parent.");
            }

            _parent[layer.Terrain] = layer.Parent;
            _depth[layer.Terrain] = _depth[layer.Parent] + 1;
        }

        Layers = [.. layers];
    }

    /// <summary>The set.</summary>
    public MapTerrainSet Set { get; }

    /// <summary>Set id (<c>grass</c>, <c>summer</c>, <c>desert</c>, <c>snow</c>).</summary>
    public string Id { get; }

    /// <summary>Terrain indices 0…<see cref="TerrainCount"/> − 1 (water included).</summary>
    public int TerrainCount { get; }

    /// <summary>Bit <c>1 &lt;&lt; t</c> set for each terrain <c>t</c> trees grow on.</summary>
    public int TreeTerrainMask { get; }

    /// <summary>Painting layers, parents before children.</summary>
    public ImmutableArray<TerrainLayer> Layers { get; }

    /// <summary>Grassland.</summary>
    public static TerrainProfile Grass { get; } = new(
        MapTerrainSet.Grass, 8, "01 04 12 13 14 15 16 17 23 24 25 26 27 34 35 36 37 45 46 47", 0b11110,
        [new(2, 1, 700, 20), new(3, 2, 150, 8), new(4, 1, 60, 6)]);

    /// <summary>Summer.</summary>
    public static TerrainProfile Summer { get; } = new(
        MapTerrainSet.Summer, 8, "01 04 12 13 14 15 16 17 23 24 25 26 27 34 35 36 37 45 46 47", 0b11110,
        [new(2, 1, 650, 20), new(4, 2, 120, 8), new(5, 2, 150, 10)]);

    /// <summary>Desert.</summary>
    public static TerrainProfile Desert { get; } = new(
        MapTerrainSet.Desert, 6, "01 02 12 13 14 15 23 24 25 34", 0b11110,
        [new(2, 1, 350, 16), new(3, 1, 120, 10), new(5, 1, 40, 6)]);

    /// <summary>Winter.</summary>
    public static TerrainProfile Winter { get; } = new(
        MapTerrainSet.Winter, 5, "01 12 13 14 23 24 34", 0b11110,
        [new(3, 1, 300, 16), new(2, 1, 100, 10), new(4, 2, 250, 6)]);

    /// <summary>The profile of <paramref name="set"/>.</summary>
    public static TerrainProfile For(MapTerrainSet set) => set switch
    {
        MapTerrainSet.Grass => Grass,
        MapTerrainSet.Summer => Summer,
        MapTerrainSet.Desert => Desert,
        MapTerrainSet.Winter => Winter,
        _ => throw new ArgumentOutOfRangeException(nameof(set)),
    };

    /// <summary>Whether the set has a transition strip for (<paramref name="a"/>, <paramref name="b"/>), any order.</summary>
    public bool HasTransition(int a, int b)
        => a != b && (uint)a < (uint)TerrainCount && (uint)b < (uint)TerrainCount && (_pairs & PairBit(Math.Min(a, b), Math.Max(a, b))) != 0;

    /// <summary>The terrain <paramref name="terrain"/> is painted on (water: −1; unpainted terrains: −1).</summary>
    public int ParentOf(int terrain) => (uint)terrain < (uint)TerrainCount ? _parent[terrain] : -1;

    /// <summary>Depth in the layer tree: water 0, coast 1, painted terrains 2…; unpainted terrains 0.</summary>
    public int DepthOf(int terrain) => (uint)terrain < (uint)TerrainCount ? _depth[terrain] : 0;

    /// <summary>Whether a corner of terrain <paramref name="a"/> may border one of <paramref name="b"/>.</summary>
    public bool MayBorder(int a, int b) => a == b || ParentOf(a) == b || ParentOf(b) == a;

    /// <summary>Whether trees grow on terrain <paramref name="terrain"/>.</summary>
    public bool GrowsTrees(int terrain) => (uint)terrain < 32u && (TreeTerrainMask & (1 << terrain)) != 0;

    private static uint PairBit(int lo, int hi) => 1u << ((lo * 8) + hi - ((lo + 1) * (lo + 2) / 2));
}
