using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Effective combat values of one entity (GAME-07/08/10/11; ADRs 0091-0095): the type's value after its owner's
/// technologies (ADR 0040) and its own upgrade levels, plus its experience level (manual: +1 attack and +½ defence per
/// level), potions taken, the weapons and armor it carries, and blessings and curses on it.
/// </summary>
internal static class CombatStats
{
    /// <summary>The attack of <paramref name="entity"/> before the target's defence.</summary>
    public static int Attack(World world, EntityId entity, EntityTypeDefinition type, ArmamentDefinition arm)
    {
        byte owner = world.Identities.Get(entity).Owner;
        long attack = ResearchRules.LevelBonus(world, entity, ModifierStat.Attack, world.Stats.Apply(owner, ModifierStat.Attack, type.Id, arm.Attack));
        if (world.Experiences.Count != 0 && world.Experiences.IndexOf(entity) is >= 0 and var x)
        {
            Experience e = world.Experiences.At(x);
            attack += ((long)e.Level * world.Ruleset.Combat.AttackPerLevel) + e.BonusAttack;
        }

        attack += ItemPower(world, entity, ItemEffect.Attack);
        attack = Status(world, entity, attack, defence: false);
        return (int)Math.Clamp(attack, 0, 1_000_000);
    }

    /// <summary>The defence of <paramref name="entity"/>.</summary>
    public static int Defence(World world, EntityId entity, EntityTypeDefinition type)
    {
        byte owner = world.Identities.Get(entity).Owner;
        long defence = world.Stats.Apply(owner, ModifierStat.Defence, type.Id, type.Traits.Defence);
        if (world.Experiences.Count != 0 && world.Experiences.IndexOf(entity) is >= 0 and var x)
        {
            Experience e = world.Experiences.At(x);
            defence += ((long)e.Level * world.Ruleset.Combat.DefenceHalvesPerLevel / 2) + e.BonusDefence;
        }

        defence += ItemPower(world, entity, ItemEffect.Defence);
        defence = Status(world, entity, defence, defence: true);
        return (int)Math.Clamp(defence, 0, 1_000_000);
    }

    /// <summary>
    /// The summed power of the carried weapons and armor that add <paramref name="stat"/>: an item's own effect (a shield
    /// among the power items, ITEM-016), else its kind's (weapons attack, armor defence).
    /// </summary>
    public static int ItemPower(World world, EntityId entity, ItemEffect stat)
    {
        if (world.Inventories.Count == 0 || world.Inventories.IndexOf(entity) is not (>= 0 and var i) || world.Inventories.At(i).Count == 0)
        {
            return 0;
        }

        Inventory inventory = world.Inventories.At(i);
        long sum = 0;
        for (int k = 0; k < inventory.Count; k++)
        {
            EntityId item = EntityId.FromValue(inventory.Items[k]);
            ItemKind kind = world.TypeOf(item).Traits.Item!.Kind;
            if (kind is ItemKind.Weapon or ItemKind.Armor && world.Items.IndexOf(item) is >= 0 and var s)
            {
                ItemState state = world.Items.At(s);
                ItemEffect adds = state.Effect != ItemEffect.Default ? state.Effect : kind == ItemKind.Weapon ? ItemEffect.Attack : ItemEffect.Defence;
                if (adds == stat)
                {
                    sum += state.Power;
                }
            }
        }

        return (int)Math.Min(sum, 1_000_000);
    }

    // Blessing raises attack; a curse lowers attack by its percent and takes away all defence (GAME-11; ReTzar's reading
    // of the original, 0x4e5841).
    private static long Status(World world, EntityId entity, long value, bool defence)
    {
        if (world.Statuses.Count == 0 || world.Statuses.IndexOf(entity) is not (>= 0 and var s))
        {
            return value;
        }

        StatusEffects st = world.Statuses.At(s);
        int tick = world.Tick.Value;
        int percent = 100;
        if (!defence && st.BlessUntil > tick)
        {
            percent += st.BlessPercent;
        }

        if (st.CurseUntil > tick)
        {
            if (defence)
            {
                return 0;
            }

            percent -= st.CursePercent;
        }

        return value * Math.Max(0, percent) / 100;
    }

    /// <summary>Experience needed for level <paramref name="level"/>: <c>base × n × (n + 1) / 2</c> (each level costs more).</summary>
    public static int XpForLevel(CombatSettings settings, int level)
        => (int)Math.Min((long)settings.LevelBaseXp * level * (level + 1) / 2, int.MaxValue);

    /// <summary>The highest level a unit may reach (base cap plus Learning, Heroism...).</summary>
    public static int LevelCap(World world, EntityId entity)
    {
        Identity identity = world.Identities.Get(entity);
        return Math.Max(0, world.Stats.Apply(identity.Owner, ModifierStat.ExperienceCap, identity.TypeId, world.Ruleset.Combat.BaseLevelCap));
    }
}
