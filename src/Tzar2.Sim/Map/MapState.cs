using System;
using System.IO;
using System.Runtime.InteropServices;
using Tzar2.Content.Compiled;
using Tzar2.Core.Hashing;
using Tzar2.Core.Serialization;

namespace Tzar2.Sim.Map;

/// <summary>
/// The simulation's view of the map (TDD §9.1, ADR 0018): static layers derived from <see cref="MapData"/> and the
/// <see cref="Ruleset"/>, plus the dynamic obstacle and occupancy layers. Sized for maps up to 1,024² tiles.
/// </summary>
/// <remarks>
/// <para>Layers are row-major. <b>Base passability</b> is one <see cref="LocomotorMask"/> per tile: a locomotor may
/// cross a tile if all four corner terrain types allow it and the tile is not a cliff (cliffs leave only
/// <see cref="Locomotor.Air"/>). <b>Blockers</b> count static footprint obstacles per tile (buildings, trees, rocks;
/// corpses and debris never block, REQ-PATH-08); a blocked tile is passable for air only. Every blocker change bumps
/// <see cref="ObstacleVersion"/>, the key that pathfinding caches (connectivity, HPA* clusters, flow fields) are
/// validated against. <b>Occupancy</b> counts mobile entities per sub-tile cell
/// (<see cref="Ruleset.OccupancyCellsPerTileAxis"/>²  cells per tile).</para>
/// <para>Memory at 1,024² with 2×2 cells: passability 1 MiB, blockers 2 MiB, occupancy 8 MiB.</para>
/// </remarks>
public sealed class MapState
{
    /// <summary>World units per tile (TDD §8.2: 32 px × 64 units).</summary>
    public const int UnitsPerTile = 2048;

    private readonly byte[] _basePassability;
    private readonly ushort[] _blockers;
    private readonly ushort[] _occupancy;

    /// <summary>Builds the map state.</summary>
    /// <exception cref="ArgumentException">The map uses terrain types the ruleset lacks.</exception>
    public MapState(MapData map, Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(ruleset);
        map.Validate(ruleset);
        Data = map;
        Width = map.Width;
        Height = map.Height;
        CellsPerTileAxis = ruleset.OccupancyCellsPerTileAxis;
        CellWidth = Width * CellsPerTileAxis;
        CellHeight = Height * CellsPerTileAxis;
        UnitsPerCell = UnitsPerTile / CellsPerTileAxis;

        _basePassability = new byte[Width * Height];
        _blockers = new ushort[Width * Height];
        _occupancy = new ushort[CellWidth * CellHeight];

        var terrainMasks = new LocomotorMask[ruleset.TerrainTypes.Count];
        for (int i = 0; i < terrainMasks.Length; i++)
        {
            terrainMasks[i] = ruleset.TerrainTypes[i].Passable;
        }

        ReadOnlySpan<byte> corners = map.TerrainCorners;
        ReadOnlySpan<byte> cliffs = map.CliffTiles;
        int stride = Width + 1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int c = (y * stride) + x;
                LocomotorMask mask = terrainMasks[corners[c]] & terrainMasks[corners[c + 1]]
                    & terrainMasks[corners[c + stride]] & terrainMasks[corners[c + stride + 1]];
                if (cliffs[(y * Width) + x] != 0)
                {
                    mask &= LocomotorMask.Air;
                }

                _basePassability[(y * Width) + x] = (byte)mask;
            }
        }
    }

    /// <summary>The compiled map this state was built from.</summary>
    public MapData Data { get; }

    /// <summary>Width in tiles.</summary>
    public int Width { get; }

    /// <summary>Height in tiles.</summary>
    public int Height { get; }

    /// <summary>Width in world units.</summary>
    public int WorldWidth => Width * UnitsPerTile;

    /// <summary>Height in world units.</summary>
    public int WorldHeight => Height * UnitsPerTile;

    /// <summary>Occupancy cells per tile along each axis.</summary>
    public int CellsPerTileAxis { get; }

    /// <summary>Occupancy grid width in cells.</summary>
    public int CellWidth { get; }

    /// <summary>Occupancy grid height in cells.</summary>
    public int CellHeight { get; }

    /// <summary>World units per occupancy cell.</summary>
    public int UnitsPerCell { get; }

    /// <summary>Incremented on every blocker change; pathfinding caches key on it (REQ-PATH-01/02/04).</summary>
    public int ObstacleVersion { get; private set; }

    /// <summary>Base passability per tile (<see cref="LocomotorMask"/> bytes), ignoring blockers.</summary>
    public ReadOnlySpan<byte> BasePassability => _basePassability;

    /// <summary>Blocker count per tile.</summary>
    public ReadOnlySpan<ushort> Blockers => _blockers;

    /// <summary>Occupant count per cell.</summary>
    public ReadOnlySpan<ushort> Occupancy => _occupancy;

    /// <summary>Whether tile (<paramref name="tx"/>, <paramref name="ty"/>) is on the map.</summary>
    public bool ContainsTile(int tx, int ty) => (uint)tx < (uint)Width && (uint)ty < (uint)Height;

    /// <summary>Whether world point (<paramref name="x"/>, <paramref name="y"/>) is on the map.</summary>
    public bool ContainsPoint(int x, int y) => (uint)x < (uint)WorldWidth && (uint)y < (uint)WorldHeight;

    /// <summary>Effective passability of a tile, including blockers; <see cref="LocomotorMask.None"/> off the map.</summary>
    public LocomotorMask PassabilityAt(int tx, int ty)
    {
        if (!ContainsTile(tx, ty))
        {
            return LocomotorMask.None;
        }

        int i = (ty * Width) + tx;
        var mask = (LocomotorMask)_basePassability[i];
        return _blockers[i] != 0 ? mask & LocomotorMask.Air : mask;
    }

    /// <summary>Whether <paramref name="locomotor"/> may enter tile (<paramref name="tx"/>, <paramref name="ty"/>).</summary>
    public bool IsPassable(Locomotor locomotor, int tx, int ty) => PassabilityAt(tx, ty).Allows(locomotor);

    /// <summary>Whether <paramref name="locomotor"/> may stand at world point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public bool IsPassableAt(Locomotor locomotor, int x, int y)
        => ContainsPoint(x, y) && IsPassable(locomotor, x / UnitsPerTile, y / UnitsPerTile);

    /// <summary>Occupancy cell index of a world point on the map.</summary>
    public int CellIndexAt(int x, int y) => ((y / UnitsPerCell) * CellWidth) + (x / UnitsPerCell);

    /// <summary>Adds a footprint obstacle to a tile.</summary>
    public void AddBlocker(int tx, int ty)
    {
        int i = TileIndex(tx, ty);
        _blockers[i] = checked((ushort)(_blockers[i] + 1));
        ObstacleVersion++;
    }

    /// <summary>Removes a footprint obstacle from a tile.</summary>
    /// <exception cref="InvalidOperationException">The tile has no blocker.</exception>
    public void RemoveBlocker(int tx, int ty)
    {
        int i = TileIndex(tx, ty);
        if (_blockers[i] == 0)
        {
            throw new InvalidOperationException($"Tile ({tx}, {ty}) has no blocker.");
        }

        _blockers[i]--;
        ObstacleVersion++;
    }

    /// <summary>Counts an entity into a cell.</summary>
    internal void AddOccupant(int cell) => _occupancy[cell] = checked((ushort)(_occupancy[cell] + 1));

    /// <summary>Counts an entity out of a cell.</summary>
    internal void RemoveOccupant(int cell)
    {
        if (_occupancy[cell] == 0)
        {
            throw new InvalidOperationException($"Occupancy cell {cell} is already empty.");
        }

        _occupancy[cell]--;
    }

    /// <summary>Adds the dynamic layers to a state hash (base passability is a pure function of map + ruleset).</summary>
    public void AppendTo(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Add(ObstacleVersion).AddSpan<ushort>(_blockers).AddSpan<ushort>(_occupancy);
    }

    /// <summary>Writes the dynamic layers for a snapshot.</summary>
    public void WriteTo(BinaryBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(ObstacleVersion);
        writer.WriteBytes(MemoryMarshal.AsBytes(_blockers.AsSpan()));
        writer.WriteBytes(MemoryMarshal.AsBytes(_occupancy.AsSpan()));
    }

    /// <summary>Restores the dynamic layers written by <see cref="WriteTo"/>.</summary>
    /// <exception cref="InvalidDataException">The data is truncated.</exception>
    public void ReadFrom(ref BinaryBufferReader reader)
    {
        ObstacleVersion = reader.ReadInt32();
        reader.ReadBytes(_blockers.Length * sizeof(ushort)).CopyTo(MemoryMarshal.AsBytes(_blockers.AsSpan()));
        reader.ReadBytes(_occupancy.Length * sizeof(ushort)).CopyTo(MemoryMarshal.AsBytes(_occupancy.AsSpan()));
    }

    private int TileIndex(int tx, int ty)
    {
        if (!ContainsTile(tx, ty))
        {
            throw new ArgumentOutOfRangeException(nameof(tx), $"Tile ({tx}, {ty}) is off the map.");
        }

        return (ty * Width) + tx;
    }
}
