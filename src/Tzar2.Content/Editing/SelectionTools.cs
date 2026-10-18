using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Content.Editing;

/// <summary>Filters of the Object Locator (TDD §18.2 "Tools").</summary>
/// <param name="TypeKey">Only this type (null = any).</param>
/// <param name="Owner">Only this owner slot or <see cref="MapObject.Nature"/> (null = any).</param>
/// <param name="NamedOnly">Only objects with a name.</param>
/// <param name="Text">Text in the name, title or type key (empty = any).</param>
public sealed record LocatorFilter(string? TypeKey = null, byte? Owner = null, bool NamedOnly = false, string Text = "");

/// <summary>The Object Locator: lists objects by filter; the editor then views, edits or deletes them.</summary>
public static class ObjectLocator
{
    /// <summary>The objects matching <paramref name="filter"/>, in placement order.</summary>
    public static ImmutableArray<EditorObject> Find(DocumentState state, LocatorFilter filter)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(filter);
        var b = ImmutableArray.CreateBuilder<EditorObject>();
        foreach (EditorObject o in state.Objects)
        {
            if ((filter.TypeKey is null || string.Equals(o.TypeKey, filter.TypeKey, StringComparison.Ordinal))
                && (filter.Owner is null || o.Owner == filter.Owner)
                && (!filter.NamedOnly || o.Info.Name.Length > 0)
                && o.Matches(filter.Text))
            {
                b.Add(o);
            }
        }

        return b.ToImmutable();
    }

    /// <summary>The objects whose tile lies in the inclusive tile rectangle (rubber-band selection).</summary>
    public static ImmutableArray<int> InRect(DocumentState state, int left, int top, int right, int bottom)
    {
        ArgumentNullException.ThrowIfNull(state);
        var b = ImmutableArray.CreateBuilder<int>();
        foreach (EditorObject o in state.Objects)
        {
            if (o.TileX >= Math.Min(left, right) && o.TileX <= Math.Max(left, right) && o.TileY >= Math.Min(top, bottom) && o.TileY <= Math.Max(top, bottom))
            {
                b.Add(o.Id);
            }
        }

        return b.ToImmutable();
    }
}

/// <summary>Editor layers that can be hidden or locked (TDD §18.3 "layers with lock/hide").</summary>
public enum EditorLayer : byte
{
    /// <summary>Terrain and cliffs.</summary>
    Terrain,

    /// <summary>Trees.</summary>
    Trees,

    /// <summary>Rocks, fish patches and other resources.</summary>
    Resources,

    /// <summary>Decorations (ground patches and objects).</summary>
    Decorations,

    /// <summary>Buildings.</summary>
    Buildings,

    /// <summary>Units and creatures.</summary>
    Units,

    /// <summary>Items.</summary>
    Items,

    /// <summary>Other objects.</summary>
    Other,

    /// <summary>Starting locations, named areas, sound sources.</summary>
    Markers,
}

/// <summary>Visibility and lock state of the editor layers (view state; saved with the editor settings, not undoable).</summary>
public sealed class LayerSettings
{
    private readonly bool[] _hidden = new bool[9];
    private readonly bool[] _locked = new bool[9];

    /// <summary>Raised when a layer is hidden, shown, locked or unlocked.</summary>
    public event Action? Changed;

    /// <summary>Whether a layer is hidden.</summary>
    public bool IsHidden(EditorLayer layer) => _hidden[(int)layer];

    /// <summary>Whether a layer is locked (its objects cannot be selected or edited).</summary>
    public bool IsLocked(EditorLayer layer) => _locked[(int)layer];

    /// <summary>Hides or shows a layer.</summary>
    public void SetHidden(EditorLayer layer, bool hidden)
    {
        _hidden[(int)layer] = hidden;
        Changed?.Invoke();
    }

    /// <summary>Locks or unlocks a layer.</summary>
    public void SetLocked(EditorLayer layer, bool locked)
    {
        _locked[(int)layer] = locked;
        Changed?.Invoke();
    }

    /// <summary>The layer of an object under <paramref name="palette"/>.</summary>
    public static EditorLayer LayerOf(EditorObject o, EditorPalette palette)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(palette);
        return palette.Find(o.TypeKey)?.Kind switch
        {
            PaletteKind.Tree => EditorLayer.Trees,
            PaletteKind.Resource => EditorLayer.Resources,
            PaletteKind.Building => EditorLayer.Buildings,
            PaletteKind.Unit or PaletteKind.Creature => EditorLayer.Units,
            PaletteKind.Item => EditorLayer.Items,
            _ => EditorLayer.Other,
        };
    }

    /// <summary>Whether an object may be selected (its layer is neither hidden nor locked).</summary>
    public bool IsEditable(EditorObject o, EditorPalette palette)
    {
        EditorLayer layer = LayerOf(o, palette);
        return !IsHidden(layer) && !IsLocked(layer);
    }

    /// <summary>The ids among <paramref name="ids"/> that may be edited.</summary>
    public ImmutableArray<int> Editable(DocumentState state, EditorPalette palette, IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ids);
        var b = ImmutableArray.CreateBuilder<int>();
        foreach (int id in ids)
        {
            if (state.Find(id) is { } o && IsEditable(o, palette))
            {
                b.Add(id);
            }
        }

        return b.ToImmutable();
    }
}

/// <summary>Multi-select transforms (TDD §18.3): turn by 90° or mirror the selection about its centre tile.</summary>
public static class Transforms
{
    /// <summary>The transforms.</summary>
    public enum Kind : byte
    {
        /// <summary>Quarter turn clockwise.</summary>
        RotateClockwise,

        /// <summary>Quarter turn counter-clockwise.</summary>
        RotateCounterClockwise,

        /// <summary>Mirror left/right.</summary>
        MirrorHorizontal,

        /// <summary>Mirror top/bottom.</summary>
        MirrorVertical,
    }

    /// <summary>Applies a transform to the positions of <paramref name="ids"/> (tile-exact; positions are clamped to the map).</summary>
    public static DocumentState Apply(DocumentState state, IReadOnlyList<int> ids, Kind kind, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return state;
        }

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (int id in ids)
        {
            if (state.Find(id) is { } o)
            {
                minX = Math.Min(minX, o.X);
                minY = Math.Min(minY, o.Y);
                maxX = Math.Max(maxX, o.X);
                maxY = Math.Max(maxY, o.Y);
            }
        }

        if (minX == int.MaxValue)
        {
            return state;
        }

        // Twice the centre, so the arithmetic stays in integers.
        int cx2 = minX + maxX, cy2 = minY + maxY;
        int limitX = (width * MapData.UnitsPerTile) - 1, limitY = (height * MapData.UnitsPerTile) - 1;
        return state.UpdateAll(ids, o =>
        {
            int dx2 = (2 * o.X) - cx2, dy2 = (2 * o.Y) - cy2;
            (int nx2, int ny2, byte facing) = kind switch
            {
                Kind.RotateClockwise => (-dy2, dx2, (byte)((o.Placement.Facing + 2) & 7)),
                Kind.RotateCounterClockwise => (dy2, -dx2, (byte)((o.Placement.Facing + 6) & 7)),
                Kind.MirrorHorizontal => (-dx2, dy2, (byte)((12 - o.Placement.Facing) & 7)),
                _ => (dx2, -dy2, (byte)((8 - o.Placement.Facing) & 7)),
            };
            int x = Math.Clamp((cx2 + nx2) / 2, 0, limitX), y = Math.Clamp((cy2 + ny2) / 2, 0, limitY);
            MapObjectFlags flags = o.Placement.Flags;
            if (kind is Kind.RotateClockwise or Kind.RotateCounterClockwise)
            {
                flags ^= MapObjectFlags.Rotated;
            }

            return o with { Placement = o.Placement with { X = x, Y = y, Facing = facing, Flags = flags } };
        });
    }
}

/// <summary>
/// Copy/paste and prefabs across maps (TDD §18.3): a selection serialised as JSON with positions relative to its first
/// object's tile, its properties and placement extras (no trigger references: those belong to the source map).
/// </summary>
public static class ObjectClipboard
{
    /// <summary>Format version.</summary>
    public const int Format = 1;

    /// <summary>The selection as clipboard text (also the format of prefab files).</summary>
    public static string Copy(DocumentState state, IReadOnlyList<int> ids, string name = "")
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ids);
        var objects = new List<EditorObject>();
        foreach (int id in ids)
        {
            if (state.Find(id) is { } o)
            {
                objects.Add(o);
            }
        }

        int ox = objects.Count > 0 ? objects[0].TileX * MapData.UnitsPerTile : 0, oy = objects.Count > 0 ? objects[0].TileY * MapData.UnitsPerTile : 0;
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("kind", "tzar2.objects");
            w.WriteNumber("format", Format);
            w.WriteString("name", name);
            w.WriteStartArray("objects");
            foreach (EditorObject o in objects)
            {
                w.WriteStartObject();
                w.WriteString("type", o.TypeKey);
                w.WriteNumber("owner", o.Owner);
                w.WriteNumber("dx", o.X - ox);
                w.WriteNumber("dy", o.Y - oy);
                w.WriteNumber("flags", (int)o.Placement.Flags);
                w.WriteNumber("facing", o.Placement.Facing);
                w.WriteNumber("amount", o.Placement.Amount);
                w.WriteString("name", o.Info.Name);
                w.WriteString("title", o.Info.Title);
                w.WriteNumber("objectFlags", (int)o.Info.Flags);
                Optional(w, "health", o.Info.Health);
                Optional(w, "maxHealth", o.Info.MaxHealth);
                Optional(w, "attack", o.Info.Attack);
                Optional(w, "defence", o.Info.Defence);
                Optional(w, "mana", o.Info.Mana);
                Optional(w, "level", o.Info.Level);
                Optional(w, "resistance", o.Info.Resistance);
                if (o.Info.Item is { } item)
                {
                    w.WriteString("item", item.Definition);
                    Optional(w, "itemValue", item.Value);
                    Optional(w, "itemAmount", item.Amount);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Pastes clipboard text with its first object at tile (<paramref name="tileX"/>, <paramref name="tileY"/>). Objects
    /// whose placement is refused (off the map, or by <paramref name="occupancy"/> when given) are skipped.
    /// </summary>
    /// <returns>The state and the new ids.</returns>
    /// <exception cref="FormatException">The text is not a Tzar2 object clipboard.</exception>
    public static DocumentState Paste(DocumentState state, string text, int tileX, int tileY, int width, int height, Occupancy? occupancy, out ImmutableArray<int> pasted)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(text);
        var made = ImmutableArray.CreateBuilder<int>();
        try
        {
            using JsonDocument json = JsonDocument.Parse(text);
            JsonElement root = json.RootElement;
            if (root.GetProperty("kind").GetString() != "tzar2.objects" || root.GetProperty("format").GetInt32() != Format)
            {
                throw new FormatException("Not a Tzar2 object clipboard.");
            }

            int ox = tileX * MapData.UnitsPerTile, oy = tileY * MapData.UnitsPerTile;
            foreach (JsonElement e in root.GetProperty("objects").EnumerateArray())
            {
                int x = ox + e.GetProperty("dx").GetInt32(), y = oy + e.GetProperty("dy").GetInt32();
                if (x < 0 || y < 0 || x >= width * MapData.UnitsPerTile || y >= height * MapData.UnitsPerTile)
                {
                    continue;
                }

                var placement = new MapObject(e.GetProperty("type").GetString() ?? string.Empty, (byte)e.GetProperty("owner").GetInt32(), x, y)
                {
                    Flags = (MapObjectFlags)(e.GetProperty("flags").GetInt32() & 7),
                    Facing = (byte)(e.GetProperty("facing").GetInt32() & 7),
                    Amount = Math.Max(0, e.GetProperty("amount").GetInt32()),
                };
                if (placement.TypeKey.Length == 0 || (occupancy is not null && !occupancy.Check(placement).Ok))
                {
                    continue;
                }

                ItemInfo? item = e.TryGetProperty("item", out JsonElement def)
                    ? new ItemInfo(def.GetString() ?? string.Empty, Opt(e, "itemValue"), Opt(e, "itemAmount"))
                    : null;
                var info = new ObjectInfo(
                    e.GetProperty("name").GetString() ?? string.Empty,
                    e.GetProperty("title").GetString() ?? string.Empty,
                    (ObjectFlags)(e.GetProperty("objectFlags").GetInt32() & 15),
                    Opt(e, "health"),
                    Opt(e, "maxHealth"),
                    Opt(e, "attack"),
                    Opt(e, "defence"),
                    Opt(e, "mana"),
                    Opt(e, "level"),
                    Opt(e, "resistance"),
                    Item: item);
                state = state.Add(placement, info, out int id);
                occupancy?.Add(id, placement);
                made.Add(id);
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("Not a Tzar2 object clipboard: " + e.Message, e);
        }

        pasted = made.ToImmutable();
        return state;
    }

    private static void Optional(Utf8JsonWriter w, string name, int? value)
    {
        if (value is int v)
        {
            w.WriteNumber(name, v);
        }
    }

    private static int? Opt(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) ? v.GetInt32() : null;
}
