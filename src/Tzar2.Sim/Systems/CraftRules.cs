using System;
using Tzar2.Content.Compiled;

namespace Tzar2.Sim.Systems;

/// <summary>
/// The magic workshop's prices and strengths (ITEM-020; ReTzar's reading of the original, D_build 0x4a5220, 0x4743b0,
/// 0x465ca0): what the card shows and what a craft makes.
/// </summary>
public static class CraftRules
{
    /// <summary>The least gold the workshop takes for variant <paramref name="variant"/> of <paramref name="itemType"/>.</summary>
    public static int Minimum(Ruleset rules, ushort itemType, int variant)
    {
        ArgumentNullException.ThrowIfNull(rules);
        int least = rules.Combat.CraftGoldStep;
        if (rules.EntityTypes[itemType].Traits.Item is { Kind: ItemKind.SpellBook } item && BookSpell(item, variant) is >= 0 and var spell)
        {
            SpellDefinition s = rules.Spells[spell];
            least = Math.Max(least, s.BookMinimum > 0 ? s.BookMinimum : 4 * BookRate(rules, spell));
        }

        return least;
    }

    /// <summary>
    /// What <paramref name="gold"/> makes of variant <paramref name="variant"/> of <paramref name="itemType"/> (0x4743b0): a
    /// book's charges, (gold / 4) / its spell's rate; a potion's experience (gold / 5), mana (the gold) or attack and
    /// defence (gold / 2); a sword's, bow's or shield's points (gold / 4, at most 250).
    /// </summary>
    public static int Strength(Ruleset rules, ushort itemType, int variant, int gold)
    {
        ArgumentNullException.ThrowIfNull(rules);
        CombatSettings c = rules.Combat;
        if (rules.EntityTypes[itemType].Traits.Item is not { } item)
        {
            return 0;
        }

        CraftVariant v = item.CraftVariantAt(variant);
        return item.Kind switch
        {
            ItemKind.SpellBook => BookSpell(item, variant) is >= 0 and var spell ? gold / 4 / BookRate(rules, spell) : 0,
            ItemKind.Potion => v.Effect switch
            {
                ItemEffect.Levels => gold / c.CraftExperienceDivisor,
                ItemEffect.Attack or ItemEffect.Defence => gold / c.CraftPotionStatDivisor,
                _ => gold,
            },
            _ => Math.Min(gold / c.CraftEquipmentDivisor, c.CraftEquipmentMax),
        };
    }

    private static int BookSpell(ItemDefinition item, int variant)
    {
        CraftVariant v = item.CraftVariantAt(variant);
        return v.Spell >= 0 ? v.Spell : item.Spell;
    }

    // A book's rate (0x465ca0): its spell's, or a summoning's creature's gold.
    private static int BookRate(Ruleset rules, int spell)
    {
        SpellDefinition s = rules.Spells[spell];
        if (s.BookValue > 0)
        {
            return s.BookValue;
        }

        int gold = rules.ResourceId("gold");
        return s.Summon >= 0 && gold >= 0 && gold < rules.EntityTypes[s.Summon].Traits.Cost.Length
            ? Math.Max(1, rules.EntityTypes[s.Summon].Traits.Cost[gold])
            : 1;
    }
}
