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
/// Pipeline step 9 (TDD §8.5, GAME-07; ADR 0038): target acquisition, chasing, the swing cycle and damage, then deaths.
/// </summary>
/// <remarks>
/// <para><b>Acquisition</b> (staggered, §8.9): an idle armed unit with <c>AutoAcquire</c>, or one on a move-advance,
/// scans every <c>acquireIntervalMs</c> (on ticks where <c>(index + tick)</c> hits its slot) for the nearest enemy in
/// sight: smallest edge distance, ties to the lower entity index. Candidates come from a uniform grid of 4×4-tile
/// cells rebuilt every tick (counting sort, ascending index per cell).</para>
/// <para><b>Swing cycle</b> (UNK-07): in reach, a swing starts when the cooldown is over; the hit lands
/// <c>HitDelayMs</c> later if the target is still in reach; the next swing waits <c>CooldownMs</c> from the start of the
/// previous one (carry-over). Out of reach the unit chases (a path request at most every
/// <see cref="ChaseIntervalTicks"/>).</para>
/// <para><b>Damage</b> (UNK-08, placeholder): <c>max(minimumDamage, attack × (1 + bonus) − defence)</c>, the bonus from
/// <c>BONUS.INI</c> in basis points, truncated toward zero. Hits apply in ascending attacker order; an entity whose
/// health drops to 0 is killed after the pass (units leave a corpse that never blocks, ORIG-01).</para>
/// </remarks>
public sealed class CombatSystem : ISimSystem
{
    /// <summary>Grid cell side in world units (4 tiles, §8.9).</summary>
    public const int CellSize = 4 * MapState.UnitsPerTile;

    /// <summary>Least ticks between two chase path requests of one unit.</summary>
    public const int ChaseIntervalTicks = 10;

    /// <summary>A chased target that moved further than this from the last chase point triggers a new request.</summary>
    public const int ChaseRetargetUnits = MapState.UnitsPerTile;

    /// <summary>An auto-acquired target further than this many times the sight radius is dropped.</summary>
    public const int LeashFactor = 2;

    /// <summary>Owner bit of Nature in the per-cell owner masks (player slots use bits 0-15).</summary>
    private const int NatureBit = 31;

    private readonly List<(EntityId Attacker, EntityId Target)> _hits = [];
    private readonly List<(EntityId Victim, EntityId Killer)> _deaths = [];
    private int[] _cellStart = [];
    private int[] _cellItems = [];
    private uint[] _cellOwners = [];
    private int _gridW;
    private int _gridH;

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Combat;

    /// <inheritdoc />
    public string Name => "combat";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        if (world.Combats.Count == 0)
        {
            return;
        }

        BuildGrid(world);
        int tick = world.Tick.Value;
        int tickMs = world.Config.TickLengthMilliseconds;
        int acquireEvery = Math.Max(1, world.Ruleset.Gameplay.AcquireIntervalMs / tickMs);
        _hits.Clear();
        _deaths.Clear();

        // Pass 1 decides on the state at the start of the step: targets, chases, swings and the hits that land now.
        for (int i = 0; i < world.Combats.Count; i++)
        {
            Step(world, world.Combats.Entities[i], ref world.Combats.At(i), tick, tickMs, acquireEvery);
        }

        // Pass 2 applies the hits, all of them (simultaneous resolution: two units that strike each other in the same
        // tick can both die, whatever their entity indices), then pass 3 removes the dead.
        foreach ((EntityId attacker, EntityId target) in _hits)
        {
            Hit(world, attacker, target);
        }

        foreach ((EntityId victim, EntityId killer) in _deaths)
        {
            if (!world.Entities.IsAlive(victim))
            {
                continue;
            }

            Identity identity = world.Identities.Get(victim);
            world.Events.Emit(new SimEvent(SimEventKind.Killed, identity.Owner, victim, killer.Index, 0, identity.TypeId));
            world.Kill(victim);
        }
    }

    private void Step(World world, EntityId attacker, ref Combat c, int tick, int tickMs, int acquireEvery)
    {
        EntityTypeDefinition type = world.TypeOf(attacker);
        ArmamentDefinition arm = type.Traits.Armament!;
        Identity self = world.Identities.Get(attacker);
        Position position = world.Positions.Get(attacker);
        int radius = Interaction.RadiusOf(world, attacker);
        EntityId target = EntityId.FromValue(c.Target);
        if (c.Target != 0 && !IsEnemyTarget(world, self.Owner, target) && !(c.Mode == CombatMode.Explicit && IsButcherTarget(world, self.Owner, target)))
        {
            c.Target = 0;
            c.HitInMs = -1;
            target = EntityId.None;
            if (c.Mode is CombatMode.Explicit or CombatMode.Auto)
            {
                c.Mode = CombatMode.None;
            }
        }

        if (target.IsNone)
        {
            c.CooldownMs = Math.Max(0, c.CooldownMs - tickMs);
            bool idle = !Interaction.IsMoving(world, attacker) && !HasTask(world, attacker);
            bool scan = c.Mode == CombatMode.AttackMove || (c.Mode == CombatMode.None && arm.AutoAcquire && idle);
            if (scan && (attacker.Index + tick) % acquireEvery == 0)
            {
                target = Acquire(world, self.Owner, position, radius, Math.Max(Sight(world, self, type), Range(world, attacker, self, type, arm)));
                if (!target.IsNone)
                {
                    c.Target = target.Value;
                    c.ChaseTick = -1;
                    if (c.Mode == CombatMode.None)
                    {
                        c.Mode = CombatMode.Auto;
                    }
                    else
                    {
                        c.Resume = 1; // move-advance: continue to the destination after this fight
                    }
                }
            }

            if (target.IsNone)
            {
                ResumeAttackMove(world, attacker, ref c);
                return;
            }
        }

        int distance = Interaction.EdgeDistance(world, position, radius, target);
        // Hand-to-hand attackers hit a building from the tiles next to it, like workers do (interaction reach).
        int range = Range(world, attacker, self, type, arm);
        int reach = range > 0 ? range
            : world.Footprints.Has(target) ? world.Ruleset.Gameplay.InteractionReachUnits
            : world.Ruleset.Gameplay.MeleeReachUnits;
        if (distance <= reach)
        {
            Interaction.Halt(world, attacker);
            Position tp = world.Positions.Get(target);
            if (tp.X != position.X || tp.Y != position.Y)
            {
                world.Identities.Get(attacker).Facing = (byte)Trig.Atan2(tp.Y - position.Y, tp.X - position.X).ToDirection8();
            }

            // A swing starts when the cooldown is over and lands HitDelayMs later; the cooldown runs from the start of the
            // swing, with carry-over when the cycle is not a whole number of ticks.
            if (c.HitInMs >= 0)
            {
                c.HitInMs -= tickMs;
                if (c.HitInMs <= 0)
                {
                    c.HitInMs = -1;
                    _hits.Add((attacker, target));
                }
            }

            if (c.HitInMs < 0 && c.CooldownMs <= 0 && world.Healths.Get(target).Current > 0)
            {
                c.CooldownMs += arm.CooldownMs;
                c.HitInMs = arm.HitDelayMs;
                if (c.HitInMs == 0)
                {
                    c.HitInMs = -1;
                    _hits.Add((attacker, target));
                }
            }

            // At most one swing starts per tick, so a cycle shorter than a tick keeps no growing debt.
            c.CooldownMs = Math.Max(c.CooldownMs - tickMs, -tickMs);
            return;
        }

        // Out of reach: the swing is lost.
        c.HitInMs = -1;
        c.CooldownMs = Math.Max(0, c.CooldownMs - tickMs);
        long sight = Math.Max(Sight(world, self, type), range);
        bool mobile = world.Motions.IndexOf(attacker) is >= 0 and var m && world.Motions.At(m).SpeedPerTick > 0;
        if (!mobile || (c.Mode == CombatMode.Auto && distance > sight * LeashFactor) || (c.Mode == CombatMode.AttackMove && distance > sight * LeashFactor))
        {
            c.Target = 0;
            c.Mode = c.Mode == CombatMode.AttackMove ? CombatMode.AttackMove : CombatMode.None;
            return;
        }

        Chase(world, attacker, ref c, target, position, type, tick);
    }

    private static void Chase(World world, EntityId attacker, ref Combat c, EntityId target, Position position, EntityTypeDefinition type, int tick)
    {
        Position tp = world.Positions.Get(target);
        bool moving = Interaction.IsMoving(world, attacker);
        bool due = c.ChaseTick < 0 || tick - c.ChaseTick >= ChaseIntervalTicks;
        bool moved = IntMath.DistanceSquared(tp.X, tp.Y, c.ChaseX, c.ChaseY) > (long)ChaseRetargetUnits * ChaseRetargetUnits;
        if (!due || (moving && !moved))
        {
            return;
        }

        int x = tp.X;
        int y = tp.Y;
        if (world.Footprints.Has(target) && !Interaction.ApproachPoint(world, target, position.X, position.Y, type.Locomotor, out x, out y))
        {
            (x, y) = (tp.X, tp.Y);
        }

        c.ChaseX = tp.X;
        c.ChaseY = tp.Y;
        c.ChaseTick = tick;
        ref Motion motion = ref world.Motions.Get(attacker);
        int orderX = motion.OrderX;
        int orderY = motion.OrderY;
        Interaction.MoveTo(world, attacker, x, y);
        if (c.Mode == CombatMode.AttackMove)
        {
            // Keep the move-advance destination to resume it after the fight.
            motion.OrderX = orderX;
            motion.OrderY = orderY;
        }
    }

    // A move-advance ends when the unit stops without having fought since its last (re)start; after a fight it resumes
    // its destination once, unless it is already there.
    private static void ResumeAttackMove(World world, EntityId attacker, ref Combat c)
    {
        if (c.Mode != CombatMode.AttackMove || Interaction.IsMoving(world, attacker))
        {
            return;
        }

        ref Motion motion = ref world.Motions.Get(attacker);
        Position p = world.Positions.Get(attacker);
        long near = 2L * MapState.UnitsPerTile;
        if (c.Resume == 0 || IntMath.DistanceSquared(p.X, p.Y, motion.OrderX, motion.OrderY) <= near * near)
        {
            c.Mode = CombatMode.None;
            c.Resume = 0;
            return;
        }

        c.Resume = 0;
        int x = motion.OrderX;
        int y = motion.OrderY;
        Interaction.MoveTo(world, attacker, x, y);
    }

    private void Hit(World world, EntityId attacker, EntityId target)
    {
        ref Health health = ref world.Healths.Get(target);
        if (health.Current <= 0)
        {
            return; // already killed by an earlier hit of this tick
        }

        EntityTypeDefinition type = world.TypeOf(attacker);
        byte attackerOwner = world.Identities.Get(attacker).Owner;
        EntityTypeDefinition targetType = world.TypeOf(target);
        byte targetOwner = world.Identities.Get(target).Owner;
        int bonus = world.Ruleset.BonusBasisPoints(type.Id, targetType.Id);

        // Technology effects (ADR 0040): the attacker's owner's attack, the target's owner's defence and resistance; ship
        // cannon levels on the attacker itself.
        int attack = ResearchRules.LevelBonus(world, attacker, ModifierStat.Attack, world.Stats.Apply(attackerOwner, ModifierStat.Attack, type.Id, type.Traits.Armament!.Attack));
        int defence = world.Stats.Apply(targetOwner, ModifierStat.Defence, targetType.Id, targetType.Traits.Defence);
        int resistance = world.Stats.Apply(targetOwner, ModifierStat.Resistance, targetType.Id, targetType.Traits.Resistance);
        long damage = ((long)attack * (10_000 + bonus) / 10_000) - defence;
        if (resistance > 0)
        {
            // Buildings: RESISTANCE reduces the damage (manual ch. 4; the scale is UNVERIFIED, UNK-08).
            int scale = world.Ruleset.Gameplay.ResistanceScale;
            damage = damage * scale / (scale + resistance);
        }

        int dealt = (int)Math.Clamp(damage, world.Ruleset.Gameplay.MinimumDamage, int.MaxValue);
        health.Current = (int)Math.Max(health.Current - (long)dealt, 0);
        Identity victim = world.Identities.Get(target);
        world.Events.Emit(new SimEvent(SimEventKind.Damaged, victim.Owner, target, attacker.Index, health.Current, dealt));
        if (health.Current == 0)
        {
            _deaths.Add((target, attacker));
            return;
        }

        // Retaliation: an idle armed unit that is hit fights back.
        int k = world.Combats.IndexOf(target);
        if (k >= 0)
        {
            ref Combat other = ref world.Combats.At(k);
            if (other.Target == 0 && other.Mode == CombatMode.None && targetType.Traits.Armament!.AutoAcquire
                && !Interaction.IsMoving(world, target) && !HasTask(world, target))
            {
                other.Target = attacker.Value;
                other.Mode = CombatMode.Auto;
                other.ChaseTick = -1;
            }
        }
    }

    private static int Range(World world, EntityId attacker, Identity self, EntityTypeDefinition type, ArmamentDefinition arm)
        => arm.RangeUnits <= 0 ? 0 : ResearchRules.LevelBonus(world, attacker, ModifierStat.Range, world.Stats.Apply(self.Owner, ModifierStat.Range, type.Id, arm.RangeUnits));

    private static int Sight(World world, Identity self, EntityTypeDefinition type)
        => world.Stats.Apply(self.Owner, ModifierStat.Sight, type.Id, type.Traits.SightUnits);

    // An own animal that leaves meat (a cow) may be attacked on an explicit order: butchering (GAME-02, ADR 0042).
    private static bool IsButcherTarget(World world, byte owner, EntityId target)
        => world.Entities.IsAlive(target) && world.Identities.IndexOf(target) is >= 0 and var i && world.Identities.At(i).Owner == owner
            && world.Ruleset.EntityTypes[world.Identities.At(i).TypeId].Traits.SpawnOnDeath >= 0 && world.Healths.Get(target).Current > 0;

    private static bool HasTask(World world, EntityId entity)
    {
        int w = world.Workers.IndexOf(entity);
        return w >= 0 && world.Workers.At(w).Task != WorkerTask.None;
    }

    // A live unit or building of another owner. Nature's units (animals, wild creatures) are hostile to every kingdom
    // and can be fought; resources never are.
    private static bool IsEnemyTarget(World world, byte owner, EntityId target)
    {
        if (!world.Entities.IsAlive(target))
        {
            return false;
        }

        int i = world.Identities.IndexOf(target);
        if (i < 0)
        {
            return false;
        }

        Identity identity = world.Identities.At(i);
        return identity.Owner != owner && IsCombatant(world, identity) && world.Healths.Get(target).Current > 0;
    }

    private static bool IsCombatant(World world, Identity identity)
        => world.Ruleset.EntityTypes[identity.TypeId].Traits.Category is EntityCategory.Unit or EntityCategory.Building;

    private static uint OwnerBit(byte owner) => owner == MapObject.Nature ? 1u << NatureBit : 1u << (owner & 15);

    private EntityId Acquire(World world, byte owner, Position position, int radius, long sight)
    {
        EntityId best = EntityId.None;
        long bestDistance = long.MaxValue;

        // Structures are bucketed by their center; their footprint reaches at most half the largest side further.
        long slack = (EntityTypeDefinition.MaxFootprintTiles / 2 * MapState.UnitsPerTile) + EntityTypeDefinition.MaxRadius;
        int reach = (int)Math.Min(sight + slack, int.MaxValue / 2);
        int cx0 = Math.Max(0, (position.X - reach) / CellSize);
        int cy0 = Math.Max(0, (position.Y - reach) / CellSize);
        int cx1 = Math.Min(_gridW - 1, (position.X + reach) / CellSize);
        int cy1 = Math.Min(_gridH - 1, (position.Y + reach) / CellSize);
        uint enemies = ~OwnerBit(owner);
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int cell = (cy * _gridW) + cx;
                if ((_cellOwners[cell] & enemies) == 0)
                {
                    continue; // nobody but the scanner's own kingdom here
                }

                // Skip cells that cannot hold anything nearer than what was found (or than the sight radius).
                long dx = Math.Max(0, Math.Max((long)cx * CellSize - position.X, position.X - (((long)cx + 1) * CellSize)));
                long dy = Math.Max(0, Math.Max((long)cy * CellSize - position.Y, position.Y - (((long)cy + 1) * CellSize)));
                long limit = Math.Min(bestDistance, sight) + slack + radius;
                if ((dx * dx) + (dy * dy) > limit * limit)
                {
                    continue;
                }

                for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
                {
                    int dense = _cellItems[k];
                    if (identities[dense].Owner == owner)
                    {
                        continue;
                    }

                    EntityId candidate = ids[dense];
                    if (world.Healths.Get(candidate).Current <= 0)
                    {
                        continue;
                    }

                    long d = Interaction.EdgeDistance(world, position, radius, candidate);
                    if (d <= sight && (d < bestDistance || (d == bestDistance && candidate.Index < best.Index)))
                    {
                        bestDistance = d;
                        best = candidate;
                    }
                }
            }
        }

        return best;
    }

    // Buckets the combatants (units and buildings; not resources) by position (structures by their center) with a
    // stable counting sort, and records which owners each cell holds.
    private void BuildGrid(World world)
    {
        _gridW = (world.Map.WorldWidth + CellSize - 1) / CellSize;
        _gridH = (world.Map.WorldHeight + CellSize - 1) / CellSize;
        int cells = _gridW * _gridH;
        if (_cellStart.Length < cells + 1)
        {
            _cellStart = new int[cells + 1];
            _cellOwners = new uint[cells];
            _cursor = new int[cells];
        }

        Array.Clear(_cellStart, 0, cells + 1);
        Array.Clear(_cellOwners, 0, cells);
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        if (_cellOf.Length < ids.Length)
        {
            _cellOf = new int[Math.Max(ids.Length, _cellOf.Length * 2)];
            _cellItems = new int[_cellOf.Length];
        }

        for (int i = 0; i < ids.Length; i++)
        {
            if (!IsCombatant(world, identities[i]))
            {
                _cellOf[i] = -1;
                continue;
            }

            Position p = world.Positions.Get(ids[i]);
            int cell = (Math.Clamp(p.Y / CellSize, 0, _gridH - 1) * _gridW) + Math.Clamp(p.X / CellSize, 0, _gridW - 1);
            _cellOf[i] = cell;
            _cellStart[cell + 1]++;
            _cellOwners[cell] |= OwnerBit(identities[i].Owner);
        }

        for (int c = 0; c < cells; c++)
        {
            _cellStart[c + 1] += _cellStart[c];
        }

        Array.Copy(_cellStart, _cursor, cells);
        for (int i = 0; i < ids.Length; i++)
        {
            if (_cellOf[i] >= 0)
            {
                _cellItems[_cursor[_cellOf[i]]++] = i;
            }
        }
    }

    private int[] _cellOf = [];
    private int[] _cursor = [];
}
