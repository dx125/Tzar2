using System.Collections.Immutable;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Content.Editing;

/// <summary>A drawn, not simulated decoration (ground patch, ground object, animation; <c>decorations.json</c>, ADR 0121).</summary>
/// <param name="Class">Legacy class (<c>ground_patch</c>, <c>ground_object</c>, <c>animation</c>…).</param>
/// <param name="Image">Image or draw-definition name.</param>
/// <param name="TileX">Tile column.</param>
/// <param name="TileY">Tile row.</param>
/// <param name="PixelX">Pixel x inside the map, or −1.</param>
/// <param name="PixelY">Pixel y inside the map, or −1.</param>
public sealed record MapDecoration(string Class, string Image, int TileX, int TileY, int PixelX, int PixelY);

/// <summary>A named rectangle reusable across triggers (TDD §18.3).</summary>
/// <param name="Name">Name.</param>
/// <param name="Area">The tile rectangle.</param>
public sealed record NamedArea(string Name, RectArea Area);

/// <summary>A named set of objects reusable across triggers (TDD §18.3).</summary>
/// <param name="Name">Name.</param>
/// <param name="ObjectIds">Object ids.</param>
public sealed record ObjectGroup(string Name, EquatableArray<int> ObjectIds);

/// <summary>Editor-only data that the game never reads (<c>editor.json</c>).</summary>
/// <param name="Areas">Named areas.</param>
/// <param name="Groups">Object groups.</param>
/// <param name="Language">Source language of the map's texts (<c>strings/&lt;lang&gt;.po</c>).</param>
public sealed record EditorExtras(EquatableArray<NamedArea> Areas, EquatableArray<ObjectGroup> Groups, string Language)
{
    /// <summary>Nothing set, English texts.</summary>
    public static EditorExtras Default { get; } = new(EquatableArray<NamedArea>.Empty, EquatableArray<ObjectGroup>.Empty, "en");
}

/// <summary>
/// The undoable, immutable part of a <see cref="MapDocument"/> besides the terrain layers (ADR 0150): every edit makes a
/// new state, and the history keeps the states before and after. Records and immutable collections share structure, so a
/// state costs little more than the parts that changed.
/// </summary>
/// <param name="Name">Map name (<see cref="Compiled.MapData.Name"/>).</param>
/// <param name="Objects">Placed objects in placement order.</param>
/// <param name="NextObjectId">The id the next new object gets.</param>
/// <param name="Scenario">Kingdoms, properties and the trigger program (object references are ids).</param>
/// <param name="Decorations">Decorations.</param>
/// <param name="Assets">User resources: path under <c>assets/</c> → bytes (images, sounds, music, custom mini-map/title).</param>
/// <param name="Extras">Named areas, object groups, language.</param>
public sealed record DocumentState(
    string Name,
    ImmutableList<EditorObject> Objects,
    int NextObjectId,
    ScenarioModel Scenario,
    ImmutableList<MapDecoration> Decorations,
    ImmutableSortedDictionary<string, ImmutableArray<byte>> Assets,
    EditorExtras Extras)
{
    /// <summary>Lua scripts (TDD §18.4 <c>scripts/*.lua</c>): file name → source text.</summary>
    public ImmutableSortedDictionary<string, string> Scripts { get; init; } = ImmutableSortedDictionary.Create<string, string>(System.StringComparer.Ordinal);

    /// <summary>The object with id <paramref name="id"/>, or null.</summary>
    public EditorObject? Find(int id)
    {
        int i = IndexOf(id);
        return i < 0 ? null : Objects[i];
    }

    /// <summary>Index of the object with id <paramref name="id"/> in <see cref="Objects"/>, or −1.</summary>
    public int IndexOf(int id)
    {
        if (_index is null)
        {
            var index = new System.Collections.Generic.Dictionary<int, int>(Objects.Count);
            int i = 0;
            foreach (EditorObject o in Objects)
            {
                index[o.Id] = i++;
            }

            _index = index;
        }

        return _index.TryGetValue(id, out int at) ? at : -1;
    }

    // A copy made by "with" must not inherit the id index of the original.
    private DocumentState(DocumentState original)
    {
        Name = original.Name;
        Objects = original.Objects;
        NextObjectId = original.NextObjectId;
        Scenario = original.Scenario;
        Decorations = original.Decorations;
        Assets = original.Assets;
        Extras = original.Extras;
        Scripts = original.Scripts;
    }

    private System.Collections.Generic.Dictionary<int, int>? _index;

    /// <summary>
    /// Equality over the state's parts (the id index is a cache, not part of the value). The collection parts compare by
    /// reference (immutable collections shared through <c>with</c>), the fast path of "did this edit change anything";
    /// two equal lists built separately compare unequal, which at worst records one redundant undo step.
    /// </summary>
    public bool Equals(DocumentState? other)
        => other is not null && (ReferenceEquals(this, other) || (Name == other.Name && Objects.Equals(other.Objects) && NextObjectId == other.NextObjectId
            && Scenario.Equals(other.Scenario) && Decorations.Equals(other.Decorations) && Assets.Equals(other.Assets) && Extras.Equals(other.Extras) && Scripts.Equals(other.Scripts)));

    /// <inheritdoc/>
    public override int GetHashCode() => Objects.Count ^ (NextObjectId << 8);
}
