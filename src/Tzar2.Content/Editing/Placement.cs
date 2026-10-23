using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Editing;

/// <summary>Where a placed entity lives (TDD §18.2 "Units": ground, water and air layers with stacking rules).</summary>
public enum PlacementLayer : byte
{
    /// <summary>Structures: buildings, trees, rocks (a footprint).</summary>
    Structure,

    /// <summary>Land units, items.</summary>
    Ground,

    /// <summary>Ships, fish patches.</summary>
    Water,

    /// <summary>Flying units.</summary>
    Air,
}

/// <summary>The result of a placement check.</summary>
/// <param name="Ok">Whether the object may stand there.</param>
/// <param name="Reason">Why not (empty when it may).</param>
public readonly record struct PlacementCheck(bool Ok, string Reason)
{
    /// <summary>Allowed.</summary>
    public static PlacementCheck Allowed => new(true, string.Empty);

    /// <summary>Refused for <paramref name="reason"/>.</summary>
    public static PlacementCheck No(string reason) => new(false, reason);
}

/// <summary>
/// What occupies each tile of a document (structures by id; units per layer), built once per tool use and updated as the
/// tool places objects. The stacking rule: one structure per tile, one unit per layer per tile (⚠ UNVERIFIED, the
/// manual only says "stacking rules"); air units may stand over anything.
/// </summary>
public sealed class Occupancy
{
    private readonly int _width;
    private readonly int _height;
    private readonly int[] _structure;
    private readonly byte[] _ground;
    private readonly byte[] _water;
    private readonly byte[] _air;
    private readonly PlacementLayer[] _layers;

    /// <summary>Builds the occupancy of <paramref name="document"/>'s objects under <paramref name="ruleset"/>.</summary>
    public Occupancy(MapDocument document, Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ruleset);
        Document = document;
        Ruleset = ruleset;
        _width = document.Width;
        _height = document.Height;
        _structure = new int[_width * _height];
        _ground = new byte[_width * _height];
        _water = new byte[_width * _height];
        _air = new byte[_width * _height];
        _layers = new PlacementLayer[ruleset.EntityTypes.Count];
        for (int t = 0; t < _layers.Length; t++)
        {
            _layers[t] = LayerOf(ruleset.EntityTypes[t]);
        }

        foreach (EditorObject o in document.State.Objects)
        {
            Add(o.Id, o.Placement);
        }
    }

    /// <summary>The document.</summary>
    public MapDocument Document { get; }

    /// <summary>The ruleset.</summary>
    public Ruleset Ruleset { get; }

    /// <summary>Id of the structure on a tile, or 0.</summary>
    public int StructureAt(int x, int y) => Document.ContainsTile(x, y) ? _structure[(y * _width) + x] : 0;

    /// <summary>Units of <paramref name="layer"/> on a tile.</summary>
    public int UnitsAt(int x, int y, PlacementLayer layer)
    {
        if (!Document.ContainsTile(x, y))
        {
            return 0;
        }

        int i = (y * _width) + x;
        return layer switch { PlacementLayer.Ground => _ground[i], PlacementLayer.Water => _water[i], PlacementLayer.Air => _air[i], _ => _structure[i] != 0 ? 1 : 0 };
    }

    /// <summary>Records a placed object.</summary>
    public void Add(int id, MapObject o) => Mark(id, o, add: true);

    /// <summary>Forgets a placed object.</summary>
    public void Remove(int id, MapObject o) => Mark(id, o, add: false);

    /// <summary>The layer an entity type lives on.</summary>
    public static PlacementLayer LayerOf(EntityTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.HasFootprint)
        {
            return PlacementLayer.Structure;
        }

        if (type.Locomotor == Locomotor.Air)
        {
            return PlacementLayer.Air;
        }

        // Fish patches sit on water (ADR 0042); the trait set has no "water" flag, so the class names it.
        bool fish = !type.IsMobile && type.Traits.ResourceNode is not null && (type.Traits.LegacyClass.Contains("fish", StringComparison.Ordinal) || type.Key.Contains("fish", StringComparison.Ordinal));
        return type.Locomotor == Locomotor.Ship || fish ? PlacementLayer.Water : PlacementLayer.Ground;
    }

    /// <summary>The footprint rectangle of a structure placement (rotation applied).</summary>
    public static (int X, int Y, int W, int H) Footprint(EntityTypeDefinition type, MapObject o)
    {
        ArgumentNullException.ThrowIfNull(type);
        bool turned = (o.Flags & MapObjectFlags.Rotated) != 0 && type.CanTurn;
        (int w, int h) = type.FootprintSize(turned);
        return (o.X / MapData.UnitsPerTile, o.Y / MapData.UnitsPerTile, Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>
    /// Whether <paramref name="o"/> may stand where it is (TDD §18.2): structures fit on the map, on land and free tiles
    /// (harbors on the coast, bridges across water with land at both ends); units on their layer's terrain, one per tile
    /// and layer, not inside a structure; items on land; fish patches on water. <paramref name="ignore"/> is an object
    /// that is being moved.
    /// </summary>
    public PlacementCheck Check(MapObject o, int ignore = 0)
    {
        int typeId = Ruleset.EntityTypeId(o.TypeKey);
        if (typeId < 0)
        {
            return PlacementCheck.No($"unknown type '{o.TypeKey}'");
        }

        EntityTypeDefinition type = Ruleset.EntityTypes[typeId];
        int tx = o.X / MapData.UnitsPerTile, ty = o.Y / MapData.UnitsPerTile;
        if (o.X < 0 || o.Y < 0 || !Document.ContainsTile(tx, ty))
        {
            return PlacementCheck.No("off the map");
        }

        PlacementLayer layer = _layers[typeId];
        if (layer == PlacementLayer.Structure)
        {
            return CheckStructure(type, o, ignore);
        }

        int i = (ty * _width) + tx;
        if (layer != PlacementLayer.Air && _structure[i] != 0 && _structure[i] != ignore)
        {
            return PlacementCheck.No("a structure stands there");
        }

        switch (layer)
        {
            case PlacementLayer.Water when !Document.IsWaterTile(tx, ty):
                return PlacementCheck.No("needs water");
            case PlacementLayer.Ground when !Document.IsLandTile(tx, ty) || Document.IsCliff(tx, ty):
                return PlacementCheck.No("needs open land");
        }

        int count = UnitsAt(tx, ty, layer);
        if (ignore != 0 && Document.State.Find(ignore) is { } moving && moving.TileX == tx && moving.TileY == ty)
        {
            count--;
        }

        return count > 0 ? PlacementCheck.No("another object of this layer stands there") : PlacementCheck.Allowed;
    }

    private PlacementCheck CheckStructure(EntityTypeDefinition type, MapObject o, int ignore)
    {
        (int x0, int y0, int w, int h) = Footprint(type, o);
        if (!Document.ContainsTile(x0, y0) || !Document.ContainsTile(x0 + w - 1, y0 + h - 1))
        {
            return PlacementCheck.No("does not fit on the map");
        }

        int water = 0;
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                int i = (y * _width) + x;
                if (_structure[i] != 0 && _structure[i] != ignore)
                {
                    return PlacementCheck.No("overlaps another structure");
                }

                if (Document.IsCliff(x, y))
                {
                    return PlacementCheck.No("stands on a cliff");
                }

                if (!Document.IsLandTile(x, y))
                {
                    water++;
                }
            }
        }

        switch (type.Traits.Placement)
        {
            case PlacementKind.Bridge:
            {
                bool alongX = w >= h;
                for (int k = 0; k < (alongX ? h : w); k++)
                {
                    bool startLand = alongX ? Document.IsLandTile(x0, y0 + k) : Document.IsLandTile(x0 + k, y0);
                    bool endLand = alongX ? Document.IsLandTile(x0 + w - 1, y0 + k) : Document.IsLandTile(x0 + k, y0 + h - 1);
                    if (!startLand || !endLand)
                    {
                        return PlacementCheck.No("a bridge needs land at both ends");
                    }
                }

                return water > 0 ? PlacementCheck.Allowed : PlacementCheck.No("a bridge must cross water");
            }

            case PlacementKind.Coast:
                if (water > 0)
                {
                    return PlacementCheck.No("needs land");
                }

                return TouchesWater(x0, y0, w, h) ? PlacementCheck.Allowed : PlacementCheck.No("a harbor must stand on the coast");
            default:
                return water > 0 ? PlacementCheck.No("needs land") : PlacementCheck.Allowed;
        }
    }

    private bool TouchesWater(int x0, int y0, int w, int h)
    {
        for (int y = y0 - 1; y <= y0 + h; y++)
        {
            for (int x = x0 - 1; x <= x0 + w; x++)
            {
                bool inside = x >= x0 && x < x0 + w && y >= y0 && y < y0 + h;
                if (!inside && Document.ContainsTile(x, y) && !Document.IsLandTile(x, y))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void Mark(int id, MapObject o, bool add)
    {
        int typeId = Ruleset.EntityTypeId(o.TypeKey);
        if (typeId < 0)
        {
            return;
        }

        EntityTypeDefinition type = Ruleset.EntityTypes[typeId];
        PlacementLayer layer = _layers[typeId];
        if (layer == PlacementLayer.Structure)
        {
            (int x0, int y0, int w, int h) = Footprint(type, o);
            for (int y = Math.Max(0, y0); y < Math.Min(_height, y0 + h); y++)
            {
                for (int x = Math.Max(0, x0); x < Math.Min(_width, x0 + w); x++)
                {
                    int i = (y * _width) + x;
                    if (add)
                    {
                        _structure[i] = id;
                    }
                    else if (_structure[i] == id)
                    {
                        _structure[i] = 0;
                    }
                }
            }

            return;
        }

        int tx = o.X / MapData.UnitsPerTile, ty = o.Y / MapData.UnitsPerTile;
        if (!Document.ContainsTile(tx, ty))
        {
            return;
        }

        byte[] cells = layer switch { PlacementLayer.Water => _water, PlacementLayer.Air => _air, _ => _ground };
        int c = (ty * _width) + tx;
        cells[c] = add ? (byte)Math.Min(255, cells[c] + 1) : (byte)Math.Max(0, cells[c] - 1);
    }
}

/// <summary>Brush shapes and sizes of the editor's painting tools.</summary>
public static class Brushes
{
    /// <summary>The six terrain brush sizes (corner square side), Appendix C / §18.2 "6 brush sizes".</summary>
    public static readonly ImmutableArray<int> TerrainSizes = [1, 2, 3, 5, 7, 9];

    /// <summary>The five tree brush radii in tiles (§18.2 "5 tree brushes").</summary>
    public static readonly ImmutableArray<int> TreeRadii = [0, 1, 2, 3, 5];

    /// <summary>The corners a terrain brush of size index <paramref name="size"/> covers around corner (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    public static IEnumerable<(int X, int Y)> Corners(int cx, int cy, int size)
    {
        int side = TerrainSizes[Math.Clamp(size, 0, TerrainSizes.Length - 1)];
        int x0 = cx - ((side - 1) / 2), y0 = cy - ((side - 1) / 2);
        for (int y = y0; y < y0 + side; y++)
        {
            for (int x = x0; x < x0 + side; x++)
            {
                yield return (x, y);
            }
        }
    }

    /// <summary>The tiles a square brush of size index <paramref name="size"/> covers around tile (<paramref name="tx"/>, <paramref name="ty"/>).</summary>
    public static IEnumerable<(int X, int Y)> Tiles(int tx, int ty, int size) => Corners(tx, ty, size);

    /// <summary>The tiles of a round tree brush of radius index <paramref name="radius"/> (integer disc).</summary>
    public static IEnumerable<(int X, int Y)> Disc(int tx, int ty, int radius)
    {
        int r = TreeRadii[Math.Clamp(radius, 0, TreeRadii.Length - 1)];
        for (int y = -r; y <= r; y++)
        {
            for (int x = -r; x <= r; x++)
            {
                if ((x * x) + (y * y) <= (r * r) + r)
                {
                    yield return (tx + x, ty + y);
                }
            }
        }
    }
}
