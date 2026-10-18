using System;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Components;

namespace Tzar2.Scripting.Legacy;

/// <summary>
/// The scenario properties of placed objects (Appendix C.2; ADR 0130): name, and the "use default" overrides of health,
/// max health, attack, defence, mana, level and resistance. Applied at match start and when a Not Appear object appears.
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

        if (info.Mana is { } mana && world.Manas.Has(entity))
        {
            int maxMana = world.TypeOf(entity).Traits.ManaPool?.Max ?? 0;
            world.Manas.Get(entity).MilliMana = (int)Math.Clamp((long)mana * 1000, 0, (long)maxMana * 1000);
        }
    }
}
