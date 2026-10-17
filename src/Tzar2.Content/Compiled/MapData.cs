using System;
using System.Text;
using Tzar2.Core.Hashing;

namespace Tzar2.Content.Compiled;

/// <summary>
/// The static part of a compiled map as the simulation consumes it (TDD §9.1, REQ-ARCH-01). Immutable: the
/// constructor copies the layers. Placed objects (trees, rocks, buildings, start units) are added with the map
/// converter (§18.5); M2 needs only the terrain layers.
/// </summary>
/// <remarks>
/// Layers are row-major. The terrain and height layers are <b>corner grids</b> of (W+1)×(H+1) vertices (TDD §9.1),
/// the cliff layer is a tile grid of W×H. Corner (x, y) is the top-left corner of tile (x, y).
/// </remarks>
public sealed class MapData
{
    /// <summary>Format version of <see cref="ContentHash"/>'s encoding.</summary>
    public const int HashVersion = 1;

    /// <summary>Smallest map side in tiles (test maps; classic maps start at 64).</summary>
    public const int MinSize = 1;

    /// <summary>Largest map side in tiles (REQ-PATH-00: extended maps up to 1,024).</summary>
    public const int MaxSize = 1024;

    private readonly byte[] _terrainCorners;
    private readonly byte[] _heightCorners;
    private readonly byte[] _cliffTiles;

    /// <summary>Creates a map.</summary>
    /// <param name="name">Display name.</param>
    /// <param name="width">Width in tiles.</param>
    /// <param name="height">Height in tiles.</param>
    /// <param name="terrainCorners">(W+1)×(H+1) terrain type ids.</param>
    /// <param name="heightCorners">(W+1)×(H+1) heights, or empty for a flat map.</param>
    /// <param name="cliffTiles">W×H flags (non-zero = cliff tile, impassable for land), or empty for none.</param>
    /// <exception cref="ArgumentException">A size or layer length is invalid.</exception>
    public MapData(string name, int width, int height, ReadOnlySpan<byte> terrainCorners, ReadOnlySpan<byte> heightCorners, ReadOnlySpan<byte> cliffTiles)
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

        ContentHash = ComputeHash();
    }

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

    /// <summary>xxHash64 of the map (lobby hash check, replay header; REQ-NET-05, §19.4).</summary>
    public ulong ContentHash { get; }

    /// <summary>Terrain type id at corner (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public byte TerrainAt(int x, int y) => _terrainCorners[(y * (Width + 1)) + x];

    /// <summary>Checks that every terrain id exists in <paramref name="ruleset"/>.</summary>
    /// <exception cref="ArgumentException">A corner uses an unknown terrain type.</exception>
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
    }

    private ulong ComputeHash()
    {
        var hasher = new StateHasher();
        hasher.Add(HashVersion).Add(Encoding.UTF8.GetBytes(Name)).Add(Width).Add(Height)
            .Add(_terrainCorners).Add(_heightCorners).Add(_cliffTiles);
        return hasher.GetCurrentHash();
    }
}
