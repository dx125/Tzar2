using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Content.Editing;

/// <summary>
/// State transformations for placed objects (TDD §18.2 "Tools": place, move, copy, delete, properties). Each returns a
/// new <see cref="DocumentState"/>; wrap calls in <see cref="MapDocument.Apply"/> or an <see cref="EditTransaction"/> to
/// make them undoable.
/// </summary>
public static class ObjectEdits
{
    /// <summary>Adds an object with a fresh id.</summary>
    /// <param name="state">The state.</param>
    /// <param name="placement">Type, owner, position.</param>
    /// <param name="info">Scenario properties, or null for none.</param>
    /// <param name="id">The new object's id.</param>
    public static DocumentState Add(this DocumentState state, MapObject placement, ObjectInfo? info, out int id)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrEmpty(placement.TypeKey))
        {
            throw new ArgumentException("An object needs a type.", nameof(placement));
        }

        id = state.NextObjectId;
        return state with
        {
            Objects = state.Objects.Add(new EditorObject(id, placement, info ?? ObjectInfo.Plain)),
            NextObjectId = id + 1,
        };
    }

    /// <summary>
    /// Removes objects. Their trigger references go too (named sets drop them, holders and sources become unset, an area
    /// that followed one becomes the rectangle of its last tile), and so do their memberships in object groups.
    /// </summary>
    public static DocumentState Remove(this DocumentState state, IReadOnlyCollection<int> ids)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ids);
        var gone = new HashSet<int>(ids);
        var tiles = new Dictionary<int, (int X, int Y)>();
        ImmutableList<EditorObject>.Builder kept = ImmutableList.CreateBuilder<EditorObject>();
        foreach (EditorObject o in state.Objects)
        {
            if (gone.Contains(o.Id))
            {
                tiles[o.Id] = (o.TileX, o.TileY);
            }
            else
            {
                kept.Add(o);
            }
        }

        if (tiles.Count == 0)
        {
            return state;
        }

        ScenarioModel scenario = ScenarioRewriter.MapObjects(
            state.Scenario,
            id => gone.Contains(id) ? null : id,
            id => tiles.TryGetValue(id, out (int X, int Y) t) ? t : null);
        var groups = new List<ObjectGroup>();
        foreach (ObjectGroup g in state.Extras.Groups)
        {
            var members = new List<int>();
            foreach (int id in g.ObjectIds)
            {
                if (!gone.Contains(id))
                {
                    members.Add(id);
                }
            }

            groups.Add(g with { ObjectIds = members.ToArray() });
        }

        return state with
        {
            Objects = kept.ToImmutable(),
            Scenario = scenario,
            Extras = state.Extras with { Groups = EquatableArray<ObjectGroup>.From(groups) },
        };
    }

    /// <summary>Replaces object <paramref name="id"/> through <paramref name="change"/> (the id stays).</summary>
    public static DocumentState Update(this DocumentState state, int id, Func<EditorObject, EditorObject> change)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(change);
        int i = state.IndexOf(id);
        if (i < 0)
        {
            throw new ArgumentException($"There is no object {id}.", nameof(id));
        }

        EditorObject next = change(state.Objects[i]) with { Id = id };
        return state with { Objects = state.Objects.SetItem(i, next) };
    }

    /// <summary>Applies <paramref name="change"/> to each of <paramref name="ids"/> (multi-selection edits apply to all).</summary>
    public static DocumentState UpdateAll(this DocumentState state, IEnumerable<int> ids, Func<EditorObject, EditorObject> change)
    {
        ArgumentNullException.ThrowIfNull(ids);
        DocumentState s = state;
        foreach (int id in ids)
        {
            s = s.Update(id, change);
        }

        return s;
    }

    /// <summary>Moves objects by a world-unit offset (Alt-drag); positions are clamped to the map.</summary>
    public static DocumentState MoveBy(this DocumentState state, IEnumerable<int> ids, int dx, int dy, int width, int height)
    {
        int maxX = (width * MapData.UnitsPerTile) - 1, maxY = (height * MapData.UnitsPerTile) - 1;
        return state.UpdateAll(ids, o => o.MovedTo(Math.Clamp(o.X + dx, 0, maxX), Math.Clamp(o.Y + dy, 0, maxY)));
    }

    /// <summary>
    /// Copies objects by a world-unit offset (Ctrl-drag). Copies get new ids and keep their properties; names are kept,
    /// as in the original (persistence matches by name, so the validator warns about Persistent duplicates).
    /// </summary>
    /// <returns>The state and the new ids, in the order of <paramref name="ids"/>.</returns>
    public static DocumentState CopyBy(this DocumentState state, IReadOnlyList<int> ids, int dx, int dy, int width, int height, out ImmutableArray<int> copies)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ids);
        int maxX = (width * MapData.UnitsPerTile) - 1, maxY = (height * MapData.UnitsPerTile) - 1;
        DocumentState s = state;
        var made = ImmutableArray.CreateBuilder<int>(ids.Count);
        foreach (int id in ids)
        {
            if (state.Find(id) is not { } o)
            {
                continue;
            }

            MapObject p = o.Placement with { X = Math.Clamp(o.X + dx, 0, maxX), Y = Math.Clamp(o.Y + dy, 0, maxY) };
            s = s.Add(p, o.Info, out int copy);
            made.Add(copy);
        }

        copies = made.ToImmutable();
        return s;
    }

    /// <summary>Replaces every trigger reference to object <paramref name="from"/> with <paramref name="to"/> (search and replace, §18.3).</summary>
    /// <returns>The state and the number of references replaced.</returns>
    public static DocumentState ReplaceReferences(this DocumentState state, int from, int to, out int replaced)
    {
        ArgumentNullException.ThrowIfNull(state);
        int count = 0;
        ScenarioModel scenario = ScenarioRewriter.MapObjects(state.Scenario, id =>
        {
            if (id == from)
            {
                count++;
                return to;
            }

            return id;
        });
        replaced = count;
        return count == 0 ? state : state with { Scenario = scenario };
    }

    /// <summary>The trigger references to object <paramref name="id"/> ("referenced by").</summary>
    public static ImmutableArray<ObjectReference> ReferencesTo(this DocumentState state, int id)
    {
        ArgumentNullException.ThrowIfNull(state);
        var b = ImmutableArray.CreateBuilder<ObjectReference>();
        foreach (ObjectReference r in ScenarioRewriter.References(state.Scenario))
        {
            if (r.Object == id)
            {
                b.Add(r);
            }
        }

        return b.ToImmutable();
    }
}
