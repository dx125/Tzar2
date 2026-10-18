using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 10 (TDD §8.5, GAME-10/11; ADRs 0094, 0095): casters walk into range and cast; area spells pulse;
/// lasting effects end; crusade anchors raise their crusaders and expire; Magic Workshops finish items.
/// </summary>
/// <remarks>
/// A cast is paid when it takes effect (mana, a book's charge, a summon's cost), never when ordered, so a caster that is
/// interrupted or runs short pays nothing. Effects on units apply in caster order; deaths they cause are processed at
/// the end of the step, like combat's (simultaneous within the step).
/// </remarks>
public sealed class MagicSystem : ISimSystem
{
    /// <summary>Least ticks between two approach move requests of a caster.</summary>
    public const int MoveIntervalTicks = 10;

    private readonly List<Death> _deaths = [];
    private readonly List<EntityId> _done = [];
    private readonly List<EntityId> _casting = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Spells;

    /// <inheritdoc />
    public string Name => "magic";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        int tick = world.Tick.Value;
        int tickMs = world.Config.TickLengthMilliseconds;
        _deaths.Clear();
        // The casting ones first: a cast may summon units (new casters shift the store), so each is looked up by entity.
        _casting.Clear();
        for (int i = 0; i < world.Casters.Count; i++)
        {
            if (world.Casters.At(i).Phase != CastPhase.None)
            {
                _casting.Add(world.Casters.Entities[i]);
            }
        }

        foreach (EntityId caster in _casting)
        {
            if (world.Entities.IsAlive(caster) && world.Casters.IndexOf(caster) is >= 0 and var c && world.Casters.At(c).Phase != CastPhase.None)
            {
                Cast(world, caster, c, tick, tickMs);
            }
        }

        PulseAreas(world, tickMs);
        ExpireStatuses(world, tick);
        Crusades(world, tick);
        Crafting(world, tickMs);
        CombatSystem.ProcessDeaths(world, _deaths);
    }

    /// <summary>
    /// Whether <paramref name="caster"/> may start <paramref name="spell"/> now (command validation): it knows the spell
    /// (its card, or the given book), its civilization has the spell's technology, it has the mana and its cooldown is
    /// over.
    /// </summary>
    internal static CommandRejectionReason CanCast(World world, EntityId caster, ushort spell, EntityId book)
    {
        if (spell >= world.Ruleset.Spells.Count || !world.CanAct(caster) || world.IsHeld(caster))
        {
            return CommandRejectionReason.Unknown;
        }

        SpellDefinition s = world.Ruleset.Spells[spell];
        EntityTraits traits = world.TypeOf(caster).Traits;
        if (book.IsNone)
        {
            if (!traits.CanCast(spell))
            {
                return CommandRejectionReason.Unknown;
            }

            int gate = world.Ruleset.OrderTechnology(s.Key, world.CivilizationOf(caster));
            if (gate >= 0 && !world.Players[world.Identities.Get(caster).Owner].HasTechnology(gate))
            {
                return CommandRejectionReason.Unknown;
            }
        }
        else if (world.Items.IndexOf(book) is not (>= 0 and var b) || world.TypeOf(book).Traits.Item is not { } item || item.Spell != spell)
        {
            return CommandRejectionReason.Unknown;
        }
        else if ((item.Charges > 0 && world.Items.At(b).Charges <= 0) || world.Items.At(b).ReadyTick > world.Tick.Value)
        {
            return CommandRejectionReason.NotReady;
        }

        if (world.Casters.IndexOf(caster) is >= 0 and var c && world.Casters.At(c).ReadyTick > world.Tick.Value)
        {
            return CommandRejectionReason.NotReady;
        }

        bool needsMana = book.IsNone || world.TypeOf(book).Traits.Item!.NeedsMana;
        if (needsMana && ManaOf(world, caster) < ManaCost(world, s, caster))
        {
            return CommandRejectionReason.NoMana;
        }

        return CommandRejectionReason.None;
    }

    /// <summary>Why a cast cannot start.</summary>
    internal enum CommandRejectionReason
    {
        /// <summary>It can.</summary>
        None,

        /// <summary>The caster cannot cast it.</summary>
        Unknown,

        /// <summary>Cooldown or no charges.</summary>
        NotReady,

        /// <summary>Not enough mana.</summary>
        NoMana,
    }

    /// <summary>
    /// A spell's mana: its own plus a summoned type's (cost suffix <c>m</c>). The tables price some summons above any
    /// caster's pool (stone golem 600m, dragon 800m against a mage's <c>max_manna = 100</c>): ⚠ HYPOTHESIS (UNK-26), a
    /// summon costs at most the caster's whole pool, so it needs a full pool and empties it.
    /// </summary>
    internal static int ManaCost(World world, SpellDefinition s, EntityId caster)
    {
        long mana = s.ManaCost;
        if (s.Effect == SpellEffect.Summon && s.Summon >= 0)
        {
            mana += (long)world.Ruleset.EntityTypes[s.Summon].Traits.ManaCost * s.Count;
            Identity identity = world.Identities.Get(caster);
            if (world.Ruleset.EntityTypes[identity.TypeId].Traits.ManaPool is { } pool)
            {
                mana = Math.Min(mana, world.Stats.Apply(identity.Owner, ModifierStat.ManaMax, identity.TypeId, pool.Max));
            }
        }

        return (int)Math.Min(mana, int.MaxValue);
    }

    /// <summary>The caster's whole mana points.</summary>
    internal static int ManaOf(World world, EntityId caster)
        => world.Manas.IndexOf(caster) is >= 0 and var m ? world.Manas.At(m).MilliMana / 1000 : 0;

    /// <summary>Whether <paramref name="target"/> is a unit the spell may affect (by type and by owner).</summary>
    internal static bool Affects(World world, SpellDefinition s, byte casterOwner, EntityId target)
    {
        if (!world.Entities.IsAlive(target) || world.Identities.IndexOf(target) is not (>= 0 and var i) || world.IsHeld(target)
            || world.Healths.IndexOf(target) is not (>= 0 and var h) || world.Healths.At(h).Current <= 0)
        {
            return false;
        }

        Identity identity = world.Identities.At(i);
        EntityTraits t = world.Ruleset.EntityTypes[identity.TypeId].Traits;
        if (t.Category is not (EntityCategory.Unit or EntityCategory.Building) || !s.AffectsType(identity.TypeId))
        {
            return false;
        }

        bool own = identity.Owner == casterOwner;
        if (!own && world.IsCloaked(target))
        {
            return false;
        }

        return own ? (s.Affects & SpellAffects.Own) != 0 : (s.Affects & SpellAffects.Enemy) != 0;
    }

    /// <summary>The cast range of <paramref name="caster"/> for <paramref name="s"/> (Medicine: +abilityRange tiles).</summary>
    internal static int RangeOf(World world, EntityId caster, SpellDefinition s)
    {
        Identity identity = world.Identities.Get(caster);
        int extraTiles = world.Stats.Apply(identity.Owner, ModifierStat.AbilityRange, identity.TypeId, 0);
        long range = Math.Max(s.RangeUnits, world.Ruleset.Gameplay.MeleeReachUnits) + ((long)extraTiles * MapState.UnitsPerTile); // range 0 = touch
        return (int)Math.Min(range, 64L * MapState.UnitsPerTile);
    }

    private void Cast(World world, EntityId caster, int index, int tick, int tickMs)
    {
        ref Caster c = ref world.Casters.At(index);
        SpellDefinition s = world.Ruleset.Spells[c.Spell];
        byte owner = world.Identities.Get(caster).Owner;
        EntityId target = EntityId.FromValue(c.Target);
        EntityId book = EntityId.FromValue(c.Book);
        bool bookGone = c.Book != 0 && !(world.Helds.IndexOf(book) is >= 0 and var bh && world.Helds.At(bh).Holder == caster.Value);
        bool targetGone = s.Target == SpellTarget.Unit && !Affects(world, s, owner, target);
        if (bookGone || targetGone || !world.CanAct(caster))
        {
            world.StopCasting(caster);
            return;
        }

        Position at = world.Positions.Get(caster);
        int range = RangeOf(world, caster, s);
        long distance = s.Target switch
        {
            SpellTarget.Unit => Interaction.EdgeDistance(world, at, Interaction.RadiusOf(world, caster), target),
            SpellTarget.Ground => IntMath.ISqrt(IntMath.DistanceSquared(at.X, at.Y, c.X, c.Y)),
            _ => 0,
        };
        if (c.Phase == CastPhase.Approach)
        {
            if (distance > range)
            {
                if (c.MoveTick < 0 || tick - c.MoveTick >= MoveIntervalTicks)
                {
                    c.MoveTick = tick;
                    (int x, int y) = s.Target == SpellTarget.Unit ? (world.Positions.Get(target).X, world.Positions.Get(target).Y) : (c.X, c.Y);
                    Interaction.MoveTo(world, caster, x, y);
                }

                return;
            }

            Interaction.Halt(world, caster);
            c.Phase = CastPhase.Casting;
            c.CastLeftMs = s.CastMs;
        }

        if (distance > range + MapState.UnitsPerTile)
        {
            c.Phase = CastPhase.Approach; // the target walked away during the cast
            return;
        }

        c.CastLeftMs -= tickMs;
        if (c.CastLeftMs > 0)
        {
            return;
        }

        // The cast takes effect: pay (mana, a book's charge, a summon's resources), then apply.
        bool needsMana = c.Book == 0 || world.TypeOf(book).Traits.Item!.NeedsMana;
        int mana = needsMana ? ManaCost(world, s, caster) : 0;
        if (ManaOf(world, caster) < mana || (s.Effect == SpellEffect.Summon && !CanSummon(world, owner, s)))
        {
            world.StopCasting(caster);
            return;
        }

        if (mana > 0)
        {
            world.Manas.Get(caster).MilliMana = (int)Math.Max(0, world.Manas.Get(caster).MilliMana - ((long)mana * 1000));
        }

        if (c.Book != 0)
        {
            ref ItemState state = ref world.Items.Get(book);
            ItemDefinition def = world.TypeOf(book).Traits.Item!;
            state.ReadyTick = tick + ((def.CooldownMs + tickMs - 1) / tickMs);
            if (def.Charges > 0)
            {
                state.Charges--;
            }
        }

        if (s.Effect == SpellEffect.Summon)
        {
            PaySummon(world, owner, s);
        }

        int ready = tick + ((s.CooldownMs + tickMs - 1) / tickMs);
        int tx = s.Target == SpellTarget.Unit ? world.Positions.Get(target).X : s.Target == SpellTarget.Self ? at.X : c.X;
        int ty = s.Target == SpellTarget.Unit ? world.Positions.Get(target).Y : s.Target == SpellTarget.Self ? at.Y : c.Y;
        EntityId effectTarget = s.Target == SpellTarget.Self ? caster : target;
        bool usedBook = c.Book != 0;
        EntityId bookEntity = book;
        world.StopCasting(caster);
        ref Caster after = ref world.Casters.Get(caster);
        after.ReadyTick = ready;
        if (!(s.Effect == SpellEffect.Cloak && effectTarget == caster))
        {
            UncloakCaster(world, caster); // casting reveals an invisible unit (1.10 fix)
        }

        world.Events.Emit(new SimEvent(SimEventKind.SpellCast, owner, caster, s.Target == SpellTarget.Unit ? target.Index : -1, (tx / 64) | ((ty / 64) << 16), s.Id));
        if (s.Projectile >= 0 && s.Target == SpellTarget.Unit && world.Entities.RemainingCapacity >= 1)
        {
            EntityId p = world.Entities.Create();
            world.Projectiles.Set(p, new Projectile
            {
                X = at.X,
                Y = at.Y,
                TargetX = tx,
                TargetY = ty,
                Target = target.Value,
                Attacker = caster.Value,
                AttackerType = world.Identities.Get(caster).TypeId,
                Spell = s.Id,
                Kind = (byte)s.Projectile,
                Owner = owner,
            });
        }
        else
        {
            ApplyEffect(world, s.Id, caster, owner, effectTarget, tx, ty, _deaths);
        }

        if (usedBook && world.Items.IndexOf(bookEntity) is >= 0 and var used && world.TypeOf(bookEntity).Traits.Item is { Charges: > 0 } && world.Items.At(used).Charges <= 0)
        {
            world.Destroy(bookEntity); // a spent book is gone
        }
    }

    private static void UncloakCaster(World world, EntityId caster)
    {
        if (world.Statuses.IndexOf(caster) is >= 0 and var s)
        {
            world.Statuses.At(s).CloakUntil = 0;
        }
    }

    /// <summary>
    /// Applies the effect of <paramref name="spell"/> (direct casts, spell projectiles, soul items): damage, healing,
    /// lasting effects, summons, raising the dead, revealers, experience, area spells, kills.
    /// </summary>
    internal static void ApplyEffect(World world, ushort spell, EntityId caster, byte owner, EntityId target, int x, int y, List<Death> deaths)
    {
        SpellDefinition s = world.Ruleset.Spells[spell];
        int tick = world.Tick.Value;
        int tickMs = world.Config.TickLengthMilliseconds;
        int until = tick + Math.Max(1, (s.DurationMs + tickMs - 1) / tickMs);
        switch (s.Effect)
        {
            case SpellEffect.Summon:
                Summon(world, caster, owner, s, x, y);
                return;
            case SpellEffect.RaiseDead:
                RaiseDead(world, caster, owner, s, x, y);
                return;
            case SpellEffect.Reveal:
                if (world.Entities.RemainingCapacity >= 1 && world.Map.ContainsPoint(x, y))
                {
                    EntityId r = world.Spawn((ushort)s.Summon, owner, x, y);
                    world.Events.Emit(new SimEvent(SimEventKind.Summoned, owner, r, caster.Index, 0, s.Summon));
                }

                return;
            case SpellEffect.AreaDamage:
                if (world.Entities.RemainingCapacity >= 1 && world.Map.ContainsPoint(x, y))
                {
                    EntityId area = world.Entities.Create();
                    world.AreaEffects.Set(area, new AreaEffect { X = x, Y = y, RemainingMs = s.DurationMs, Caster = caster.Value, Spell = s.Id, Owner = owner });
                }

                return;
        }

        if (!Affects(world, s, owner, target))
        {
            return;
        }

        world.Events.Emit(new SimEvent(SimEventKind.SpellEffect, world.Identities.Get(target).Owner, target, caster.Index, 0, s.Id));
        switch (s.Effect)
        {
            case SpellEffect.Damage:
                Damage(world, caster, owner, target, s.Amount, deaths);
                break;
            case SpellEffect.Heal:
            {
                ref Health h = ref world.Healths.Get(target);
                h.Current = (int)Math.Min((long)h.Current + s.Amount, h.Max);
                break;
            }

            case SpellEffect.Bless:
            {
                ref StatusEffects st = ref StatusOf(world, target);
                st.BlessUntil = until;
                st.BlessPercent = (short)Math.Min(s.Amount, 1000);
                break;
            }

            case SpellEffect.Curse:
            {
                ref StatusEffects st = ref StatusOf(world, target);
                st.CurseUntil = until;
                st.CursePercent = (short)Math.Min(s.Amount, 100);
                break;
            }

            case SpellEffect.Freeze:
            case SpellEffect.Stone:
            {
                ref StatusEffects st = ref StatusOf(world, target);
                if (s.Effect == SpellEffect.Freeze)
                {
                    st.FrozenUntil = Math.Max(st.FrozenUntil, until);
                }
                else
                {
                    st.StoneUntil = Math.Max(st.StoneUntil, until);
                }

                GameplayCommands.ClearTasks(world, target);
                world.StopCasting(target);
                Interaction.Halt(world, target);
                break;
            }

            case SpellEffect.Cloak:
            {
                ref StatusEffects st = ref StatusOf(world, target);
                st.CloakUntil = s.DurationMs > 0 ? until : tick + (3_600_000 / tickMs); // "until it attacks": an hour at most (fits the restore bound)
                break;
            }

            case SpellEffect.Confuse:
            {
                ref StatusEffects st = ref StatusOf(world, target);
                st.ConfuseUntil = until;
                if (world.Combats.IndexOf(target) is >= 0 and var cb)
                {
                    world.Combats.At(cb).Target = 0;
                    world.Combats.At(cb).Mode = CombatMode.None;
                }

                break;
            }

            case SpellEffect.Experience:
                if (world.Experiences.IndexOf(target) is >= 0 and var xp)
                {
                    world.Experiences.At(xp).PendingXp += s.Amount;
                }

                break;
            case SpellEffect.Kill:
                if (!world.IsHero(target) && (world.TypeOf(target).Traits.Abilities & AbilityFlags.KillImmune) == 0 && !world.IsStone(target))
                {
                    Damage(world, caster, owner, target, world.Healths.Get(target).Current, deaths);
                }

                break;
        }
    }

    // Spell damage: the amount, without defence or bonuses (UNVERIFIED, UNK-26); stone and units inside holders are immune.
    private static void Damage(World world, EntityId caster, byte owner, EntityId target, int amount, List<Death> deaths)
    {
        if (amount <= 0 || world.IsStone(target) || world.IsHeld(target) || world.IsInvulnerable(world.Identities.Get(target).Owner))
        {
            return;
        }

        ref Health h = ref world.Healths.Get(target);
        if (h.Current <= 0)
        {
            return;
        }

        h.Current = Math.Max(0, h.Current - amount);
        Identity victim = world.Identities.Get(target);
        world.Events.Emit(new SimEvent(SimEventKind.Damaged, victim.Owner, target, caster.Index, h.Current, amount));
        CombatSystem.Credit(world, new HitRecord(caster, target, owner, 0, 0, 0, 100, DamageClass.Arrow, false), victim.Owner, amount);
        if (h.Current == 0)
        {
            deaths.Add(new Death(target, caster, owner));
        }
    }

    internal static ref StatusEffects StatusOf(World world, EntityId unit)
    {
        if (world.Statuses.IndexOf(unit) is not (>= 0 and var s))
        {
            world.Statuses.Set(unit, default);
            s = world.Statuses.IndexOf(unit);
        }

        return ref world.Statuses.At(s);
    }

    private static bool CanSummon(World world, byte owner, SpellDefinition s)
    {
        if (!world.HasPlayer(owner) || world.Entities.RemainingCapacity < s.Count)
        {
            return false;
        }

        EntityTypeDefinition type = world.Ruleset.EntityTypes[s.Summon];
        ref PlayerState player = ref world.Players[owner];
        if (player.Population + ((long)type.Traits.PopulationCost * s.Count) > player.PopulationCap)
        {
            return false;
        }

        for (int r = 0; r < type.Traits.Cost.Length; r++)
        {
            if (player.Resources[r] < (long)type.Traits.Cost[r] * s.Count)
            {
                return false;
            }
        }

        return true;
    }

    private static void PaySummon(World world, byte owner, SpellDefinition s)
    {
        EntityTypeDefinition type = world.Ruleset.EntityTypes[s.Summon];
        ref PlayerState player = ref world.Players[owner];
        for (int r = 0; r < type.Traits.Cost.Length; r++)
        {
            player.Resources[r] -= type.Traits.Cost[r] * s.Count;
        }

        player.Population += type.Traits.PopulationCost * s.Count;
    }

    private static void Summon(World world, EntityId caster, byte owner, SpellDefinition s, int x, int y)
    {
        EntityTypeDefinition type = world.Ruleset.EntityTypes[s.Summon];
        for (int n = 0; n < s.Count && world.Entities.RemainingCapacity >= 1; n++)
        {
            (int px, int py) = FreeSpot(world, type.Locomotor, x, y);
            EntityId unit = world.Spawn(type.Id, owner, px, py);
            world.Events.Emit(new SimEvent(SimEventKind.Summoned, owner, unit, caster.Index, 0, type.Id));
        }
    }

    // Raise Dead (necromancy): the oldest corpses within the radius of the point rise as the spell's units.
    private static void RaiseDead(World world, EntityId caster, byte owner, SpellDefinition s, int x, int y)
    {
        EntityTypeDefinition type = world.Ruleset.EntityTypes[s.Summon];
        for (int n = 0; n < s.Count && world.Entities.RemainingCapacity >= 1; n++)
        {
            if (!world.Decals.TakeCorpse(x, y, s.RadiusUnits, out Decal corpse))
            {
                break;
            }

            (int px, int py) = FreeSpot(world, type.Locomotor, corpse.X, corpse.Y);
            EntityId unit = world.Spawn(type.Id, owner, px, py);
            world.Events.Emit(new SimEvent(SimEventKind.Summoned, owner, unit, caster.Index, 0, type.Id));
        }
    }

    // The point itself when passable for the locomotor, else the nearest passable tile center within 3 tiles.
    internal static (int X, int Y) FreeSpot(World world, Locomotor locomotor, int x, int y)
    {
        MapState map = world.Map;
        x = Math.Clamp(x, 0, map.WorldWidth - 1);
        y = Math.Clamp(y, 0, map.WorldHeight - 1);
        if (map.IsPassableAt(locomotor, x, y))
        {
            return (x, y);
        }

        int cx = x / MapState.UnitsPerTile;
        int cy = y / MapState.UnitsPerTile;
        for (int r = 1; r <= 3; r++)
        {
            for (int ty = cy - r; ty <= cy + r; ty++)
            {
                for (int tx = cx - r; tx <= cx + r; tx++)
                {
                    if (map.ContainsTile(tx, ty) && map.IsPassable(locomotor, tx, ty))
                    {
                        return ((tx * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2), (ty * MapState.UnitsPerTile) + (MapState.UnitsPerTile / 2));
                    }
                }
            }
        }

        return (x, y);
    }

    // Rain of fire, whirlpools: each pulse damages every affected unit and building in the radius (fire rain hits the
    // caster's own units too, manual ch. 6).
    private void PulseAreas(World world, int tickMs)
    {
        _done.Clear();
        ReadOnlySpan<EntityId> ids = world.AreaEffects.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            ref AreaEffect a = ref world.AreaEffects.At(i);
            SpellDefinition s = world.Ruleset.Spells[a.Spell];
            int elapsed = Math.Min(tickMs, a.RemainingMs);
            a.RemainingMs -= elapsed;
            a.PulseAccumMs += elapsed;
            while (a.PulseAccumMs >= s.PulseMs)
            {
                a.PulseAccumMs -= s.PulseMs;
                Pulse(world, a, s);
            }

            if (a.RemainingMs <= 0)
            {
                _done.Add(ids[i]);
            }
        }

        foreach (EntityId e in _done)
        {
            world.Destroy(e);
        }
    }

    private void Pulse(World world, in AreaEffect a, SpellDefinition s)
    {
        long r2 = (long)s.RadiusUnits * s.RadiusUnits;
        EntityId caster = EntityId.FromValue(a.Caster);
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            EntityId e = ids[i];
            if (!Affects(world, s, a.Owner, e) || Interaction.DistanceSquaredTo(world, a.X, a.Y, e) > r2)
            {
                continue;
            }

            Damage(world, caster, a.Owner, e, s.Amount, _deaths);
        }
    }

    private void ExpireStatuses(World world, int tick)
    {
        _done.Clear();
        ReadOnlySpan<EntityId> ids = world.Statuses.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            ref StatusEffects s = ref world.Statuses.At(i);
            if (s.FrozenUntil > tick || s.StoneUntil > tick)
            {
                Interaction.Halt(world, ids[i]); // a frozen or stone unit stays put
            }

            if (s.FrozenUntil <= tick && s.StoneUntil <= tick && s.CloakUntil <= tick && s.ConfuseUntil <= tick && s.BlessUntil <= tick && s.CurseUntil <= tick)
            {
                _done.Add(ids[i]);
            }
        }

        foreach (EntityId e in _done)
        {
            world.Statuses.Remove(e);
        }
    }

    // Crusade anchors raise their crusaders one per tick (while there is room); once all are dead the anchor stays for
    // the cooldown and then goes (a new crusade needs it gone; 1.09).
    private void Crusades(World world, int tick)
    {
        _done.Clear();
        CombatSettings settings = world.Ruleset.Combat;
        ReadOnlySpan<EntityId> ids = world.Crusades.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            ref CrusadeState s = ref world.Crusades.At(i);
            byte owner = world.Identities.Get(ids[i]).Owner;
            if (s.ToRaise > 0 && settings.Crusader >= 0 && world.Entities.RemainingCapacity >= 1)
            {
                Position at = world.Positions.Get(ids[i]);
                (int x, int y) = FreeSpot(world, world.Ruleset.EntityTypes[settings.Crusader].Locomotor, at.X, at.Y);
                EntityId crusader = world.Spawn((ushort)settings.Crusader, owner, x, y);
                world.CrusaderLinks.Set(crusader, new CrusaderOf { Anchor = ids[i].Value });
                world.Events.Emit(new SimEvent(SimEventKind.Summoned, owner, crusader, ids[i].Index, 0, settings.Crusader));
                s.ToRaise--;
                continue;
            }

            bool alive = false;
            foreach (CrusaderOf link in world.CrusaderLinks.Components)
            {
                alive |= link.Anchor == ids[i].Value;
            }

            if (alive || s.ToRaise > 0)
            {
                s.EndTick = 0;
            }
            else if (s.EndTick == 0)
            {
                s.EndTick = tick + ((settings.CrusadeCooldownMs + world.Config.TickLengthMilliseconds - 1) / world.Config.TickLengthMilliseconds);
            }
            else if (tick >= s.EndTick)
            {
                _done.Add(ids[i]);
            }
        }

        foreach (EntityId e in _done)
        {
            world.Destroy(e);
        }
    }

    // Magic Workshops (GAME-10): a finished item appears next to the workshop.
    private void Crafting(World world, int tickMs)
    {
        _done.Clear();
        ReadOnlySpan<EntityId> ids = world.CraftJobs.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            ref CraftJob job = ref world.CraftJobs.At(i);
            job.LeftMs -= tickMs;
            if (job.LeftMs <= 0 && world.Entities.RemainingCapacity >= 1)
            {
                _done.Add(ids[i]);
            }
        }

        foreach (EntityId workshop in _done)
        {
            CraftJob job = world.CraftJobs.Get(workshop);
            world.CraftJobs.Remove(workshop);
            Position at = world.Positions.Get(workshop);
            if (!Interaction.ApproachPoint(world, workshop, at.X, at.Y + MapState.UnitsPerTile * 4, Locomotor.Foot, out int x, out int y))
            {
                (x, y) = (at.X, at.Y);
            }

            EntityId item = world.Spawn((ushort)job.ItemType, MapObject.Nature, x, y);
            world.Items.Get(item).Power = job.Power;
            world.Events.Emit(new SimEvent(SimEventKind.ItemCrafted, world.Identities.Get(workshop).Owner, item, workshop.Index, 0, job.ItemType));
        }
    }
}
