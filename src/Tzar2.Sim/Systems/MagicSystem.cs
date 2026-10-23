using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    private readonly List<(EntityId Unit, byte Owner)> _woken = [];

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
        MarketItems.Step(world, tick);
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
        else if (world.Items.IndexOf(book) is not (>= 0 and var b) || world.TypeOf(book).Traits.Item is not { } item || world.ItemSpell(book) != spell)
        {
            return CommandRejectionReason.Unknown;
        }
        else if ((world.Items.At(b).LimitedCharges(item) && world.Items.At(b).Charges <= 0) || world.Items.At(b).ReadyTick > world.Tick.Value)
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
        return (int)Math.Min(range, 1024L * MapState.UnitsPerTile);
    }

    private void Cast(World world, EntityId caster, int index, int tick, int tickMs)
    {
        ref Caster c = ref world.Casters.At(index);
        SpellDefinition s = world.Ruleset.Spells[c.Spell];
        byte owner = world.Identities.Get(caster).Owner;
        EntityId target = EntityId.FromValue(c.Target);
        EntityId book = EntityId.FromValue(c.Book);
        bool bookGone = c.Book != 0 && !(world.Helds.IndexOf(book) is >= 0 and var bh && world.Helds.At(bh).Holder == caster.Value);
        bool targetGone = s.Target == SpellTarget.Unit && (!Affects(world, s, owner, target) || !Teachable(world, s, caster, target));
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
            state.ReadyTick = tick + ((state.CooldownFor(def) + tickMs - 1) / tickMs);
            if (state.LimitedCharges(def))
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

        if (usedBook && world.Items.IndexOf(bookEntity) is >= 0 and var used && world.TypeOf(bookEntity).Traits.Item is { } usedType && world.Items.At(used).LimitedCharges(usedType) && world.Items.At(used).Charges <= 0)
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
        int durationMs = s.DurationMaxMs > s.DurationMs ? s.DurationMs + world.Rng.Misc.NextInt(s.DurationMaxMs - s.DurationMs + 1) : s.DurationMs;
        int until = tick + Math.Max(1, (durationMs + tickMs - 1) / tickMs);
        switch (s.Effect)
        {
            case SpellEffect.Bless or SpellEffect.Freeze or SpellEffect.Confuse when s.RadiusUnits > 0:
                if (world.Entities.IsAlive(target) && world.Positions.Has(target))
                {
                    Position c = world.Positions.Get(target);
                    (x, y) = (c.X, c.Y);
                }

                StatusAround(world, s, caster, owner, x, y, until, deaths);
                return;
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
                    // A delayed first pulse (fire rain's fall) starts the accumulator below zero.
                    world.AreaEffects.Set(area, new AreaEffect { X = x, Y = y, RemainingMs = s.DurationMs, PulseAccumMs = -s.DelayMs, Caster = caster.Value, Spell = s.Id, Owner = owner });
                }

                return;
            case SpellEffect.Curse:
                if (world.Entities.IsAlive(target) && world.Positions.Has(target))
                {
                    Position p = world.Positions.Get(target);
                    (x, y) = (p.X, p.Y);
                }

                CurseAround(world, s, caster, owner, x, y, until);
                return;
        }

        if (!Affects(world, s, owner, target))
        {
            return;
        }

        if (s.Effect is SpellEffect.Freeze or SpellEffect.Stone or SpellEffect.Confuse or SpellEffect.Hypnotize or SpellEffect.Kill && IsMagicImmune(world, target))
        {
            return; // the Messiah (WAR-010; ReTzar's reading of the original, 0x4ef200)
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
            case SpellEffect.Freeze:
            case SpellEffect.Confuse:
                ApplyStatus(world, s, caster, owner, target, until, deaths);
                break;

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

            case SpellEffect.Hypnotize:
            {
                // MAG-008 (ReTzar's reading of the original, 0x473887, 0x4e59b2, 0x4e5c4b): the caster's kingdom commands the
                // unit and it takes the caster's side until the effect ends; a new hypnosis restarts the time and the unit
                // still goes back to its first kingdom.
                byte from = world.Identities.Get(target).Owner;
                if (from == owner || !world.HasPlayer(owner))
                {
                    break;
                }

                ref StatusEffects st = ref StatusOf(world, target);
                if (st.HypnoUntil <= tick)
                {
                    st.HypnoOwner = from;
                }

                st.HypnoUntil = until;
                world.StopCasting(target);
                world.ChangeOwner(target, owner);
                GameplayCommands.ClearTasks(world, target);
                Interaction.Halt(world, target);
                break;
            }

            case SpellEffect.SoulBind:
                BindSoul(world, caster, target, s);
                break;
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
        if (amount <= 0 || world.IsStone(target) || world.IsHeld(target) || world.IsInvulnerable(world.Identities.Get(target).Owner)
            || IsMagicImmune(world, target)) // spells are magic: the Messiah takes none (WAR-010)
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

    // Teaching (TECH-028; ReTzar's reading of the original, 0x473bf0): mana turns into experience only for a unit of a lower
    // level than the caster's.
    private static bool Teachable(World world, SpellDefinition s, EntityId caster, EntityId target)
    {
        if (s.Effect != SpellEffect.Experience)
        {
            return true;
        }

        int teacher = world.Experiences.IndexOf(caster) is >= 0 and var c ? world.Experiences.At(c).Level : 0;
        return world.Experiences.IndexOf(target) is >= 0 and var t && world.Experiences.At(t).Level < teacher;
    }

    // Soul Bind (ITEM-010; ReTzar's reading of the original, 0x473403): the target keeps its owner; a soul item bound to it
    // goes into the caster's inventory, if there is room.
    private static void BindSoul(World world, EntityId caster, EntityId target, SpellDefinition s)
    {
        if (!world.Entities.IsAlive(caster) || world.Inventories.IndexOf(caster) is not (>= 0 and var inv)
            || world.Inventories.At(inv).Count >= world.TypeOf(caster).Traits.InventorySlots || world.Entities.RemainingCapacity < 1)
        {
            return;
        }

        Position at = world.Positions.Get(caster);
        EntityId gem = world.Spawn((ushort)s.Summon, Content.Compiled.MapObject.Nature, at.X, at.Y);
        world.Items.Get(gem).Bound = target.Value;
        world.PutInInventory(caster, gem);
        world.Events.Emit(new SimEvent(SimEventKind.Summoned, world.Identities.Get(caster).Owner, gem, caster.Index, 0, s.Summon));
    }

    /// <summary>
    /// A soul item's orders (ITEM-010; ReTzar's reading of the original, 0x465340): item_kill strikes its bound unit with
    /// 10,000 magic damage, item_berserk casts <c>combat.soulBerserkSpell</c>'s confusion on it. The item stays.
    /// </summary>
    internal static bool SoulOrder(World world, EntityId holder, EntityId gem, bool berserk)
    {
        EntityId bound = EntityId.FromValue(world.Items.Get(gem).Bound);
        if (world.Items.Get(gem).Bound == 0 || !world.Entities.IsAlive(bound) || !world.Healths.Has(bound))
        {
            return false;
        }

        byte owner = world.Identities.Get(holder).Owner;
        if (berserk)
        {
            int spell = world.Ruleset.Combat.SoulBerserkSpell;
            if (spell >= 0 && !IsMagicImmune(world, bound))
            {
                SpellDefinition s = world.Ruleset.Spells[spell];
                int tickMs = world.Config.TickLengthMilliseconds;
                ref StatusEffects st = ref StatusOf(world, bound);
                st.ConfuseUntil = world.Tick.Value + Math.Max(1, (s.DurationMs + tickMs - 1) / tickMs);
                world.Events.Emit(new SimEvent(SimEventKind.SpellEffect, world.Identities.Get(bound).Owner, bound, holder.Index, 0, s.Id));
            }

            return true;
        }

        var deaths = new List<Death>(1);
        Damage(world, holder, owner, bound, 10_000, deaths);
        CombatSystem.ProcessDeaths(world, deaths);
        return true;
    }

    // Blessing, freezing and confusion on the units around the point, within the spell's radius counted in tiles on the
    // larger axis (ReTzar's reading of the original: bless ±4 on units not hostile to the caster, 0x4e5809; freeze ±3 on
    // every unit, 0x432590; confusion ±2 on every unit but the caster, 0x473250).
    private static void StatusAround(World world, SpellDefinition s, EntityId caster, byte owner, int x, int y, int until, List<Death> deaths)
    {
        long r = s.RadiusUnits;
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<uint> hit = stackalloc uint[256];
        int n = 0;
        for (int i = 0; i < ids.Length && n < hit.Length; i++)
        {
            EntityId e = ids[i];
            Identity identity = world.Identities.Get(e);
            if (e == caster || !world.Entities.IsAlive(e) || world.Ruleset.EntityTypes[identity.TypeId].Traits.Category != EntityCategory.Unit
                || !s.AffectsType(identity.TypeId) || world.IsHeld(e) || world.Healths.IndexOf(e) is not (>= 0 and var h) || world.Healths.At(h).Current <= 0)
            {
                continue;
            }

            Position p = world.Positions.Get(e);
            if (Math.Max(Math.Abs((long)p.X - x), Math.Abs((long)p.Y - y)) > r
                || (s.Effect == SpellEffect.Bless && identity.Owner != owner && world.MayAttack(owner, e)))
            {
                continue;
            }

            hit[n++] = e.Value;
        }

        for (int k = 0; k < n; k++)
        {
            ApplyStatus(world, s, caster, owner, EntityId.FromValue(hit[k]), until, deaths);
        }
    }

    // One unit blessed (+amount attack), frozen (stopped, no defence, up to amount damage short of death) or confused.
    private static void ApplyStatus(World world, SpellDefinition s, EntityId caster, byte owner, EntityId target, int until, List<Death> deaths)
    {
        if (s.Effect != SpellEffect.Bless && IsMagicImmune(world, target))
        {
            return;
        }

        world.Events.Emit(new SimEvent(SimEventKind.SpellEffect, world.Identities.Get(target).Owner, target, caster.Index, 0, s.Id));
        ref StatusEffects st = ref StatusOf(world, target);
        switch (s.Effect)
        {
            case SpellEffect.Bless:
                st.BlessUntil = until;
                st.BlessPercent = (short)Math.Min(s.Amount, 1000);
                break;
            case SpellEffect.Freeze:
                st.FrozenUntil = Math.Max(st.FrozenUntil, until);
                GameplayCommands.ClearTasks(world, target);
                world.StopCasting(target);
                Interaction.Halt(world, target);
                if (s.Amount > 0 && world.Healths.Get(target).Current > 1)
                {
                    Damage(world, caster, owner, target, Math.Min(s.Amount, world.Healths.Get(target).Current - 1), deaths);
                }

                break;
            case SpellEffect.Confuse:
                st.ConfuseUntil = until;
                if (world.Combats.IndexOf(target) is >= 0 and var cb)
                {
                    world.Combats.At(cb).Target = 0;
                    world.Combats.At(cb).Mode = CombatMode.None;
                }

                break;
        }
    }

    /// <summary>Whether lasting spells do not take on <paramref name="unit"/> (the Messiah; WAR-010).</summary>
    internal static bool IsMagicImmune(World world, EntityId unit)
        => (world.TypeOf(unit).Traits.Abilities & AbilityFlags.MagicImmune) != 0;

    // The curse (ReTzar's reading of the original, 0x4e5841): every enemy unit the spell may affect within the caster's
    // sight of the point (the spell's radius when the caster has gone) loses half its attack and all its defence until
    // the effect ends. Cursing a cursed unit restarts the time.
    private static void CurseAround(World world, SpellDefinition s, EntityId caster, byte owner, int x, int y, int until)
    {
        long radius = s.RadiusUnits;
        if (world.Entities.IsAlive(caster) && world.Identities.IndexOf(caster) is >= 0 and var ci)
        {
            Identity self = world.Identities.At(ci);
            radius = world.Stats.Apply(self.Owner, ModifierStat.Sight, self.TypeId, world.Ruleset.EntityTypes[self.TypeId].Traits.SightUnits);
        }

        long r2 = radius * radius;
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            EntityId e = ids[i];
            if (e == caster || !Affects(world, s, owner, e) || !world.MayAttack(owner, e) || Interaction.DistanceSquaredTo(world, x, y, e) > r2)
            {
                continue;
            }

            if (IsMagicImmune(world, e))
            {
                continue;
            }

            world.Events.Emit(new SimEvent(SimEventKind.SpellEffect, world.Identities.Get(e).Owner, e, caster.Index, 0, s.Id));

            ref StatusEffects st = ref StatusOf(world, e);
            st.CurseUntil = until;
            st.CursePercent = (short)Math.Min(s.Amount, 100);
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

    // Raise Dead (necromancy): the oldest corpses within the radius of the point rise as the spell's units; only corpses of
    // the spell's target types (not machines or the bodiless; MAG-022).
    private static void RaiseDead(World world, EntityId caster, byte owner, SpellDefinition s, int x, int y)
    {
        EntityTypeDefinition type = world.Ruleset.EntityTypes[s.Summon];
        for (int n = 0; n < s.Count && world.Entities.RemainingCapacity >= 1; n++)
        {
            if (!world.Decals.TakeCorpse(x, y, s.RadiusUnits, s.TargetTypes, out Decal corpse))
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
    // caster's own units too, manual ch. 6). As ReTzar reads the original (0x472ddb, 0x450820, 0x4fd0e0): fire rain is twenty
    // fireballs, one every 170 ms, each falling 375 ms on a random tile up to 2 from the point and dealing 40 to what meets
    // the 5 x 5 tiles around it; a whirlpool deals 20 to the ships on its 3 x 3 tiles every 250 ms, steps one tile at random
    // every 2 s where it stays on water and is gone after 30 s.
    private void PulseAreas(World world, int tickMs)
    {
        _done.Clear();
        ReadOnlySpan<EntityId> ids = world.AreaEffects.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            ref AreaEffect a = ref world.AreaEffects.At(i);
            SpellDefinition s = world.Ruleset.Spells[a.Spell];
            int elapsed = Math.Min(tickMs, a.RemainingMs);
            int before = s.DurationMs - a.RemainingMs;
            a.RemainingMs -= elapsed;
            a.PulseAccumMs += elapsed;
            while (a.PulseAccumMs >= s.PulseMs)
            {
                a.PulseAccumMs -= s.PulseMs;
                Pulse(world, a, s);
            }

            // The moves due in this tick: every MoveMs since the cast (the time is the state: nothing more to keep).
            if (s.MoveMs > 0 && a.RemainingMs > 0)
            {
                for (int m = (before / s.MoveMs) + 1; m <= (s.DurationMs - a.RemainingMs) / s.MoveMs; m++)
                {
                    Step(world, ref a, s);
                }
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
        (int x, int y) = (a.X, a.Y);
        if (s.ScatterUnits > 0)
        {
            ref Core.Rng.Xoshiro256StarStar rng = ref world.Rng.Misc;
            x = Math.Clamp(x + rng.NextInt((2 * s.ScatterUnits) + 1) - s.ScatterUnits, 0, world.Map.WorldWidth - 1);
            y = Math.Clamp(y + rng.NextInt((2 * s.ScatterUnits) + 1) - s.ScatterUnits, 0, world.Map.WorldHeight - 1);
        }

        // A square area counts the tiles around the pulse's tile: what meets them is in.
        int tile = MapState.UnitsPerTile;
        if (s.Square)
        {
            (x, y) = (((x / tile) * tile) + (tile / 2), ((y / tile) * tile) + (tile / 2));
        }

        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            EntityId e = ids[i];
            bool inside = s.Square ? Interaction.ChebyshevTo(world, x, y, e) < s.RadiusUnits + (tile / 2) : Interaction.DistanceSquaredTo(world, x, y, e) <= r2;
            if (!inside || !Affects(world, s, a.Owner, e))
            {
                continue;
            }

            Damage(world, caster, a.Owner, e, s.Amount, _deaths);
        }
    }

    // One random step of a tile (each axis -1, 0 or +1), taken only when the whole square stays on water.
    private static void Step(World world, ref AreaEffect a, SpellDefinition s)
    {
        ref Core.Rng.Xoshiro256StarStar rng = ref world.Rng.Misc;
        int dx = rng.NextInt(3) - 1, dy = rng.NextInt(3) - 1;
        int tile = MapState.UnitsPerTile;
        if (SquareOnWater(world, (a.X / tile) + dx, (a.Y / tile) + dy, s.RadiusUnits / tile))
        {
            a.X += dx * tile;
            a.Y += dy * tile;
        }
    }

    /// <summary>Whether every tile within <paramref name="r"/> of tile (<paramref name="cx"/>, <paramref name="cy"/>) is water a ship can sail.</summary>
    internal static bool SquareOnWater(World world, int cx, int cy, int r)
    {
        for (int ty = cy - r; ty <= cy + r; ty++)
        {
            for (int tx = cx - r; tx <= cx + r; tx++)
            {
                if (!world.Map.ContainsTile(tx, ty) || !world.Map.IsPassable(Locomotor.Ship, tx, ty))
                {
                    return false;
                }
            }
        }

        return true;
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

            if (s.HypnoUntil > 0 && s.HypnoUntil <= tick)
            {
                _woken.Add((ids[i], s.HypnoOwner));
                s.HypnoUntil = 0;
                s.HypnoOwner = 0;
            }

            if (s.FrozenUntil <= tick && s.StoneUntil <= tick && s.CloakUntil <= tick && s.ConfuseUntil <= tick && s.BlessUntil <= tick && s.CurseUntil <= tick
                && s.HypnoUntil <= tick)
            {
                _done.Add(ids[i]);
            }
        }

        foreach (EntityId e in _done)
        {
            world.Statuses.Remove(e);
        }

        // A hypnotized unit goes back to its kingdom (if it still plays), its orders dropped.
        foreach ((EntityId unit, byte owner) in _woken)
        {
            if (world.Entities.IsAlive(unit) && world.HasPlayer(owner) && world.Identities.Get(unit).Owner != owner)
            {
                world.StopCasting(unit);
                world.ChangeOwner(unit, owner);
                GameplayCommands.ClearTasks(world, unit);
                Interaction.Halt(world, unit);
            }
        }

        _woken.Clear();
    }

    /// <summary>The time between two crusaders arriving at the anchor (<see cref="CombatSettings.CrusaderArrivalMs"/>), in ticks.</summary>
    public static int CrusaderArrivalTicks(CombatSettings combat, int tickMs) => Math.Max(1, combat.CrusaderArrivalMs / Math.Max(1, tickMs));

    /// <summary>The most crusaders <paramref name="player"/>'s cathedrals gather: the start, plus a step per full crusade.</summary>
    public static int CrusadeCeiling(World world, int player)
    {
        CombatSettings c = world.Ruleset.Combat;
        return Math.Min(c.CrusadeCeilingMax, c.CrusadeStart + (c.CrusadeCeilingStep * world.Players[player].FullCrusades));
    }

    // Each finished cathedral gathers one crusader for its kingdom, up to the ceiling, while no crusade of the kingdom runs,
    // at the pace of combat.crusadeGather for the time played (MAG-017). Checked once a second.
    private static void GatherCrusaders(World world, int tick)
    {
        int tickMs = world.Config.TickLengthMilliseconds;
        int second = Math.Max(1, 1000 / tickMs);
        if (tick % second != 0)
        {
            return;
        }

        long elapsedMs = (long)tick * tickMs;
        ImmutableArray<CrusadeGatherStep> steps = world.Ruleset.Combat.CrusadeGather;
        int every = steps[0].EverySeconds;
        for (int s = 1; s < steps.Length && elapsedMs >= steps[s].FromMinute * 60_000L; s++)
        {
            every = steps[s].EverySeconds;
        }

        long seconds = tick / second;
        ReadOnlySpan<EntityId> structures = world.Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            EntityId cathedral = structures[i];
            if ((world.TypeOf(cathedral).Traits.Abilities & AbilityFlags.PreachCrusade) == 0 || world.Constructions.Has(cathedral)
                || (seconds + cathedral.Index) % every != 0)
            {
                continue;
            }

            byte owner = world.Identities.Get(cathedral).Owner;
            if (!world.HasPlayer(owner) || CrusadeRuns(world, owner))
            {
                continue;
            }

            ref PlayerState kingdom = ref world.Players[owner];
            if (kingdom.Crusaders < CrusadeCeiling(world, owner))
            {
                kingdom.Crusaders++;
            }
        }
    }

    private static bool CrusadeRuns(World world, byte owner)
    {
        foreach (EntityId anchor in world.Crusades.Entities)
        {
            if (world.Identities.Get(anchor).Owner == owner)
            {
                return true;
            }
        }

        return false;
    }

    // Crusade anchors call their crusaders one every 500 ms (while there is room); once all have come and died the anchor
    // goes and the kingdom gathers again from crusadeRestart (MAG-016, MAG-017).
    private void Crusades(World world, int tick)
    {
        _done.Clear();
        CombatSettings settings = world.Ruleset.Combat;
        GatherCrusaders(world, tick);
        ReadOnlySpan<EntityId> ids = world.Crusades.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            ref CrusadeState s = ref world.Crusades.At(i);
            byte owner = world.Identities.Get(ids[i]).Owner;
            if (s.ToRaise > 0 && tick < s.NextTick)
            {
                continue;
            }

            if (s.ToRaise > 0 && settings.Crusader >= 0 && world.Entities.RemainingCapacity >= 1)
            {
                s.NextTick = tick + CrusaderArrivalTicks(world.Ruleset.Combat, world.Config.TickLengthMilliseconds);
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

            if (!alive && s.ToRaise == 0)
            {
                _done.Add(ids[i]);
            }
        }

        foreach (EntityId e in _done)
        {
            byte owner = world.Identities.Get(e).Owner;
            world.Destroy(e);
            if (world.HasPlayer(owner))
            {
                world.Players[owner].Crusaders = (ushort)Math.Min(settings.CrusadeRestart, CrusadeCeiling(world, owner));
            }
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
            ItemDefinition def = world.Ruleset.EntityTypes[job.ItemType].Traits.Item!;
            CraftVariant v = def.CraftVariantAt(job.Variant);
            CombatSettings settings = world.Ruleset.Combat;
            ref ItemState state = ref world.Items.Get(item);
            state.Effect = v.Effect;
            state.Spell = (ushort)(v.Spell + 1);
            state.Look = (byte)v.Look;

            // The gold buys power: points of attack or defence, or a potion's hit points, mana or levels, or a book's
            // uses (ITEM-016; the scales are UNVERIFIED, UNK-25).
            state.Power = v.Effect switch
            {
                ItemEffect.Health => job.Power * settings.CraftHealthPerPower,
                ItemEffect.Mana => job.Power * settings.CraftManaPerPower,
                ItemEffect.Levels => Math.Max(1, job.Power / settings.CraftPowerPerLevel),
                _ => job.Power,
            };
            if (def.Kind == ItemKind.SpellBook && def.Charges > 0)
            {
                state.Charges = job.Power;
            }

            world.Events.Emit(new SimEvent(SimEventKind.ItemCrafted, world.Identities.Get(workshop).Owner, item, workshop.Index, 0, job.ItemType));
        }
    }
}
