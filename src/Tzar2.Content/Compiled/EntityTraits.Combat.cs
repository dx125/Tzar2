using System.Collections.Immutable;

namespace Tzar2.Content.Compiled;

/// <summary>The traits of GAME-07…11 (combat, experience, holders, items, magic; ADRs 0090-0095).</summary>
public sealed partial record EntityTraits
{
    /// <summary>Carries units (castles, outposts, ships, carpets, caravans), or null.</summary>
    public HolderDefinition? Holder { get; init; }

    /// <summary>Item slots of a unit's inventory (<c>max_obj_in_holder</c> of soldiers); 0 = carries no items.</summary>
    public int InventorySlots { get; init; }

    /// <summary>The type is an item, or null.</summary>
    public ItemDefinition? Item { get; init; }

    /// <summary>Spell ids the type can cast (its command card's <c>spell</c> orders), ascending.</summary>
    public ImmutableArray<ushort> Spells { get; init; } = [];

    /// <summary>Whether the unit gains experience in combat (GAME-08).</summary>
    public bool GainsExperience { get; init; }

    /// <summary>Special abilities.</summary>
    public AbilityFlags Abilities { get; init; }

    /// <summary>Made from another unit in place (Jihad warriors from peasants), or null.</summary>
    public ConversionDefinition? ConvertsFrom { get; init; }

    /// <summary>Crafts items (Magic Workshop), or null.</summary>
    public CrafterDefinition? Crafter { get; init; }

    /// <summary>Whether the type can cast <paramref name="spell"/>.</summary>
    public bool CanCast(ushort spell) => !Spells.IsDefaultOrEmpty && Spells.BinarySearch(spell) >= 0;
}
