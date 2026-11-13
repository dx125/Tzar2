using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Content.Editing;

/// <summary>How one half of the map is copied onto the other (ADR 0292).</summary>
public enum MirrorKind : byte
{
    /// <summary>The left half, mirrored onto the right.</summary>
    LeftToRight,

    /// <summary>The top half, mirrored onto the bottom.</summary>
    TopToBottom,

    /// <summary>The left half, turned half round onto the right (a start in the top left faces one in the bottom right).</summary>
    LeftTurned,
}

/// <summary>
/// Builds a fair multiplayer map from one half (ADR 0292): the half's terrain (painted corners, heights, cliffs), its
/// decorations and objects are copied onto the other half mirrored or turned, and the kingdoms of the copy are their
/// partners' (1 and 2, 3 and 4, 5 and 6, 7 and 8 change places; Nature stays), starting locations included. What stood
/// on the other half goes first. One undoable step; an object whose copy does not fit is left out.
/// </summary>
public static class MapMirror
{
    /// <summary>The kingdom slot (0-7) a copied object of <paramref name="owner"/> belongs to; Nature stays Nature.</summary>
    public static byte Partner(byte owner) => owner == MapObject.Nature || owner > 7 ? owner : (byte)(owner ^ 1);

    /// <summary>Copies one half of <paramref name="document"/> onto the other; returns how many objects were copied.</summary>
    public static int Apply(MapDocument document, Ruleset ruleset, MirrorKind kind)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ruleset);
        int w = document.Width, h = document.Height;
        const int tile = MapData.UnitsPerTile;

        // Corners and tiles of the target half take the source's values.
        (int X, int Y) Corner(int x, int y) => kind switch { MirrorKind.LeftToRight => (w - x, y), MirrorKind.TopToBottom => (x, h - y), _ => (w - x, h - y) };
        (int X, int Y) Tile(int x, int y) => kind switch { MirrorKind.LeftToRight => (w - 1 - x, y), MirrorKind.TopToBottom => (x, h - 1 - y), _ => (w - 1 - x, h - 1 - y) };
        bool Target(int x, int y, int half) => kind == MirrorKind.TopToBottom ? y > half : x > half;

        using EditTransaction tx = document.Begin(kind switch { MirrorKind.LeftToRight => "Mirror the left half", MirrorKind.TopToBottom => "Mirror the top half", _ => "Turn the left half round" });
        ReadOnlySpan<byte> painted = document.PaintedCorners;
        ReadOnlySpan<byte> heights = document.HeightCorners;
        byte[] paintedCopy = painted.ToArray(), heightCopy = heights.ToArray();
        int stride = document.Stride;
        int cornerHalf = (kind == MirrorKind.TopToBottom ? h : w) / 2;
        for (int y = 0; y <= h; y++)
        {
            for (int x = 0; x <= w; x++)
            {
                if (Target(x, y, cornerHalf))
                {
                    (int sx, int sy) = Corner(x, y);
                    tx.Paint(x, y, paintedCopy[(sy * stride) + sx]);
                    tx.SetHeight(x, y, heightCopy[(sy * stride) + sx]);
                }
            }
        }

        byte[] cliffs = document.CliffTiles.ToArray();
        int tileHalf = ((kind == MirrorKind.TopToBottom ? h : w) - 1) / 2;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (Target(x, y, tileHalf))
                {
                    (int sx, int sy) = Tile(x, y);
                    tx.SetCliff(x, y, cliffs[(sy * w) + sx] != 0);
                }
            }
        }

        // The other half's objects and decorations go; the source half's are copied.
        DocumentState state = tx.State;
        bool InTarget(int px, int py) => kind == MirrorKind.TopToBottom ? py * 2 >= h * tile : px * 2 >= w * tile;
        var gone = new List<int>();
        var sources = new List<EditorObject>();
        foreach (EditorObject o in state.Objects)
        {
            (int cx, int cy) = Centre(ruleset, o.Placement);
            if (InTarget(cx, cy))
            {
                gone.Add(o.Id);
            }
            else
            {
                sources.Add(o);
            }
        }

        state = state.Remove(gone);
        var decorations = state.Decorations.RemoveAll(d => kind == MirrorKind.TopToBottom ? d.TileY * 2 >= h : d.TileX * 2 >= w);
        foreach (MapDecoration d in decorations.ToArray())
        {
            (int tx2, int ty2) = Tile(d.TileX, d.TileY);
            decorations = decorations.Add(d with { TileX = tx2, TileY = ty2 });
        }

        state = state with { Decorations = decorations };
        tx.State = state;
        var occupancy = new Occupancy(document, ruleset);
        int copied = 0;
        foreach (EditorObject o in sources)
        {
            MapObject p = o.Placement;
            MapObject copy;
            if (ruleset.EntityTypeId(p.TypeKey) is >= 0 and var t && ruleset.EntityTypes[t].HasFootprint)
            {
                (int fx, int fy, int fw, int fh) = Occupancy.Footprint(ruleset.EntityTypes[t], p);
                int nx = kind == MirrorKind.TopToBottom ? fx : w - fx - fw;
                int ny = kind == MirrorKind.LeftToRight ? fy : h - fy - fh;
                copy = p with { X = (nx * tile) + (p.X - (fx * tile)), Y = (ny * tile) + (p.Y - (fy * tile)) };
            }
            else
            {
                copy = p with
                {
                    X = kind == MirrorKind.TopToBottom ? p.X : Math.Clamp((w * tile) - 1 - p.X, 0, (w * tile) - 1),
                    Y = kind == MirrorKind.LeftToRight ? p.Y : Math.Clamp((h * tile) - 1 - p.Y, 0, (h * tile) - 1),
                };
            }

            copy = copy with { Owner = Partner(p.Owner) };
            if (!occupancy.Check(copy).Ok)
            {
                continue;
            }

            tx.State = tx.State.Add(copy, o.Info, out int id);
            occupancy.Add(id, copy);
            copied++;
        }

        // The partners' starting locations follow.
        ScenarioModel model = tx.State.Scenario;
        foreach (KingdomDef k in model.Kingdoms)
        {
            int partner = Partner((byte)(k.Number - 1)) + 1;
            if (k.StartTileX >= 0 && !InTarget((k.StartTileX * tile) + (tile / 2), (k.StartTileY * tile) + (tile / 2)))
            {
                (int sx, int sy) = Tile(k.StartTileX, k.StartTileY);
                model = model.SetStart(partner, sx, sy);
            }
        }

        tx.State = tx.State.WithScenario(_ => model);
        tx.Commit();
        return copied;
    }

    // An object's centre in world units (a structure's footprint centre, else its position).
    private static (int X, int Y) Centre(Ruleset ruleset, MapObject p)
    {
        if (ruleset.EntityTypeId(p.TypeKey) is >= 0 and var t && ruleset.EntityTypes[t].HasFootprint)
        {
            (int fx, int fy, int fw, int fh) = Occupancy.Footprint(ruleset.EntityTypes[t], p);
            return (((fx * 2) + fw) * MapData.UnitsPerTile / 2, ((fy * 2) + fh) * MapData.UnitsPerTile / 2);
        }

        return (p.X, p.Y);
    }
}
