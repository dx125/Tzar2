using System;
using System.Collections.Immutable;
using System.IO;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;

namespace Tzar2.Sim;

/// <content>Restore validation of the GAME-07…11 stores (ADR 0019: snapshots are untrusted; ADR 0096).</content>
public sealed partial class World
{
    private void ValidateCombatMagicComponents()
    {
        int tick = Tick.Value;
        int tickMs = Config.TickLengthMilliseconds;
        long maxFuture = (long)tick + (3_600_000 / tickMs) + 1;
        CombatSettings settings = Ruleset.Combat;
        int types = Ruleset.EntityTypes.Count;

        ReadOnlySpan<EntityId> ids = Experiences.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Experience x = Experiences.Components[i];
            RequireUnit(ids[i], "experience");
            if (!TypeOf(ids[i]).Traits.GainsExperience || x.Xp < 0 || x.PendingXp != 0 || x.Level > 10_000 || x.BonusAttack is < 0 or > 100_000
                || x.BonusDefence is < 0 or > 100_000 || x.Flags > ExperienceFlags.Hero || x.Reserved != 0
                || ((x.Flags & ExperienceFlags.Hero) != 0 && x.Level < settings.HeroLevel))
            {
                throw Invalid("invalid experience");
            }
        }

        ids = Helds.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Held h = Helds.Components[i];
            EntityId holder = EntityId.FromValue(h.Holder);
            // Items may be in the inventory of a unit that is itself inside a holder; holders never nest.
            if (!Entities.IsAlive(holder) || holder == ids[i] || (Helds.Has(holder) && !Items.Has(ids[i])) || !Identities.Has(ids[i]) || !Positions.Has(ids[i]) || !Positions.Has(holder))
            {
                throw Invalid("invalid holder of a held entity");
            }

            if (Items.Has(ids[i]))
            {
                if (!Inventories.Has(holder) || h.RequestSerial != 0)
                {
                    throw Invalid("an item held by something without an inventory");
                }
            }
            else if (TypeOf(holder).Traits.Holder is not { } def || !def.CanHold(Identities.Get(ids[i]).TypeId) || !Holders.Has(holder)
                || Motions.Has(ids[i]) || OccupancyCells.Has(ids[i]) || Identities.Get(ids[i]).Owner != Identities.Get(holder).Owner
                || Positions.Get(ids[i]).X != Positions.Get(holder).X || Positions.Get(ids[i]).Y != Positions.Get(holder).Y
                || Errands.Has(ids[i]) || (Casters.IndexOf(ids[i]) is >= 0 and var c && Casters.At(c).Phase != CastPhase.None))
            {
                throw Invalid("invalid unit inside a holder");
            }
        }

        int[] occupants = new int[Holders.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            if (!Items.Has(ids[i]))
            {
                occupants[Holders.IndexOf(EntityId.FromValue(Helds.Components[i].Holder))]++;
            }
        }

        ids = Holders.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            HolderState s = Holders.Components[i];
            if (TypeOf(ids[i]).Traits.Holder is null || Constructions.Has(ids[i]) || s.Count != occupants[i] || s.Count > HolderCapacity(ids[i]) || s.HealAccumMs is < 0 or >= 60_000)
            {
                throw Invalid("invalid holder state");
            }
        }

        ids = Errands.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Errand e = Errands.Components[i];
            RequireUnit(ids[i], "errand");
            bool argOk = e.Kind switch
            {
                ErrandKind.Give => Items.Has(EntityId.FromValue((uint)e.Arg)) && Helds.IndexOf(EntityId.FromValue((uint)e.Arg)) is >= 0 and var gh
                    && Helds.At(gh).Holder == ids[i].Value,
                ErrandKind.Convert => (uint)e.Arg < (uint)types && Ruleset.EntityTypes[e.Arg].Traits.ConvertsFrom is { } conv
                    && conv.From.BinarySearch(Identities.Get(ids[i]).TypeId) >= 0,
                _ => e.Arg == 0,
            };
            if (e.Kind is < ErrandKind.Board or > ErrandKind.Impersonate || !argOk || !Motions.Has(ids[i]) || e.Retries < 0 || e.TimerMs is < 0 or > 3_600_000
                || e.MoveTick < -1 || e.MoveTick > tick || e.Reserved0 != 0 || e.Reserved1 != 0 || e.Reserved2 != 0
                || (e.Kind != ErrandKind.Convert && e.TimerMs != 0) || (e.Kind == ErrandKind.Convert && e.Target != 0))
            {
                throw Invalid("invalid errand");
            }
        }

        ids = Inventories.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Inventory inv = Inventories.Components[i];
            RequireUnit(ids[i], "inventory");
            int slots = TypeOf(ids[i]).Traits.InventorySlots;
            if (slots <= 0 || inv.Count > slots || (inv.Default != Inventory.NoDefault && inv.Default >= inv.Count) || inv.Reserved0 != 0 || inv.Reserved1 != 0)
            {
                throw Invalid("invalid inventory");
            }

            for (int k = 0; k < Ruleset.InventoryLimit; k++)
            {
                uint v = inv.Items[k];
                EntityId item = EntityId.FromValue(v);
                if (k >= inv.Count ? v != 0 : !Entities.IsAlive(item) || !Items.Has(item) || Helds.IndexOf(item) is not (>= 0 and var h) || Helds.At(h).Holder != ids[i].Value)
                {
                    throw Invalid("invalid inventory slot");
                }

                for (int j = 0; j < k && k < inv.Count; j++)
                {
                    if (inv.Items[j] == v)
                    {
                        throw Invalid("an item in two slots");
                    }
                }
            }
        }

        // Every held item is in its holder's slots (the inventory check above covers the other direction).
        ids = Helds.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            if (!Items.Has(ids[i]))
            {
                continue;
            }

            Inventory inv = Inventories.Get(EntityId.FromValue(Helds.Components[i].Holder));
            bool found = false;
            for (int k = 0; k < inv.Count; k++)
            {
                found |= inv.Items[k] == ids[i].Value;
            }

            if (!found)
            {
                throw Invalid("a held item missing from its holder's inventory");
            }
        }

        ids = Items.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            ItemState s = Items.Components[i];
            if (!Identities.Has(ids[i]) || TypeOf(ids[i]).Traits.Item is not { } itemDef || s.Power is < 0 or > 1_000_000 || s.Charges is < 0 or > 1_000_000 || s.ReadyTick > maxFuture
                || s.Spell > Ruleset.Spells.Count || s.Effect > ItemEffect.Mana || s.Look > itemDef.Looks.Length
                || Combats.Has(ids[i]) || Motions.Has(ids[i]))
            {
                throw Invalid("invalid item");
            }
        }

        ids = Projectiles.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Projectile p = Projectiles.Components[i];
            if (Identities.Has(ids[i]) || Positions.Has(ids[i]) || p.Kind >= Ruleset.Projectiles.Count || !Map.ContainsPoint(p.X, p.Y) || !Map.ContainsPoint(p.TargetX, p.TargetY)
                || (p.Spell != Projectile.NoSpell && p.Spell >= Ruleset.Spells.Count) || !IsValidOwner(p.Owner) || p.AttackerType >= types
                || p.Attack is < 0 or > 1_000_000 || p.MaxAttack is < 0 or > 1_000_000 || p.DamageClass > DamageClass.Cannon || p.FromHero > 1)
            {
                throw Invalid("invalid projectile");
            }
        }

        ids = AreaEffects.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            AreaEffect a = AreaEffects.Components[i];
            if (Identities.Has(ids[i]) || Positions.Has(ids[i]) || a.Spell >= Ruleset.Spells.Count || Ruleset.Spells[a.Spell].Effect != SpellEffect.AreaDamage
                || a.RemainingMs < 0 || a.RemainingMs > Ruleset.Spells[a.Spell].DurationMs || a.PulseAccumMs < 0 || a.PulseAccumMs >= Ruleset.Spells[a.Spell].PulseMs
                || !Map.ContainsPoint(a.X, a.Y) || !IsValidOwner(a.Owner) || a.Reserved != 0)
            {
                throw Invalid("invalid area effect");
            }
        }

        ids = WaypointQueues.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            WaypointQueue q = WaypointQueues.Components[i];
            if (!Entities.IsAlive(ids[i]) || !Motions.Has(ids[i]) || q.Count is < 1 or > WaypointQueue.Capacity)
            {
                throw Invalid("invalid waypoints");
            }

            for (int k = 0; k < WaypointQueue.Capacity; k++)
            {
                Waypoint p = q.Points[k];
                if (k < q.Count
                    ? !Map.ContainsPoint(p.X, p.Y) || p.Mode is not (CombatMode.None or CombatMode.AttackMove or CombatMode.Sneak) || (p.Reserved0 | p.Reserved1 | p.Reserved2) != 0
                    : p.X != 0 || p.Y != 0 || p.Mode != CombatMode.None || (p.Reserved0 | p.Reserved1 | p.Reserved2) != 0)
                {
                    throw Invalid("invalid waypoint");
                }
            }
        }

        ids = Disguises.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Disguise g = Disguises.Components[i];
            RequireUnit(ids[i], "disguise");
            if (g.TypeId >= types || !IsValidOwner(g.Owner) || g.Owner == Identities.Get(ids[i]).Owner || g.Mastery > 100
                || (Ruleset.EntityTypes[g.TypeId].Traits.Abilities & AbilityFlags.Impersonate) == 0)
            {
                throw Invalid("invalid disguise");
            }
        }

        ids = Statuses.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            StatusEffects s = Statuses.Components[i];
            RequireUnit(ids[i], "status");
            if (s.FrozenUntil < 0 || s.StoneUntil < 0 || s.CloakUntil < 0 || s.ConfuseUntil < 0 || s.BlessUntil < 0 || s.CurseUntil < 0
                || s.FrozenUntil > maxFuture || s.StoneUntil > maxFuture || s.CloakUntil > maxFuture || s.ConfuseUntil > maxFuture || s.BlessUntil > maxFuture
                || s.CurseUntil > maxFuture || s.BlessPercent is < 0 or > 1000 || s.CursePercent is < 0 or > 100)
            {
                throw Invalid("invalid status effects");
            }
        }

        ids = Casters.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Caster c = Casters.Components[i];
            EntityTraits t = TypeOf(ids[i]).Traits;
            bool idle = c.Phase == CastPhase.None;
            bool spellOk = idle
                ? c.Spell == Projectile.NoSpell && c.Target == 0 && c.Book == 0 && c.CastLeftMs == 0 && c.X == 0 && c.Y == 0 && c.MoveTick == -1
                : c.Spell < Ruleset.Spells.Count && (c.Book == 0 ? t.CanCast(c.Spell) : BookHolds(ids[i], c.Book, c.Spell));
            if ((t.Spells.IsDefaultOrEmpty && t.InventorySlots == 0) || c.Phase > CastPhase.Casting || !spellOk || c.CastLeftMs is < 0 or > 60_000
                || c.ReadyTick > maxFuture || c.MoveTick < -1 || c.MoveTick > tick || c.Reserved != 0 || !Map.ContainsPoint(c.X, c.Y))
            {
                throw Invalid("invalid caster state");
            }
        }

        ids = Stances.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            if (Stances.Components[i].Value is < CombatStance.StandGround or > CombatStance.HoldFire || !Combats.Has(ids[i]))
            {
                throw Invalid("invalid stance");
            }
        }

        ids = CraftJobs.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            CraftJob j = CraftJobs.Components[i];
            if (TypeOf(ids[i]).Traits.Crafter is not { } crafter || Constructions.Has(ids[i]) || (uint)j.ItemType >= (uint)types
                || crafter.Items.BinarySearch((ushort)j.ItemType) < 0 || (uint)j.Variant >= (uint)Ruleset.EntityTypes[j.ItemType].Traits.Item!.CraftVariantCount || j.Power < 1 || j.Power > settings.CraftMaxPower || j.LeftMs < 0
                || j.LeftMs > (long)j.Power * settings.CraftMsPerPower || !HasPlayer(Identities.Get(ids[i]).Owner))
            {
                throw Invalid("invalid craft job");
            }
        }

        ids = Crusades.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            CrusadeState s = Crusades.Components[i];
            if (Identities.Get(ids[i]).TypeId != settings.CrusadeAnchor || s.ToRaise < 0 || s.ToRaise > settings.CrusadeSize || s.EndTick < 0
                || s.EndTick > tick + ((long)settings.CrusadeCooldownMs / tickMs) + 1 || !HasPlayer(Identities.Get(ids[i]).Owner))
            {
                throw Invalid("invalid crusade");
            }
        }

        ids = Combats.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Combat c = Combats.Components[i];
            if (c.Mode == CombatMode.Ground && (c.Target != 0 || !Map.ContainsPoint(c.ChaseX, c.ChaseY) || TypeOf(ids[i]).Traits.Armament is not { RangeUnits: > 0 }))
            {
                throw Invalid("invalid attack-ground state");
            }
        }

        ids = CrusaderLinks.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            RequireUnit(ids[i], "crusader");
        }

        static InvalidDataException Invalid(string what) => new($"Snapshot violates a world invariant: {what}.");
    }

    private bool BookHolds(EntityId caster, uint book, ushort spell)
    {
        EntityId item = EntityId.FromValue(book);
        return Inventories.IndexOf(caster) is >= 0 and var inv && Helds.IndexOf(item) is >= 0 and var h && Helds.At(h).Holder == caster.Value
            && Inventories.At(inv).Count > 0 && Items.Has(item) && ItemSpell(item) == spell;
    }

    private void RequireUnit(EntityId e, string what)
    {
        if (!Identities.Has(e) || !Positions.Has(e) || !Healths.Has(e) || !TypeOf(e).IsMobile)
        {
            throw new InvalidDataException($"Snapshot violates a world invariant: {what} on {e}, which is not a unit.");
        }
    }
}
