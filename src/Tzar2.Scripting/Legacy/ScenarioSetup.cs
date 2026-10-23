using System;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Components;

namespace Tzar2.Scripting.Legacy;

/// <summary>
/// The scenario properties of placed objects (Appendix C.2; ADR 0130): name, and the "use default" overrides of health,
/// max health, attack, defence, mana, level and resistance, and an item's own properties (a book's pool and charges,
/// ADR 0265). Applied at match start and when a Not Appear object appears.
/// </summary>
public static class ScenarioSetup
{
    /// <summary>Gives a live entity its object's properties. Values are clamped to the sim's ranges.</summary>
    public static void ApplyInfo(World world, EntityId entity, ObjectInfo info)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(info);
        if (!world.Entities.IsAlive(entity))
        {
            return;
        }

        if (info.Name.Length > 0)
        {
            world.SetCold(entity, new EntityName(info.Name));
        }

        if (info.Level is { } level && world.Experiences.Has(entity))
        {
            world.Experiences.Get(entity).Level = (ushort)Math.Clamp(level, 0, 10_000);
            world.RefreshMaxHealth(entity);
            ref Health full = ref world.Healths.Get(entity);
            full.Current = full.Max;
        }

        if (world.Healths.Has(entity))
        {
            ref Health h = ref world.Healths.Get(entity);
            if (info.MaxHealth is { } max)
            {
                h.Max = Math.Clamp(max, 1, 1_000_000);
                h.Current = h.Max;
            }

            if (info.Health is { } health)
            {
                h.Current = Math.Clamp(health, 1, h.Max);
            }
        }

        // Attack and defence overrides are absolute values: the difference to the type's value becomes a permanent
        // bonus (UNVERIFIED: the original may replace the base value instead, UNK-58). Types without experience keep theirs.
        if (world.Experiences.Has(entity))
        {
            ref Experience x = ref world.Experiences.Get(entity);
            if (info.Attack is { } attack)
            {
                x.BonusAttack = Math.Clamp(x.BonusAttack + (attack - Sim.Systems.GameplayQueries.AttackOf(world, entity)), 0, 100_000);
            }

            if (info.Defence is { } defence)
            {
                x.BonusDefence = Math.Clamp(x.BonusDefence + (defence - Sim.Systems.GameplayQueries.DefenceOf(world, entity)), 0, 100_000);
            }
        }

        // Item properties (Appendix C.2 "Items"; ADR 0186): a power item's attack or defence and a potion's one effect
        // come from the object's own stats, a book's spell from its magic type, the look from the item's name; else the
        // magnitude (resource amount) and a spell book's charges. Scroll texts are the presenter's (UNK-67).
        if (info.Item is { } item && world.Items.Has(entity))
        {
            ref ItemState state = ref world.Items.Get(entity);
            ItemDefinition def = world.TypeOf(entity).Traits.Item!;
            if (def.Kind is ItemKind.Weapon or ItemKind.Armor && (info.Attack is > 0 || info.Defence is > 0))
            {
                // The value word: 1 attack (swords, bows), 2 defence (shields).
                bool defence = item.Value == 2 || info.Attack is not > 0;
                state.Effect = defence ? ItemEffect.Defence : ItemEffect.Attack;
                state.Power = Math.Clamp((defence ? info.Defence : info.Attack) ?? 0, 0, 1_000_000);
            }
            else if (def.Kind == ItemKind.Potion && PotionEffect(info) is ({ } effect, var amount))
            {
                state.Effect = effect;
                state.Power = Math.Clamp(amount, 0, 1_000_000);
            }
            else if ((item.Amount ?? item.Value) is { } power)
            {
                state.Power = Math.Clamp(power, 0, 1_000_000);
            }

            if (item.Charges is { } charges)
            {
                state.Charges = Math.Clamp(charges, 0, 1_000_000);
            }

            // A book's own pool and endless charges (+0x62 to +0x66; ADR 0265): a book without limited charges never runs
            // out; one that needs mana waits its time restriction (a pool of N at one point a second, UNVERIFIED), one
            // that does not, nothing. Records without these fields keep the type's.
            if (def.Kind == ItemKind.SpellBook)
            {
                if (item.LimitedCharges == false)
                {
                    state.Flags |= ItemStateFlags.Endless;
                }

                if (item.NeedsMana is { } pool)
                {
                    state.Flags |= ItemStateFlags.OwnCooldown;
                    state.CooldownMs = pool ? Math.Clamp((item.CooldownSeconds ?? 0) * 1000, 0, 3_600_000) : 0;
                }
            }

            if (def.Kind == ItemKind.SpellBook && item.MagicType is >= 0 and var magic && magic < LegacySpellOrder.Length
                && world.Ruleset.SpellId(LegacySpellOrder[magic]) is >= 0 and var spell)
            {
                state.Spell = (ushort)(spell + 1);
            }

            state.Look = (byte)(def.Looks.IndexOf(item.Definition) + 1);
        }

        if (info.Mana is { } mana && world.Manas.Has(entity))
        {
            int maxMana = world.TypeOf(entity).Traits.ManaPool?.Max ?? 0;
            world.Manas.Get(entity).MilliMana = (int)Math.Clamp((long)mana * 1000, 0, (long)maxMana * 1000);
        }
    }

    // The spells a map's book names by number (its magic type), in the original's numbering (ReTzar src/world.rs, ADR
    // 0259: hypnosis is 13, not last as STRINGS.INI [spell_names] suggested).
    private static readonly string[] LegacySpellOrder =
    [
        "spell_firerain", "spell_lightning", "spell_heal", "spell_spawncarpet", "spell_spawnbat", "spell_bless", "spell_freeze",
        "spell_berserk", "spell_soulbind", "spell_cloak", "spell_spawnstonegolem", "spell_magicvision", "raisedead",
        "spell_hypnotize", "spell_turntostone", "spell_whirlpool", "spell_spawndragon", "spell_flamebolt", "spell_raiseexp",
        "spell_spawnjin", "spell_curse",
    ];

    // A potion's effect: the first of its stats the map sets (health, level, mana, attack, defence).
    private static (ItemEffect? Effect, int Amount) PotionEffect(ObjectInfo info)
        => info.Health is > 0 and var h ? (ItemEffect.Health, h)
            : info.Level is > 0 and var l ? (ItemEffect.Levels, l)
            : info.Mana is > 0 and var m ? (ItemEffect.Mana, m)
            : info.Attack is > 0 and var a ? (ItemEffect.Attack, a)
            : info.Defence is > 0 and var d ? (ItemEffect.Defence, d)
            : (null, 0);
}
