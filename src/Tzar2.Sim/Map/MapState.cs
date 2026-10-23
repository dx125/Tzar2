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
/// <see cref="ObstacleVersion"/>; every effective passability change also bumps <see cref="PassabilityVersion"/>, the
/// key that pathfinding caches (connectivity, HPA* clusters, flow fields) and plans are validated against. <b>Occupancy</b> counts mobile entities per sub-tile cell
/// (<see cref="Ruleset.OccupancyCellsPerTileAxis"/>²  cells per tile).</para>
/// <para>Memory at 1,024² with 2×2 cells: passability 1 MiB, blockers 2 MiB, occupancy 16 MiB (<c>int</c> counts, so
/// no cell can overflow even with every entity in one cell, ADR 0019). Snapshots store only the obstacle version and
/// the non-zero blockers; base passability is recomputed from the map and occupancy is rebuilt from the entities'
/// <c>OccupancyCell</c> components.</para>
/// </remarks>
public sealed class MapState
{
    /// <summary>World units per tile (TDD §8.2: 32 px × 64 units).</summary>
    public const int UnitsPerTile = 2048;

    private readonly byte[] _basePassability;
    private readonly ushort[] _blockers;
    private readonly byte[] _bridges;
    private readonly ushort[] _walls; // derived: how many of a tile's blockers are walls (rebuilt on restore)
    private readonly int[] _occupancy;
    private int[] _changedTiles = new int[64];
    private int _changedCount;

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
        _bridges = new byte[Width * Height];
        _walls = new ushort[Width * Height];
        _occupancy = new int[CellWidth * CellHeight];

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
                    // Cliffs stop every ground unit but the ninja, who climbs a band of one or two tiles from ground to
                    // ground, never three (WORLD-018, ADR 0220).
                    mask &= ThinCliff(map, terrainMasks, x, y) ? LocomotorMask.Air | (mask & LocomotorMask.Climber) : LocomotorMask.Air;
                }

                _basePassability[(y * Width) + x] = (byte)mask;
            }
        }
    }

    // Whether a cliff tile lies in a band a climber crosses: along a row, a column or a diagonal, the run of cliff tiles
    // through it is at most two long and has ground a climber may stand on at both ends (ReTzar 0x4a99a0 refuses a third
    // cliff cell in a row of steps). Walls are climbed the same way (ThinWall).
    private static bool ThinCliff(MapData map, LocomotorMask[] terrainMasks, int x, int y)
    {
        ReadOnlySpan<(int Dx, int Dy)> axes = [(1, 0), (0, 1), (1, 1), (1, -1)];
        foreach ((int dx, int dy) in axes)
        {
            int run = 1;
            int ax = x - dx, ay = y - dy;
            while (IsCliff(map, ax, ay) && run <= 2)
            {
                run++;
                ax -= dx;
                ay -= dy;
            }

            int bx = x + dx, by = y + dy;
            while (IsCliff(map, bx, by) && run <= 2)
            {
                run++;
                bx += dx;
                by += dy;
            }

            if (run <= 2 && Climbable(map, terrainMasks, ax, ay) && Climbable(map, terrainMasks, bx, by))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCliff(MapData map, int x, int y)
        => (uint)x < (uint)map.Width && (uint)y < (uint)map.Height && map.CliffTiles[(y * map.Width) + x] != 0;

    // A tile off the cliff whose four corners let a climber stand.
    private static bool Climbable(MapData map, LocomotorMask[] terrainMasks, int x, int y)
    {
        if ((uint)x >= (uint)map.Width || (uint)y >= (uint)map.Height || map.CliffTiles[(y * map.Width) + x] != 0)
        {
            return false;
        }

        ReadOnlySpan<byte> corners = map.TerrainCorners;
        int stride = map.Width + 1;
        int c = (y * stride) + x;
        return (terrainMasks[corners[c]] & terrainMasks[corners[c + 1]] & terrainMasks[corners[c + stride]] & terrainMasks[corners[c + stride + 1]]
            & LocomotorMask.Climber) != 0;
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

    /// <summary>
    /// Incremented only when a tile's effective passability changes (its blocker count goes 0 → 1 or 1 → 0): the
    /// version navigation and plans are validated against (a second blocker on a blocked tile changes nothing).
    /// </summary>
    public int PassabilityVersion { get; private set; }

    /// <summary>Base passability per tile (<see cref="LocomotorMask"/> bytes), ignoring blockers.</summary>
    public ReadOnlySpan<byte> BasePassability => _basePassability;

    /// <summary>Blocker count per tile.</summary>
    public ReadOnlySpan<ushort> Blockers => _blockers;

    /// <summary>Occupant count per cell.</summary>
    public ReadOnlySpan<int> Occupancy => _occupancy;

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
        if (_bridges[i] != 0)
        {
            // A completed bridge deck carries land units and closes the water under it to ships (GAME-03, ADR 0043).
            mask = (mask & ~LocomotorMask.Ship) | LocomotorMask.Land;
        }

        if (_blockers[i] == 0)
        {
            return mask;
        }

        // A wall stops every ground unit but the ninja, who climbs a band of one or two tiles of wall or cliff from ground
        // to ground, as he climbs cliffs (WORLD-018).
        return _walls[i] == _blockers[i] && (mask & LocomotorMask.Climber) != 0 && ThinWall(tx, ty)
            ? mask & (LocomotorMask.Air | LocomotorMask.Climber)
            : mask & LocomotorMask.Air;
    }

    // Whether a wall tile lies in a band a climber crosses: along a row, a column or a diagonal, the run of wall or cliff
    // tiles through it is at most two long and has open ground at both ends (ReTzar 0x4a99a0: walls and cliffs alike).
    private bool ThinWall(int x, int y)
    {
        ReadOnlySpan<(int Dx, int Dy)> axes = [(1, 0), (0, 1), (1, 1), (1, -1)];
        foreach ((int dx, int dy) in axes)
        {
            int run = 1;
            int ax = x - dx, ay = y - dy;
            while (Climbed(ax, ay) && run <= 2)
            {
                run++;
                ax -= dx;
                ay -= dy;
            }

            int bx = x + dx, by = y + dy;
            while (Climbed(bx, by) && run <= 2)
            {
                run++;
                bx += dx;
                by += dy;
            }

            if (run <= 2 && Ground(ax, ay) && Ground(bx, by))
            {
                return true;
            }
        }

        return false;
    }

    // A tile a climber climbs over: a cliff, or one only walls block.
    private bool Climbed(int x, int y)
    {
        if (!ContainsTile(x, y))
        {
            return false;
        }

        int i = (y * Width) + x;
        return Data.CliffTiles[i] != 0 || (_blockers[i] != 0 && _walls[i] == _blockers[i]);
    }

    // A tile a climber stands on before and after the climb: no cliff, no blocker, ground or a bridge deck.
    private bool Ground(int x, int y)
    {
        if (!ContainsTile(x, y))
        {
            return false;
        }

        int i = (y * Width) + x;
        return Data.CliffTiles[i] == 0 && _blockers[i] == 0 && (((LocomotorMask)_basePassability[i] & LocomotorMask.Climber) != 0 || _bridges[i] != 0);
    }

    /// <summary>Bridge deck count per tile.</summary>
    public ReadOnlySpan<byte> Bridges => _bridges;

    /// <summary>Adds a bridge deck to a tile.</summary>
    public void AddBridge(int tx, int ty)
    {
        int i = TileIndex(tx, ty);
        _bridges[i] = checked((byte)(_bridges[i] + 1));
        ObstacleVersion++;
        if (_bridges[i] == 1)
        {
            RecordChange(i);
            PassabilityVersion++;
        }
    }

    /// <summary>Removes a bridge deck from a tile.</summary>
    /// <exception cref="InvalidOperationException">The tile has no bridge.</exception>
    public void RemoveBridge(int tx, int ty)
    {
        int i = TileIndex(tx, ty);
        if (_bridges[i] == 0)
        {
            throw new InvalidOperationException($"Tile ({tx}, {ty}) has no bridge.");
        }

        _bridges[i]--;
        ObstacleVersion++;
        if (_bridges[i] == 0)
        {
            RecordChange(i);
            PassabilityVersion++;
        }
    }

    /// <summary>Whether <paramref name="locomotor"/> may enter tile (<paramref name="tx"/>, <paramref name="ty"/>).</summary>
    public bool IsPassable(Locomotor locomotor, int tx, int ty) => PassabilityAt(tx, ty).Allows(locomotor);

    /// <summary>Whether <paramref name="locomotor"/> may stand at world point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public bool IsPassableAt(Locomotor locomotor, int x, int y)
        => ContainsPoint(x, y) && IsPassable(locomotor, x / UnitsPerTile, y / UnitsPerTile);

    /// <summary>Occupancy cell index of a world point on the map.</summary>
    public int CellIndexAt(int x, int y) => ((y / UnitsPerCell) * CellWidth) + (x / UnitsPerCell);

    /// <summary>Adds a footprint obstacle to a tile; <paramref name="wall"/>: a wall's, which ninjas climb (WORLD-018).</summary>
    public void AddBlocker(int tx, int ty, bool wall = false)
    {
        int i = TileIndex(tx, ty);
        bool climbs = wall || _walls[i] != 0;
        Span<byte> before = stackalloc byte[25];
        if (climbs)
        {
            Around(tx, ty, before);
        }

        _blockers[i] = checked((ushort)(_blockers[i] + 1));
        ObstacleVersion++;
        if (wall)
        {
            _walls[i]++;
        }

        if (climbs)
        {
            RecordAround(tx, ty, before);
        }

        if (_blockers[i] == 1)
        {
            RecordChange(i);
            PassabilityVersion++;
        }
    }

    /// <summary>Removes a footprint obstacle from a tile (<paramref name="wall"/>: a wall's).</summary>
    /// <exception cref="InvalidOperationException">The tile has no blocker (of a wall).</exception>
    public void RemoveBlocker(int tx, int ty, bool wall = false)
    {
        int i = TileIndex(tx, ty);
        if (_blockers[i] == 0 || (wall && _walls[i] == 0))
        {
            throw new InvalidOperationException($"Tile ({tx}, {ty}) has no blocker.");
        }

        bool climbs = wall || _walls[i] != 0;
        Span<byte> before = stackalloc byte[25];
        if (climbs)
        {
            Around(tx, ty, before);
        }

        _blockers[i]--;
        ObstacleVersion++;
        if (wall)
        {
            _walls[i]--;
        }

        if (climbs)
        {
            RecordAround(tx, ty, before);
        }

        if (_blockers[i] == 0)
        {
            RecordChange(i);
            PassabilityVersion++;
        }
    }

    /// <summary>
    /// Tiles whose effective passability changed since the last <see cref="ClearChangedTiles"/> (consumed by the
    /// navigation layers for incremental updates; derived bookkeeping, not state).
    /// </summary>
    internal ReadOnlySpan<int> ChangedTiles => _changedTiles.AsSpan(0, _changedCount);

    /// <summary>Whether the blockers were replaced wholesale (snapshot restore) since <see cref="ClearChangedTiles"/>.</summary>
    internal bool BlockersReplaced { get; private set; }

    /// <summary>Forgets <see cref="ChangedTiles"/> and <see cref="BlockersReplaced"/>.</summary>
    internal void ClearChangedTiles()
    {
        _changedCount = 0;
        BlockersReplaced = false;
    }

    /// <summary>Counts an entity into a cell.</summary>
    internal void AddOccupant(int cell) => _occupancy[cell]++;

    /// <summary>Counts an entity out of a cell.</summary>
    internal void RemoveOccupant(int cell)
    {
        if (_occupancy[cell] == 0)
        {
            throw new InvalidOperationException($"Occupancy cell {cell} is already empty.");
        }

        _occupancy[cell]--;
    }

    /// <summary>The dynamic layers' arrays for <see cref="AppendTo"/>, in its order (ADR 0215).</summary>
    public void CollectBulk(Snapshots.BulkHash bulk)
    {
        ArgumentNullException.ThrowIfNull(bulk);
        bulk.Add(_blockers);
        bulk.Add(_bridges);
        bulk.Add(_occupancy);
    }

    /// <summary>
    /// Adds the dynamic layers to a state hash (base passability is a pure function of map + ruleset); the layers' bytes
    /// come from <paramref name="bulk"/>, collected by <see cref="CollectBulk"/> (ADR 0215).
    /// </summary>
    public void AppendTo(StateHasher hasher, Snapshots.BulkHash bulk)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(bulk);
        hasher.Add(ObstacleVersion).Add(PassabilityVersion);
        bulk.AppendNext(hasher);
        bulk.AppendNext(hasher);
        bulk.AppendNext(hasher);
    }

    /// <summary>
    /// Writes the dynamic layers for a snapshot: the obstacle version and the non-zero blockers as (tile index delta,
    /// count) pairs. Occupancy is derived state and is rebuilt on restore (<see cref="RebuildOccupancy"/>).
    /// </summary>
    public void WriteTo(BinaryBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteInt32(ObstacleVersion);
        writer.WriteInt32(PassabilityVersion);
        int nonZero = 0;
        foreach (ushort b in _blockers)
        {
            nonZero += b != 0 ? 1 : 0;
        }

        writer.WriteVarUInt64((ulong)nonZero);
        int previous = -1;
        for (int i = 0; i < _blockers.Length; i++)
        {
            if (_blockers[i] != 0)
            {
                writer.WriteVarUInt64((ulong)(i - previous - 1));
                writer.WriteVarUInt64(_blockers[i]);
                previous = i;
            }
        }

        nonZero = 0;
        foreach (byte b in _bridges)
        {
            nonZero += b != 0 ? 1 : 0;
        }

        writer.WriteVarUInt64((ulong)nonZero);
        previous = -1;
        for (int i = 0; i < _bridges.Length; i++)
        {
            if (_bridges[i] != 0)
            {
                writer.WriteVarUInt64((ulong)(i - previous - 1));
                writer.WriteVarUInt64(_bridges[i]);
                previous = i;
            }
        }
    }

    /// <summary>Restores the layers written by <see cref="WriteTo"/>; occupancy is cleared for <see cref="RebuildOccupancy"/>.</summary>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    public void ReadFrom(ref BinaryBufferReader reader)
    {
        ObstacleVersion = reader.ReadInt32();
        PassabilityVersion = reader.ReadInt32();
        Array.Clear(_blockers);
        Array.Clear(_occupancy);
        ulong count = reader.ReadVarUInt64();
        if (count > (ulong)_blockers.Length)
        {
            throw new InvalidDataException($"Snapshot lists {count} blocked tiles on a {Width}x{Height} map.");
        }

        long index = -1;
        for (ulong k = 0; k < count; k++)
        {
            ulong delta = reader.ReadVarUInt64();
            ulong value = reader.ReadVarUInt64();
            index += (long)Math.Min(delta, (ulong)_blockers.Length) + 1;
            if (index >= _blockers.Length || value is 0 or > ushort.MaxValue)
            {
                throw new InvalidDataException("Invalid blocker layer in snapshot.");
            }

            _blockers[index] = (ushort)value;
        }

        Array.Clear(_bridges);
        count = reader.ReadVarUInt64();
        if (count > (ulong)_bridges.Length)
        {
            throw new InvalidDataException($"Snapshot lists {count} bridge tiles on a {Width}x{Height} map.");
        }

        index = -1;
        for (ulong k = 0; k < count; k++)
        {
            ulong delta = reader.ReadVarUInt64();
            ulong value = reader.ReadVarUInt64();
            index += (long)Math.Min(delta, (ulong)_bridges.Length) + 1;
            if (index >= _bridges.Length || value is 0 or > byte.MaxValue)
            {
                throw new InvalidDataException("Invalid bridge layer in snapshot.");
            }

            _bridges[index] = (byte)value;
        }

        BlockersReplaced = true;
    }

    /// <summary>
    /// Recomputes the occupancy counts from the entities' cells (snapshot restore). Every cell must be on the grid.
    /// </summary>
    /// <exception cref="InvalidDataException">A cell is off the grid.</exception>
    internal void RebuildOccupancy(ReadOnlySpan<Components.OccupancyCell> cells)
    {
        Array.Clear(_occupancy);
        foreach (Components.OccupancyCell cell in cells)
        {
            if ((uint)cell.Cell >= (uint)_occupancy.Length)
            {
                throw new InvalidDataException($"Occupancy cell {cell.Cell} is off the {CellWidth}x{CellHeight} grid.");
            }

            _occupancy[cell.Cell]++;
        }
    }

    // The passability of the 5 x 5 tiles around a tile: a wall there may open or close a climb up to two tiles away.
    private void Around(int tx, int ty, Span<byte> into)
    {
        for (int k = 0; k < 25; k++)
        {
            into[k] = (byte)PassabilityAt(tx + (k % 5) - 2, ty + (k / 5) - 2);
        }
    }

    // Records the tiles around a changed wall tile whose passability changed for the climber (WORLD-018); the tile itself
    // is recorded by its blocker count as before.
    private void RecordAround(int tx, int ty, ReadOnlySpan<byte> before)
    {
        for (int k = 0; k < 25; k++)
        {
            int x = tx + (k % 5) - 2, y = ty + (k / 5) - 2;
            if ((x != tx || y != ty) && ContainsTile(x, y) && (byte)PassabilityAt(x, y) != before[k])
            {
                RecordChange((y * Width) + x);
                PassabilityVersion++;
            }
        }
    }

    /// <summary>Forgets which blockers are walls, before <see cref="CountWall"/> counts them again (snapshot restore).</summary>
    internal void ClearWalls() => Array.Clear(_walls);

    /// <summary>Counts one of a tile's blockers as a wall's (snapshot restore).</summary>
    internal void CountWall(int tx, int ty) => _walls[TileIndex(tx, ty)]++;

    private void RecordChange(int tile)
    {
        if (_changedCount == _changedTiles.Length)
        {
            Array.Resize(ref _changedTiles, _changedTiles.Length * 2);
        }

        _changedTiles[_changedCount++] = tile;
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
