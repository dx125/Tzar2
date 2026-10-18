using System;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;

namespace Tzar2.Content.Editing;

/// <summary>
/// A placed object of a <see cref="MapDocument"/>: its placement as the sim consumes it (<see cref="MapObject"/>), its
/// scenario properties (<see cref="ObjectInfo"/>) and a stable <see cref="Id"/> (ADR 0150). Inside a document every
/// object reference of the trigger program is such an id, not an index; <see cref="MapDocument.BuildScenario"/> maps ids
/// to the indices of <see cref="MapData.Objects"/>.
/// </summary>
/// <param name="Id">Stable id, unique in the document, ≥ 1.</param>
/// <param name="Placement">Type, owner, position (world units).</param>
/// <param name="Info">Scenario properties (name, title, flags, skill overrides, item properties).</param>
public sealed record EditorObject(int Id, MapObject Placement, ObjectInfo Info)
{
    /// <summary>Entity type key.</summary>
    public string TypeKey => Placement.TypeKey;

    /// <summary>Owner slot, or <see cref="MapObject.Nature"/>.</summary>
    public byte Owner => Placement.Owner;

    /// <summary>X in world units.</summary>
    public int X => Placement.X;

    /// <summary>Y in world units.</summary>
    public int Y => Placement.Y;

    /// <summary>Tile column of <see cref="X"/>.</summary>
    public int TileX => Placement.X / MapData.UnitsPerTile;

    /// <summary>Tile row of <see cref="Y"/>.</summary>
    public int TileY => Placement.Y / MapData.UnitsPerTile;

    /// <summary>A copy at another position.</summary>
    public EditorObject MovedTo(int x, int y) => this with { Placement = Placement with { X = x, Y = y } };

    /// <summary>Display label: title, else name, else the type key.</summary>
    public string Label => Info.Title.Length > 0 ? Info.Title : Info.Name.Length > 0 ? Info.Name : TypeKey;

    /// <summary>Whether <paramref name="text"/> occurs in the name, title or type key (ordinal, ignoring case).</summary>
    public bool Matches(string text)
        => text.Length == 0
            || Info.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || Info.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
            || TypeKey.Contains(text, StringComparison.OrdinalIgnoreCase);
}
