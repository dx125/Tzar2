using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;

namespace Tzar2.Content.Editing;

/// <summary>Palette sections of the editor (TDD §18.2: buildings, units, items, resources, decorations).</summary>
public enum PaletteKind : byte
{
    /// <summary>Buildings (walls, gates and bridges included).</summary>
    Building,

    /// <summary>Units of the civilizations.</summary>
    Unit,

    /// <summary>Animals and creatures (common units; Nature owns them by default).</summary>
    Creature,

    /// <summary>Trees.</summary>
    Tree,

    /// <summary>Other resources: rocks, fish patches.</summary>
    Resource,

    /// <summary>Items.</summary>
    Item,

    /// <summary>Everything else (special locations, ground objects as entities).</summary>
    Other,
}

/// <summary>One placeable entity type.</summary>
/// <param name="Key">Entity type key.</param>
/// <param name="Kind">Palette section.</param>
/// <param name="Civilization">Civilization key (<c>comm</c> = common).</param>
/// <param name="LegacyClass">Legacy class name, or empty.</param>
/// <param name="Group">Group for "random within a group" (the class family: the key without a trailing number or <c>_suffix</c>).</param>
public sealed record PaletteEntry(string Key, PaletteKind Kind, string Civilization, string LegacyClass, string Group)
{
    /// <summary>Whether Nature owns new objects of this entry by default (trees, rocks, animals).</summary>
    public bool NatureByDefault => Kind is PaletteKind.Tree or PaletteKind.Resource or PaletteKind.Creature or PaletteKind.Other or PaletteKind.Item;
}

/// <summary>
/// The placeable types of a ruleset, by section and civilization. It is built from the compiled ruleset, so units and
/// buildings added by enabled mods appear without editor changes (TDD §18.3 "mod-aware palettes").
/// </summary>
public sealed class EditorPalette
{
    private EditorPalette(ImmutableArray<PaletteEntry> entries) => Entries = entries;

    /// <summary>All entries, ordered by section, civilization and key.</summary>
    public ImmutableArray<PaletteEntry> Entries { get; }

    /// <summary>Builds the palette of <paramref name="ruleset"/>.</summary>
    public static EditorPalette Build(Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        int wood = ruleset.ResourceId("wood");
        var produced = new HashSet<ushort>();
        foreach (EntityTypeDefinition t in ruleset.EntityTypes)
        {
            if (t.Traits.Producer is { } p)
            {
                foreach (ushort id in p.Produces)
                {
                    produced.Add(id);
                }
            }
        }

        var list = new List<PaletteEntry>();
        foreach (EntityTypeDefinition t in ruleset.EntityTypes)
        {
            EntityTraits tr = t.Traits;
            string civ = tr.Civilization.Length > 0 ? tr.Civilization : CivOf(t.Key);
            PaletteKind kind = tr.Category switch
            {
                EntityCategory.Building => PaletteKind.Building,
                EntityCategory.Unit => civ == "comm" && !produced.Contains(t.Id) ? PaletteKind.Creature : PaletteKind.Unit,
                EntityCategory.Resource when tr.ResourceNode is { } n && n.Resource == wood && t.HasFootprint => PaletteKind.Tree,
                EntityCategory.Resource => PaletteKind.Resource,
                _ when tr.Item is not null => PaletteKind.Item,
                _ => PaletteKind.Other,
            };
            if (tr.Item is not null)
            {
                kind = PaletteKind.Item;
            }

            list.Add(new PaletteEntry(t.Key, kind, civ, tr.LegacyClass, GroupOf(t.Key)));
        }

        list.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind)
            : !string.Equals(a.Civilization, b.Civilization, StringComparison.Ordinal) ? string.CompareOrdinal(a.Civilization, b.Civilization)
            : string.CompareOrdinal(a.Key, b.Key));
        return new EditorPalette(list.ToImmutableArray());
    }

    /// <summary>Entries of one section (and civilization, if given).</summary>
    public ImmutableArray<PaletteEntry> Of(PaletteKind kind, string? civilization = null)
    {
        var b = ImmutableArray.CreateBuilder<PaletteEntry>();
        foreach (PaletteEntry e in Entries)
        {
            if (e.Kind == kind && (civilization is null || string.Equals(e.Civilization, civilization, StringComparison.Ordinal)))
            {
                b.Add(e);
            }
        }

        return b.ToImmutable();
    }

    /// <summary>The entry of <paramref name="key"/>, or null.</summary>
    public PaletteEntry? Find(string key)
    {
        foreach (PaletteEntry e in Entries)
        {
            if (string.Equals(e.Key, key, StringComparison.Ordinal))
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>Entries of the same section and group as <paramref name="entry"/> ("random within a group").</summary>
    public ImmutableArray<PaletteEntry> Siblings(PaletteEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var b = ImmutableArray.CreateBuilder<PaletteEntry>();
        foreach (PaletteEntry e in Entries)
        {
            if (e.Kind == entry.Kind && string.Equals(e.Group, entry.Group, StringComparison.Ordinal))
            {
                b.Add(e);
            }
        }

        return b.ToImmutable();
    }

    /// <summary>Civilization part of a key (<c>euro.knight</c> → <c>euro</c>).</summary>
    public static string CivOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        int dot = key.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? key[..dot] : "comm";
    }

    /// <summary>The family of a key: the class without trailing digits or a last <c>_part</c> (<c>comm.item_potion2</c> → <c>comm.item</c>).</summary>
    public static string GroupOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string s = key.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        int dot = s.IndexOf('.', StringComparison.Ordinal);
        int underscore = s.IndexOf('_', dot + 1);
        return underscore > dot + 1 ? s[..underscore] : s;
    }
}
