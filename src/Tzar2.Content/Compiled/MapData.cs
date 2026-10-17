using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Tzar2.Core.Hashing;

namespace Tzar2.Content.Compiled;

/// <summary>
/// The static part of a compiled map as the simulation consumes it (TDD §9.1, REQ-ARCH-01). Immutable: the
/// constructor copies the layers. <see cref="Objects"/> are the placed objects (trees, rocks, buildings, start units)
/// the match starts with; the legacy map converter (§18.5) fills them later, M2 test maps build them in code.
/// </summary>
/// <remarks>
/// Layers are row-major. The terrain and height layers are <b>corner grids</b> of (W+1)×(H+1) vertices (TDD §9.1),
/// the cliff layer is a tile grid of W×H. Corner (x, y) is the top-left corner of tile (x, y).
/// <para>
/// A map may also name its <see cref="TerrainSet"/> and carry <see cref="SetTerrainCorners"/>: the terrain-set index
/// (0 = water, 1… = ground types of <c>&lt;SET&gt;.TERRAIN.INI</c>) of every corner, the layer the renderer selects base
/// and transition tiles from (docs/formats/bmp.md §3). The simulation ignores it and reads only the ruleset terrain
/// ids of <see cref="TerrainCorners"/>. Random maps (ADR 0060) fill both; maps without a set leave it empty.
/// </para>
/// </remarks>
public sealed class MapData
{
    /// <summary>Format version of <see cref="ContentHash"/>'s encoding.</summary>
    public const int HashVersion = 2;

    /// <summary>Most placed objects a map may hold.</summary>
    public const int MaxObjects = 1 << 18;

    /// <summary>Smallest map side in tiles (test maps; classic maps start at 64).</summary>
    public const int MinSize = 1;

    /// <summary>Largest map side in tiles (REQ-PATH-00: extended maps up to 1,024).</summary>
    public const int MaxSize = 1024;

    private readonly byte[] _terrainCorners;
    private readonly byte[] _heightCorners;
    private readonly byte[] _cliffTiles;
    private readonly ImmutableArray<MapObject> _objects;
    private readonly byte[] _setTerrainCorners;

    /// <summary>Creates a map.</summary>
    /// <param name="name">Display name.</param>
    /// <param name="width">Width in tiles.</param>
    /// <param name="height">Height in tiles.</param>
    /// <param name="terrainCorners">(W+1)×(H+1) terrain type ids.</param>
    /// <param name="heightCorners">(W+1)×(H+1) heights, or empty for a flat map.</param>
    /// <param name="cliffTiles">W×H flags (non-zero = cliff tile, impassable for land), or empty for none.</param>
    /// <param name="objects">Placed objects, in placement order (null = none).</param>
    /// <param name="terrainSet">Terrain set id (<c>grass</c>, <c>summer</c>, <c>desert</c>, <c>snow</c>…), or null.</param>
    /// <param name="setTerrainCorners">(W+1)×(H+1) terrain-set indices; required with <paramref name="terrainSet"/>, empty without.</param>
    /// <exception cref="ArgumentException">A size or layer length is invalid, or an object is off the map.</exception>
    public MapData(string name, int width, int height, ReadOnlySpan<byte> terrainCorners, ReadOnlySpan<byte> heightCorners, ReadOnlySpan<byte> cliffTiles, IReadOnlyList<MapObject>? objects = null, string? terrainSet = null, ReadOnlySpan<byte> setTerrainCorners = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, MinSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, MinSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaxSize);
        int corners = (width + 1) * (height + 1);
        if (terrainCorners.Length != corners)
        {
            throw new ArgumentException($"Expected {corners} terrain corners, got {terrainCorners.Length}.", nameof(terrainCorners));
        }

        if (heightCorners.Length != 0 && heightCorners.Length != corners)
        {
            throw new ArgumentException($"Expected 0 or {corners} height corners, got {heightCorners.Length}.", nameof(heightCorners));
        }

        if (cliffTiles.Length != 0 && cliffTiles.Length != width * height)
        {
            throw new ArgumentException($"Expected 0 or {width * height} cliff flags, got {cliffTiles.Length}.", nameof(cliffTiles));
        }

        if (terrainSet is null ? setTerrainCorners.Length != 0 : terrainSet.Length == 0 || setTerrainCorners.Length != corners)
        {
            throw new ArgumentException($"A terrain set needs a non-empty id and {corners} set corners; a map without one needs none.", nameof(setTerrainCorners));
        }

        Name = name;
        Width = width;
        Height = height;
        _terrainCorners = terrainCorners.ToArray();
        _heightCorners = heightCorners.Length == 0 ? new byte[corners] : heightCorners.ToArray();
        _cliffTiles = new byte[width * height];
        for (int i = 0; i < cliffTiles.Length; i++)
        {
            _cliffTiles[i] = cliffTiles[i] != 0 ? (byte)1 : (byte)0;
        }

        objects ??= [];
        if (objects.Count > MaxObjects)
        {
            throw new ArgumentException($"At most {MaxObjects} objects.", nameof(objects));
        }

        var builder = ImmutableArray.CreateBuilder<MapObject>(objects.Count);
        foreach (MapObject o in objects)
        {
            if (string.IsNullOrEmpty(o.TypeKey) || (uint)o.X >= (uint)(width * UnitsPerTile) || (uint)o.Y >= (uint)(height * UnitsPerTile))
            {
                throw new ArgumentException($"Object '{o.TypeKey}' at ({o.X}, {o.Y}) is invalid or off the map.", nameof(objects));
            }

            builder.Add(o);
        }

        _objects = builder.MoveToImmutable();
        TerrainSet = terrainSet;
        _setTerrainCorners = setTerrainCorners.ToArray();
        ContentHash = ComputeHash();
    }

    /// <summary>World units per tile (TDD §8.2).</summary>
    public const int UnitsPerTile = 2048;

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Width in tiles.</summary>
    public int Width { get; }

    /// <summary>Height in tiles.</summary>
    public int Height { get; }

    /// <summary>Terrain type id per corner, (W+1)×(H+1), row-major.</summary>
    public ReadOnlySpan<byte> TerrainCorners => _terrainCorners;

    /// <summary>Height per corner, (W+1)×(H+1), row-major (0 everywhere on a flat map).</summary>
    public ReadOnlySpan<byte> HeightCorners => _heightCorners;

    /// <summary>Cliff flag per tile (0 or 1), W×H, row-major.</summary>
    public ReadOnlySpan<byte> CliffTiles => _cliffTiles;

    /// <summary>The objects placed when the match starts, in placement order.</summary>
    public ImmutableArray<MapObject> Objects => _objects;

    /// <summary>Terrain set the renderer draws the map with, or null (ADR 0060).</summary>
    public string? TerrainSet { get; }

    /// <summary>Terrain-set index per corner, (W+1)×(H+1), row-major; empty when <see cref="TerrainSet"/> is null.</summary>
    public ReadOnlySpan<byte> SetTerrainCorners => _setTerrainCorners;

    /// <summary>xxHash64 of the map (lobby hash check, replay header; REQ-NET-05, §19.4).</summary>
    public ulong ContentHash { get; }

    /// <summary>Terrain type id at corner (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public byte TerrainAt(int x, int y) => _terrainCorners[(y * (Width + 1)) + x];

    /// <summary>Checks that every terrain id and object type exists in <paramref name="ruleset"/>.</summary>
    /// <exception cref="ArgumentException">A corner uses an unknown terrain type, or an object an unknown entity type.</exception>
    public void Validate(Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        int count = ruleset.TerrainTypes.Count;
        foreach (byte id in _terrainCorners)
        {
            if (id >= count)
            {
                throw new ArgumentException($"Map '{Name}' uses terrain type {id}, but ruleset '{ruleset.Name}' defines {count}.", nameof(ruleset));
            }
        }

        foreach (MapObject o in _objects)
        {
            if (ruleset.EntityTypeId(o.TypeKey) < 0)
            {
                throw new ArgumentException($"Map '{Name}' places '{o.TypeKey}', which ruleset '{ruleset.Name}' does not define.", nameof(ruleset));
            }
        }
    }

    private ulong ComputeHash()
    {
        var hasher = new StateHasher();
        hasher.Add(HashVersion).Add(Encoding.UTF8.GetBytes(Name)).Add(Width).Add(Height)
            .Add(_terrainCorners).Add(_heightCorners).Add(_cliffTiles);
        hasher.Add(_objects.Length);
        foreach (MapObject o in _objects)
        {
            hasher.Add(Encoding.UTF8.GetBytes(o.TypeKey)).Add(o.Owner).Add(o.X).Add(o.Y);
        }

        // Added after HashVersion 2 without bumping it: maps without a terrain set hash exactly as before.
        if (TerrainSet is not null)
        {
            hasher.Add(Encoding.UTF8.GetBytes(TerrainSet)).Add(_setTerrainCorners);
        }

        return hasher.GetCurrentHash();
    }
}

/// <summary>
/// An object placed on a map at match start. Units stand at (<see cref="X"/>, <see cref="Y"/>) in world units; a
/// structure (a type with a footprint) is placed with its footprint's top-left tile at (X / 2,048, Y / 2,048).
/// </summary>
/// <param name="TypeKey">Entity type key in the ruleset, e.g. <c>euro.castle</c>, <c>comm.tree</c>.</param>
/// <param name="Owner">Player slot, or <see cref="Nature"/> for unowned objects (resources, animals).</param>
/// <param name="X">X in world units.</param>
/// <param name="Y">Y in world units.</param>
public readonly record struct MapObject(string TypeKey, byte Owner, int X, int Y)
{
    /// <summary>The owner of objects that belong to no kingdom (trees, rocks; §9.1 "Nature").</summary>
    public const byte Nature = 0xFF;

    /// <summary>A structure placed with its footprint's top-left at tile (<paramref name="tileX"/>, <paramref name="tileY"/>).</summary>
    public static MapObject AtTile(string typeKey, byte owner, int tileX, int tileY)
        => new(typeKey, owner, tileX * MapData.UnitsPerTile, tileY * MapData.UnitsPerTile);
}
