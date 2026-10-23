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
/// Pipeline step 9 (TDD §8.5, GAME-07; ADRs 0038, 0091): projectiles in flight, target acquisition, chasing, the swing
/// cycle, then all of the tick's hits at once, then deaths.
/// </summary>
/// <remarks>
/// <para><b>Acquisition</b> (staggered, §8.9): an idle armed unit with <c>AutoAcquire</c>, or one on a move-advance,
/// scans every <c>acquireIntervalMs</c> (on ticks where <c>(index + tick)</c> hits its slot) for the nearest enemy in
/// sight: smallest edge distance, ties to the lower entity index. Candidates come from a uniform grid of 4×4-tile
/// cells rebuilt every tick (counting sort, ascending index per cell). Units inside holders, stone and cloaked units
/// are never candidates; a confused unit takes everyone as an enemy and everyone takes it as one.</para>
/// <para><b>Stances</b>: stand ground fights only what is in reach (no chasing); hold fire never starts a fight by
/// itself. Frozen and stone units do nothing.</para>
/// <para><b>Swing cycle</b> (UNK-07): in reach (and not inside <c>min_attack_range</c>), a swing starts when the cooldown is
/// over; the hit lands <c>HitDelayMs</c> later if the target is still in reach; the next swing waits <c>CooldownMs</c>
/// from the start of the previous one (carry-over). A ranged attacker with a projectile launches it at the hit frame;
/// the projectile flies at its speed and hits on arrival (homing ones follow the target, others land where the target
/// stood and hurt what is around: splash).</para>
/// <para><b>Damage</b> (UNK-08, placeholder): <c>max(minimumDamage, attack × (1 + bonus) − defence)</c>, attack and defence
/// after technologies, levels, items, blessings and curses (<see cref="CombatStats"/>); buildings then by resistance; a
/// holder keeps its <c>[damage]</c> share and spreads the rest over the units inside. All hits of a tick apply together
/// (simultaneous resolution, ADR 0038); deaths follow (<see cref="SimEventKind.Killed"/>: X = killer index, Y = killer
/// owner).</para>
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

    private readonly List<HitRecord> _hits = [];
    private readonly List<Death> _deaths = [];
    private readonly List<EntityId> _spent = [];
    private readonly List<EntityId> _splash = [];
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
        if (world.Combats.Count == 0 && world.Projectiles.Count == 0)
        {
            return;
        }

        BuildGrid(world);
        int tick = world.Tick.Value;
        int tickMs = world.Config.TickLengthMilliseconds;
        int acquireEvery = Math.Max(1, world.Ruleset.Gameplay.AcquireIntervalMs / tickMs);
        _hits.Clear();
        _deaths.Clear();
        _spent.Clear();

        // Pass 0: projectiles fired in earlier ticks fly; arrivals become hits of this tick.
        FlyProjectiles(world, tickMs);

        // Pass 1 decides on the state at the start of the step: targets, chases, swings and the hits that land now.
        for (int i = 0; i < world.Combats.Count; i++)
        {
            EntityId attacker = world.Combats.Entities[i];
            if (world.IsHeld(attacker))
            {
                continue; // inside a holder: no fighting (GAME-09)
            }

            Step(world, attacker, ref world.Combats.At(i), tick, tickMs, acquireEvery);
        }

        foreach (EntityId p in _spent)
        {
            world.Destroy(p);
        }

        // Pass 2 applies the hits, all of them (simultaneous resolution: two units that strike each other in the same
        // tick can both die, whatever their entity indices), then pass 3 removes the dead.
        foreach (HitRecord hit in _hits)
        {
            Apply(world, hit, _deaths);
            Banshee(world, hit, _deaths);
        }

        ProcessDeaths(world, _deaths);
    }

    /// <summary>
    /// Kills every victim at 0 hit points, in order (<see cref="SimEventKind.Killed"/> with the killer's index and owner),
    /// crediting the killers' kill experience (GAME-08).
    /// </summary>
    internal static void ProcessDeaths(World world, List<Death> deaths)
    {
        foreach (Death d in deaths)
        {
            if (!world.Entities.IsAlive(d.Victim) || world.Healths.Get(d.Victim).Current > 0)
            {
                continue;
            }

            Identity identity = world.Identities.Get(d.Victim);
            world.Events.Emit(new SimEvent(SimEventKind.Killed, identity.Owner, d.Victim, d.Killer.Index, d.KillerOwner, identity.TypeId));
            if (world.Entities.IsAlive(d.Killer) && world.Experiences.IndexOf(d.Killer) is >= 0 and var x && !Allies(world, d.KillerOwner, identity.Owner))
            {
                world.Experiences.At(x).PendingXp += world.Ruleset.Combat.ExperiencePerKill;
            }

            world.Kill(d.Victim);
        }

        deaths.Clear();
    }

    private void Step(World world, EntityId attacker, ref Combat c, int tick, int tickMs, int acquireEvery)
    {
        EntityTypeDefinition type = world.TypeOf(attacker);
        ArmamentDefinition arm = type.Traits.Armament!;
        Identity self = world.Identities.Get(attacker);
        Position position = world.Positions.Get(attacker);
        if (!world.CanAct(attacker))
        {
            // Frozen or stone (GAME-11): no swing, no chase; the cooldown keeps running.
            c.HitInMs = -1;
            c.CooldownMs = Math.Max(0, c.CooldownMs - tickMs);
            Interaction.Halt(world, attacker);
            return;
        }

        CombatStance stance = StanceOf(world, attacker);
        if (c.Mode == CombatMode.Ground)
        {
            GroundStep(world, attacker, ref c, type, arm, self, position, tick, tickMs);
            return;
        }

        int radius = Interaction.RadiusOf(world, attacker);
        EntityId target = EntityId.FromValue(c.Target);
        if (c.Target != 0 && !IsHostileTarget(world, attacker, self.Owner, target)
            && !(c.Mode == CombatMode.Explicit && (IsButcherTarget(world, self.Owner, target) || IsTrainingTarget(world, self.Owner, target)))
            && !(c.Mode == CombatMode.Scripted && target != attacker && IsTargetable(world, target)))
        {
            c.Target = 0;
            c.HitInMs = -1;
            target = EntityId.None;
            if (c.Mode is CombatMode.Explicit or CombatMode.Auto or CombatMode.Scripted)
            {
                c.Mode = CombatMode.None;
            }

            // The fight is over: the next of the unit's queued orders (CTRL-026); a move-advance resumes first, and a
            // unit still walking takes it on arrival.
            if (c.Mode != CombatMode.AttackMove && world.WaypointQueues.Count > 0 && world.WaypointQueues.Has(attacker) && !Interaction.IsMoving(world, attacker))
            {
                OrderQueue.Next(world, attacker);
                target = EntityId.FromValue(c.Target);
            }
        }

        if (target.IsNone)
        {
            c.CooldownMs = Math.Max(0, c.CooldownMs - tickMs);
            bool idle = !Interaction.IsMoving(world, attacker) && !HasTask(world, attacker);
            // A guard (follow order) watches for enemies while it walks with what it guards.
            bool guarding = world.Motions.IndexOf(attacker) is >= 0 and var gm && world.Motions.At(gm).Order == MotionOrder.Follow;
            bool scan = c.Mode == CombatMode.AttackMove || (c.Mode == CombatMode.None && arm.AutoAcquire && (idle || guarding) && stance != CombatStance.HoldFire);
            if (scan && (attacker.Index + tick) % acquireEvery == 0)
            {
                long sight = Math.Max(Sight(world, self, type), Range(world, attacker, self, type, arm));
                if (stance == CombatStance.StandGround)
                {
                    sight = Math.Max(Range(world, attacker, self, type, arm), world.Ruleset.Gameplay.MeleeReachUnits);
                }

                target = Acquire(world, attacker, self.Owner, position, radius, sight, arm.MinRangeUnits, PlaneOf(type), Range(world, attacker, self, type, arm));
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
            : world.Footprints.Has(target) ? (Interaction.IsMoving(world, attacker) ? world.Ruleset.Gameplay.InteractionTouchUnits : world.Ruleset.Gameplay.InteractionReachUnits)
            : world.Ruleset.Gameplay.MeleeReachUnits;
        if (distance <= reach && distance >= arm.MinRangeUnits)
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
                    Strike(world, attacker, type, arm, self, position, range, target, tp.X, tp.Y);
                }
            }

            if (c.HitInMs < 0 && c.CooldownMs <= 0 && world.Healths.Get(target).Current > 0)
            {
                c.CooldownMs += Cycle(world, attacker, arm);
                c.HitInMs = arm.HitDelayMs;
                Uncloak(world, attacker); // attacking reveals an invisible unit (1.10 fix); a disguise stays (the owner)
                if (c.HitInMs == 0)
                {
                    c.HitInMs = -1;
                    Strike(world, attacker, type, arm, self, position, range, target, tp.X, tp.Y);
                }
            }

            // At most one swing starts per tick, so a cycle shorter than a tick keeps no growing debt.
            c.CooldownMs = Math.Max(c.CooldownMs - tickMs, -tickMs);
            return;
        }

        // Out of reach (or too close for a minimum range): the swing is lost.
        c.HitInMs = -1;
        c.CooldownMs = Math.Max(0, c.CooldownMs - tickMs);
        // A unit may always chase what can hit it from where it stands (a tower that outranges its sight).
        long leash = Math.Max(Math.Max(Sight(world, self, type), range), ReachOf(world, target));
        bool mobile = world.Motions.IndexOf(attacker) is >= 0 and var m && world.Motions.At(m).SpeedPerTick > 0;
        bool tooClose = distance < arm.MinRangeUnits;
        bool across = IsAcrossShore(PlaneOf(type), PlaneOf(world.TypeOf(target))); // REQ-PATH-09: no chasing across the shore
        if (!mobile || (c.Mode == CombatMode.Auto && (distance > leash * LeashFactor || stance == CombatStance.StandGround || tooClose || across))
            || (c.Mode == CombatMode.AttackMove && (distance > leash * LeashFactor || tooClose || across)))
        {
            c.Target = 0;
            c.Mode = c.Mode == CombatMode.AttackMove ? CombatMode.AttackMove : CombatMode.None;
            return;
        }

        if (tooClose)
        {
            return; // an explicit target inside the minimum range: the unit waits (UNVERIFIED: no backing off)
        }

        Chase(world, attacker, ref c, target, position, type, tick);
    }

    // Attack ground (siege, order attack_ground): fire at the point while in range; move into range otherwise.
    private void GroundStep(World world, EntityId attacker, ref Combat c, EntityTypeDefinition type, ArmamentDefinition arm, Identity self, Position position, int tick, int tickMs)
    {
        int range = Range(world, attacker, self, type, arm);
        long d = IntMath.ISqrt(IntMath.DistanceSquared(position.X, position.Y, c.ChaseX, c.ChaseY));
        if (d <= range && d >= arm.MinRangeUnits)
        {
            Interaction.Halt(world, attacker);
            if (c.HitInMs >= 0)
            {
                c.HitInMs -= tickMs;
                if (c.HitInMs <= 0)
                {
                    c.HitInMs = -1;
                    Strike(world, attacker, type, arm, self, position, range, EntityId.None, c.ChaseX, c.ChaseY);
                }
            }

            if (c.HitInMs < 0 && c.CooldownMs <= 0)
            {
                c.CooldownMs += Cycle(world, attacker, arm);
                c.HitInMs = arm.HitDelayMs;
                if (c.HitInMs == 0)
                {
                    c.HitInMs = -1;
                    Strike(world, attacker, type, arm, self, position, range, EntityId.None, c.ChaseX, c.ChaseY);
                }
            }

            c.CooldownMs = Math.Max(c.CooldownMs - tickMs, -tickMs);
            return;
        }

        c.HitInMs = -1;
        c.CooldownMs = Math.Max(0, c.CooldownMs - tickMs);
        if (d > range && !Interaction.IsMoving(world, attacker) && (c.ChaseTick < 0 || tick - c.ChaseTick >= ChaseIntervalTicks))
        {
            c.ChaseTick = tick;
            int x = c.ChaseX;
            int y = c.ChaseY;
            Interaction.MoveTo(world, attacker, x, y);
        }
    }

    // The hit frame: a ranged attacker with a projectile fires it; everyone else hits at once.
    private void Strike(World world, EntityId attacker, EntityTypeDefinition type, ArmamentDefinition arm, Identity self, Position from, int range, EntityId target, int x, int y)
    {
        int attack = CombatStats.Attack(world, attacker, type, arm);
        bool hero = world.IsHero(attacker);
        if (arm.Projectile >= 0 && range > 0 && world.Entities.RemainingCapacity >= 1)
        {
            ProjectileDefinition def = world.Ruleset.Projectiles[arm.Projectile];
            EntityId p = world.Entities.Create();
            world.Projectiles.Set(p, new Projectile
            {
                X = from.X,
                Y = from.Y,
                TargetX = x,
                TargetY = y,
                Target = target.Value,
                Attacker = attacker.Value,
                Attack = attack,
                MaxAttack = MaxRoll(arm),
                AttackerType = type.Id,
                Spell = Projectile.NoSpell,
                Kind = (byte)arm.Projectile,
                Owner = self.Owner,
                DamageClass = arm.DamageClass,
                FromHero = hero ? (byte)1 : (byte)0,
            });
            long flightMs = IntMath.ISqrt(IntMath.DistanceSquared(from.X, from.Y, x, y)) * 1000 / def.SpeedUnitsPerSecond;
            world.Events.Emit(new SimEvent(SimEventKind.ProjectileLaunched, self.Owner, attacker, x, y, arm.Projectile | (int)(Math.Min(flightMs, 0xFFFFFF) << 8)));
            return;
        }

        if (!target.IsNone)
        {
            _hits.Add(new HitRecord(attacker, target, self.Owner, type.Id, attack, MaxRoll(arm), 100, arm.DamageClass, hero));
        }
    }

    // The extra damage a max_damage shooter may roll above its attack (towers; ⚠ HYPOTHESIS).
    private static int MaxRoll(ArmamentDefinition arm)
        => arm.MaxAttack > arm.Attack ? arm.MaxAttack - arm.Attack : 0;

    private void FlyProjectiles(World world, int tickMs)
    {
        ReadOnlySpan<EntityId> ids = world.Projectiles.Entities;
        Span<Projectile> projectiles = world.Projectiles.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            ref Projectile p = ref projectiles[i];
            ProjectileDefinition def = world.Ruleset.Projectiles[p.Kind];
            EntityId target = EntityId.FromValue(p.Target);
            bool targetAlive = p.Target != 0 && world.Entities.IsAlive(target) && world.Positions.Has(target) && !world.IsHeld(target)
                && world.Healths.IndexOf(target) is >= 0 and var h && world.Healths.At(h).Current > 0;
            if (def.Homing && p.Target != 0)
            {
                if (!targetAlive)
                {
                    _spent.Add(ids[i]); // its target is gone: the arrow is lost
                    continue;
                }

                Position tp = world.Positions.Get(target);
                p.TargetX = tp.X;
                p.TargetY = tp.Y;
            }

            long step = Math.Max(1, (long)def.SpeedUnitsPerSecond * tickMs / 1000);
            long dx = p.TargetX - p.X;
            long dy = p.TargetY - p.Y;
            long dist = IntMath.ISqrt((dx * dx) + (dy * dy));
            if (dist > step)
            {
                p.X += (int)(dx * step / dist);
                p.Y += (int)(dy * step / dist);
                continue;
            }

            p.X = p.TargetX;
            p.Y = p.TargetY;
            _spent.Add(ids[i]);
            Impact(world, p, def, target, targetAlive);
        }
    }

    // Arrival: the target is hit if it is there (homing: always), and splash hurts what stands around the point.
    private void Impact(World world, in Projectile p, ProjectileDefinition def, EntityId target, bool targetAlive)
    {
        EntityId attacker = EntityId.FromValue(p.Attacker);
        bool hitsTarget = targetAlive && (def.Homing || Interaction.EdgeDistance(world, new Position(p.X, p.Y), 0, target) <= def.HitRadiusUnits);
        if (p.Spell != Projectile.NoSpell)
        {
            if (hitsTarget)
            {
                _hits.Add(new HitRecord(attacker, target, p.Owner, p.AttackerType, 0, 0, 100, p.DamageClass, false) { Spell = p.Spell });
            }

            return;
        }

        if (hitsTarget)
        {
            _hits.Add(new HitRecord(attacker, target, p.Owner, p.AttackerType, p.Attack, p.MaxAttack, 100, p.DamageClass, p.FromHero != 0));
        }

        int radius = Math.Max(def.SplashPercent > 0 ? def.SplashRadiusUnits : 0, p.Target == 0 ? def.HitRadiusUnits : 0);
        if (radius <= 0)
        {
            return;
        }

        _splash.Clear();
        Within(world, p.X, p.Y, radius, _splash);
        foreach (EntityId victim in _splash)
        {
            if (hitsTarget && victim == target)
            {
                continue;
            }

            byte owner = world.Identities.Get(victim).Owner;
            if (owner == p.Owner && !def.FriendlyFire)
            {
                continue;
            }

            // Attack ground (no target): what stands at the point takes the full hit, the rest the splash share.
            bool atPoint = p.Target == 0 && Interaction.EdgeDistance(world, new Position(p.X, p.Y), 0, victim) <= def.HitRadiusUnits;
            _hits.Add(new HitRecord(attacker, victim, p.Owner, p.AttackerType, p.Attack, p.MaxAttack, atPoint ? 100 : def.SplashPercent, p.DamageClass, p.FromHero != 0));
        }
    }

    /// <summary>
    /// The combatants (units and buildings on the map, alive, not stone) whose body is within <paramref name="radius"/>
    /// of the point, in grid order (deterministic: cells row by row, ascending index in a cell). Uses this tick's grid.
    /// </summary>
    internal void Within(World world, int x, int y, int radius, List<EntityId> output)
    {
        long slack = (EntityTypeDefinition.MaxFootprintTiles / 2 * MapState.UnitsPerTile) + EntityTypeDefinition.MaxRadius;
        long reach = radius + slack;
        int cx0 = (int)Math.Max(0, (x - reach) / CellSize);
        int cy0 = (int)Math.Max(0, (y - reach) / CellSize);
        int cx1 = (int)Math.Min(_gridW - 1, (x + reach) / CellSize);
        int cy1 = (int)Math.Min(_gridH - 1, (y + reach) / CellSize);
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int cell = (cy * _gridW) + cx;
                for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
                {
                    EntityId candidate = ids[_cellItems[k]];
                    if (!world.Entities.IsAlive(candidate) || world.Healths.Get(candidate).Current <= 0 || world.IsStone(candidate))
                    {
                        continue;
                    }

                    if (Interaction.EdgeDistance(world, new Position(x, y), 0, candidate) <= radius)
                    {
                        output.Add(candidate);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Applies one hit (pass 2; also used by spells): damage after bonus, defence, resistance, the holder's share and
    /// immunity, experience for the attacker, retaliation, and the death queue.
    /// </summary>
    internal static void Apply(World world, in HitRecord hit, List<Death> deaths)
    {
        if (!world.Entities.IsAlive(hit.Target) || world.Healths.IndexOf(hit.Target) is not (>= 0 and var hp))
        {
            return;
        }

        ref Health health = ref world.Healths.At(hp);
        if (health.Current <= 0 || world.IsInvulnerable(world.Identities.Get(hit.Target).Owner))
        {
            return; // already killed by an earlier hit of this tick, or hmgod (GAME-19)
        }

        if (hit.Spell != Projectile.NoSpell)
        {
            MagicSystem.ApplyEffect(world, hit.Spell, hit.Attacker, hit.Owner, hit.Target, 0, 0, deaths);
            return;
        }

        if (world.IsStone(hit.Target) || world.IsHeld(hit.Target))
        {
            return; // stone takes no damage; a unit inside a holder is reached only through its holder's share
        }

        EntityTypeDefinition targetType = world.TypeOf(hit.Target);
        Identity victim = world.Identities.Get(hit.Target);
        bool attackerAlive = world.Entities.IsAlive(hit.Attacker) && world.Identities.Has(hit.Attacker);
        if ((targetType.Traits.Abilities & AbilityFlags.TrainingDummy) != 0 && victim.Owner == hit.Owner)
        {
            // Training (Tactics): no damage; the attacker learns up to the dummies' level.
            if (attackerAlive && world.Experiences.IndexOf(hit.Attacker) is >= 0 and var dx && world.Experiences.At(dx).Level < world.Ruleset.Combat.DummyMaxLevel)
            {
                world.Experiences.At(dx).PendingXp += Math.Max(1, (int)((long)hit.Attack * world.Ruleset.Combat.ExperiencePerDamagePercent / 100));
            }

            return;
        }

        if ((targetType.Traits.Abilities & AbilityFlags.MagicImmune) != 0 && world.Ruleset.Combat.MagicAttackers.AsSpan().BinarySearch(hit.AttackerType) >= 0)
        {
            return; // the Messiah takes no magic: mages, dragons, priests (WAR-010)
        }

        int dealt = Damage(world, hit, targetType);
        if (world.Holders.Count != 0 && world.Holders.IndexOf(hit.Target) is >= 0 and var hs && world.Holders.At(hs).Count > 0 && targetType.Traits.Holder is { } holder)
        {
            DamageShare share = holder.ShareFor(hit.DamageClass);
            int spread = (int)((long)dealt * share.DistributePercent / 100);
            dealt = (int)((long)dealt * share.TakePercent / 100);
            Share(world, hit, spread, deaths);
        }

        if (dealt <= 0)
        {
            return;
        }

        health.Current = (int)Math.Max(health.Current - (long)dealt, 0);
        world.Events.Emit(new SimEvent(SimEventKind.Damaged, victim.Owner, hit.Target, hit.Attacker.Index, health.Current, dealt));
        Credit(world, hit, victim.Owner, dealt);
        if (health.Current == 0)
        {
            deaths.Add(new Death(hit.Target, hit.Attacker, hit.Owner));
            return;
        }

        // Retaliation: an idle armed unit that is hit fights back (not on hold fire). So does a unit on a move-advance
        // without a target, which then resumes its way: a tower that outranges its sight would shoot it down unanswered.
        // A sneaking unit fights back whether it walks or stands (ORD-004).
        int k = world.Combats.IndexOf(hit.Target);
        if (k >= 0 && attackerAlive && world.Identities.Get(hit.Attacker).Owner != victim.Owner)
        {
            ref Combat other = ref world.Combats.At(k);
            bool idle = (other.Mode == CombatMode.Sneak || (other.Mode == CombatMode.None && !Interaction.IsMoving(world, hit.Target) && !HasTask(world, hit.Target)))
                && targetType.Traits.Armament!.AutoAcquire;
            bool advancing = other.Mode == CombatMode.AttackMove;
            if (other.Target == 0 && (idle || advancing) && StanceOf(world, hit.Target) != CombatStance.HoldFire && !world.IsHeld(hit.Attacker))
            {
                other.Target = hit.Attacker.Value;
                other.ChaseTick = -1;
                if (advancing)
                {
                    other.Resume = 1;
                }
                else
                {
                    other.Mode = CombatMode.Auto;
                }
            }
        }
    }

    // WAR-009 (ReTzar's reading of the original, 0x4ee780): after its blow a ghost, in 71 draws of 1001, lets out a banshee
    // scream, a free curse on the enemies around it.
    private static void Banshee(World world, in HitRecord hit, List<Death> deaths)
    {
        CombatSettings settings = world.Ruleset.Combat;
        if (settings.BansheeSpell < 0 || hit.Spell != Projectile.NoSpell
            || (world.Ruleset.EntityTypes[hit.AttackerType].Traits.Abilities & AbilityFlags.Banshee) == 0
            || !world.Entities.IsAlive(hit.Attacker) || !world.Positions.Has(hit.Attacker)
            || world.Rng.Combat.NextInt(settings.BansheeOutOf) >= settings.BansheeChance)
        {
            return;
        }

        Position at = world.Positions.Get(hit.Attacker);
        MagicSystem.ApplyEffect(world, (ushort)settings.BansheeSpell, hit.Attacker, hit.Owner, EntityId.None, at.X, at.Y, deaths);
    }

    // The damage formula (UNK-08 placeholder, ADR 0038): attack (rolled up to max_damage), BONUS.INI, Honor, defence,
    // the minimum, building resistance, then the splash share.
    private static int Damage(World world, in HitRecord hit, EntityTypeDefinition targetType)
    {
        byte targetOwner = world.Identities.Get(hit.Target).Owner;
        long attack = hit.Attack;
        if (hit.MaxRoll > 0)
        {
            attack += world.Rng.Combat.NextInt(hit.MaxRoll + 1);
        }

        CombatSettings combat = world.Ruleset.Combat;
        if (hit.FromHero && combat.HonorTechnology >= 0 && world.HasPlayer(hit.Owner) && world.Players[hit.Owner].HasTechnology(combat.HonorTechnology)
            && world.IsHero(hit.Target))
        {
            attack = attack * (100 + combat.HonorAttackPercent) / 100; // Honor: heroes hit heroes harder
        }

        int bonus = world.Ruleset.BonusBasisPoints(hit.AttackerType, targetType.Id);
        int defence = CombatStats.Defence(world, hit.Target, targetType);
        int resistance = world.Stats.Apply(targetOwner, ModifierStat.Resistance, targetType.Id, targetType.Traits.Resistance);
        long damage = (attack * (10_000 + bonus) / 10_000) - defence;
        if (resistance > 0)
        {
            // Buildings: RESISTANCE reduces the damage (manual ch. 4; the scale is UNVERIFIED, UNK-08).
            int scale = world.Ruleset.Gameplay.ResistanceScale;
            damage = damage * scale / (scale + resistance);
        }

        long dealt = Math.Clamp(damage, world.Ruleset.Gameplay.MinimumDamage, int.MaxValue);
        if (hit.Percent != 100)
        {
            dealt = dealt * hit.Percent / 100; // splash share
        }

        return (int)Math.Clamp(dealt, 0, int.MaxValue);
    }

    // A holder's distributed share: split evenly over the units inside (the first ones by entity index take the rest).
    private static void Share(World world, in HitRecord hit, int total, List<Death> deaths)
    {
        if (total <= 0)
        {
            return;
        }

        // Only the units still alive this tick share it (one killed by an earlier hit of the tick takes no part).
        ReadOnlySpan<EntityId> held = world.Helds.Entities;
        int n = 0;
        for (int i = 0; i < held.Length; i++)
        {
            n += IsLiveOccupant(world, i, hit.Target) ? 1 : 0;
        }

        if (n == 0)
        {
            return;
        }

        int each = total / n;
        int rest = total - (each * n);
        int done = 0;
        for (int i = 0; i < held.Length && done < n; i++)
        {
            if (!IsLiveOccupant(world, i, hit.Target))
            {
                continue;
            }

            int amount = each + (done < rest ? 1 : 0);
            done++;
            if (amount <= 0)
            {
                continue;
            }

            ref Health health = ref world.Healths.Get(held[i]);
            health.Current = Math.Max(0, health.Current - amount);
            world.Events.Emit(new SimEvent(SimEventKind.Damaged, world.Identities.Get(held[i]).Owner, held[i], hit.Attacker.Index, health.Current, amount));
            if (health.Current == 0)
            {
                deaths.Add(new Death(held[i], hit.Attacker, hit.Owner));
            }
        }

        world.Events.Emit(new SimEvent(SimEventKind.DamageShared, world.Identities.Get(hit.Target).Owner, hit.Target, n, 0, total));
    }

    private static bool IsLiveOccupant(World world, int heldIndex, EntityId holder)
    {
        EntityId e = world.Helds.Entities[heldIndex];
        return world.Helds.Components[heldIndex].Holder == holder.Value && !world.Items.Has(e) && world.Healths.Get(e).Current > 0;
    }

    // Experience for damage dealt to another kingdom's (or Nature's) unit or building, not to an own or an ally's (1.05,
    // EXP-003: confusion, a rain of fire or a scripted fight may hit allies; GAME-08).
    internal static void Credit(World world, in HitRecord hit, byte victimOwner, int dealt)
    {
        if (Allies(world, hit.Owner, victimOwner) || !world.Entities.IsAlive(hit.Attacker) || world.Experiences.Count == 0
            || world.Experiences.IndexOf(hit.Attacker) is not (>= 0 and var x))
        {
            return;
        }

        world.Experiences.At(x).PendingXp += (int)Math.Min((long)dealt * world.Ruleset.Combat.ExperiencePerDamagePercent / 100, 1_000_000);
    }

    // Whether the attacker's kingdom counts the victim's as its own or an ally (no experience from them; EXP-003).
    private static bool Allies(World world, int attacker, int victim)
        => attacker == victim || world.Diplomacy.StanceOf(attacker, victim) == Kingdoms.Stance.Ally;

    private static void Uncloak(World world, EntityId unit)
    {
        if (world.Statuses.Count != 0 && world.Statuses.IndexOf(unit) is >= 0 and var s)
        {
            world.Statuses.At(s).CloakUntil = 0;
        }
    }

    // Movement planes for naval combat: 0 land (and buildings), 1 water (ships), 2 air.
    private static byte PlaneOf(EntityTypeDefinition type)
        => !type.IsMobile ? (byte)0 : type.Locomotor == Locomotor.Ship ? (byte)1 : type.Locomotor == Locomotor.Air ? (byte)2 : (byte)0;

    private static bool IsAcrossShore(byte a, byte b) => a != b && a != 2 && b != 2;

    private static CombatStance StanceOf(World world, EntityId entity)
        => world.Stances.Count != 0 && world.Stances.IndexOf(entity) is >= 0 and var s ? world.Stances.At(s).Value : CombatStance.Aggressive;

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
        uint guarded = motion.Order == MotionOrder.Follow ? motion.FollowTarget : 0;
        Interaction.MoveTo(world, attacker, x, y);
        if (c.Mode == CombatMode.AttackMove)
        {
            // Keep the move-advance destination to resume it after the fight.
            motion.OrderX = orderX;
            motion.OrderY = orderY;
        }

        if (guarded != 0)
        {
            // A guard chases and then goes back to what it guards.
            motion.Order = MotionOrder.Follow;
            motion.FollowTarget = guarded;
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
            if (world.WaypointQueues.Count > 0 && world.WaypointQueues.Has(attacker))
            {
                OrderQueue.Next(world, attacker); // the leg is done: the next queued order (ORD-005)
            }

            return;
        }

        c.Resume = 0;
        int x = motion.OrderX;
        int y = motion.OrderY;
        Interaction.MoveTo(world, attacker, x, y);
    }

    // The swing cycle: a ship's cannon levels make its cannons fire more often (help HT_UPGRADE_SHIPS; TECH-036), the
    // cycle divided by the fire rate in percent, never shorter than the hit delay.
    private static int Cycle(World world, EntityId attacker, ArmamentDefinition arm)
    {
        if (world.UpgradeLevels.Count == 0)
        {
            return arm.CooldownMs;
        }

        int rate = ResearchRules.LevelBonus(world, attacker, ModifierStat.FireRate, 100);
        return rate <= 100 ? arm.CooldownMs : Math.Max(Math.Max(1, arm.HitDelayMs), (int)((long)arm.CooldownMs * 100 / rate));
    }

    private static int Range(World world, EntityId attacker, Identity self, EntityTypeDefinition type, ArmamentDefinition arm)
        => arm.RangeUnits <= 0 ? 0 : ResearchRules.LevelBonus(world, attacker, ModifierStat.Range, world.Stats.Apply(self.Owner, ModifierStat.Range, type.Id, arm.RangeUnits));

    private static int ReachOf(World world, EntityId target)
    {
        EntityTypeDefinition type = world.TypeOf(target);
        return type.Traits.Armament is { } arm ? Range(world, target, world.Identities.Get(target), type, arm) : 0;
    }

    private static int Sight(World world, Identity self, EntityTypeDefinition type)
        => world.Stats.Apply(self.Owner, ModifierStat.Sight, type.Id, type.Traits.SightUnits);

    // An own animal that leaves meat (a cow) may be attacked on an explicit order: butchering (GAME-02, ADR 0042).
    private static bool IsButcherTarget(World world, byte owner, EntityId target)
        => world.Entities.IsAlive(target) && world.Identities.IndexOf(target) is >= 0 and var i && world.Identities.At(i).Owner == owner
            && world.Ruleset.EntityTypes[world.Identities.At(i).TypeId].Traits.SpawnOnDeath >= 0 && world.Healths.Get(target).Current > 0;

    // An own training dummy may be attacked on an explicit order (Tactics; GAME-08).
    internal static bool IsTrainingTarget(World world, byte owner, EntityId target)
        => world.Entities.IsAlive(target) && world.Identities.IndexOf(target) is >= 0 and var i && world.Identities.At(i).Owner == owner
            && (world.Ruleset.EntityTypes[world.Identities.At(i).TypeId].Traits.Abilities & AbilityFlags.TrainingDummy) != 0 && !world.Constructions.Has(target);

    private static bool HasTask(World world, EntityId entity)
    {
        int w = world.Workers.IndexOf(entity);
        return w >= 0 && world.Workers.At(w).Task != WorkerTask.None;
    }

    // An enemy for this attacker: a live enemy (IsEnemyTarget), or anyone at all when either side is confused (GAME-11).
    private static bool IsHostileTarget(World world, EntityId attacker, byte owner, EntityId target)
    {
        if (IsEnemyTarget(world, owner, target))
        {
            return true;
        }

        return target != attacker && (world.IsConfused(attacker) || world.IsConfused(target)) && IsTargetable(world, target);
    }

    // A live unit or building of another owner. Nature's units (animals, wild creatures) are hostile to every kingdom
    // and can be fought; resources never are. Units inside holders, stone and invisible units cannot be targeted.
    internal static bool IsEnemyTarget(World world, byte owner, EntityId target)
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
        // GAME-12: an invisible unit is a target only where a detector of the kingdom sees it; GAME-13/15: stances,
        // unclaimed gateways and ambient life.
        return identity.Owner != owner && IsTargetable(world, target) && world.MayAttack(owner, target)
            && !(owner < world.Players.Length && world.IsConcealed(target) && world.Visibility.IsConcealedFrom(world, owner, target, world.Positions.Get(target)));
    }

    private static bool IsTargetable(World world, EntityId target)
        => world.Entities.IsAlive(target) && world.Identities.IndexOf(target) is >= 0 and var i && IsCombatant(world, world.Identities.At(i))
            && world.Healths.Get(target).Current > 0 && !world.IsHeld(target) && !world.IsStone(target);

    private static bool IsCombatant(World world, Identity identity)
        => world.Ruleset.EntityTypes[identity.TypeId].Traits.Category is EntityCategory.Unit or EntityCategory.Building;

    private static uint OwnerBit(byte owner) => owner == MapObject.Nature ? 1u << NatureBit : 1u << (owner & 15);

    private EntityId Acquire(World world, EntityId scanner, byte owner, Position position, int radius, long sight, int minRange, byte plane, int range)
    {
        EntityId best = EntityId.None;
        long bestDistance = long.MaxValue;
        bool confused = world.IsConfused(scanner);

        // Structures are bucketed by their center; their footprint reaches at most half the largest side further.
        long slack = (EntityTypeDefinition.MaxFootprintTiles / 2 * MapState.UnitsPerTile) + EntityTypeDefinition.MaxRadius;
        int reach = (int)Math.Min(sight + slack, int.MaxValue / 2);
        int cx0 = Math.Max(0, (position.X - reach) / CellSize);
        int cy0 = Math.Max(0, (position.Y - reach) / CellSize);
        int cx1 = Math.Min(_gridW - 1, (position.X + reach) / CellSize);
        int cy1 = Math.Min(_gridH - 1, (position.Y + reach) / CellSize);
        uint enemies = confused ? uint.MaxValue : ~OwnerBit(owner);
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
                    EntityId candidate = ids[dense];
                    if (candidate == scanner)
                    {
                        continue;
                    }

                    // Nearest first: the lookups below only for a candidate that would become the best (same choice).
                    long d = Interaction.EdgeDistance(world, position, radius, candidate);
                    if (d > sight || d < minRange || d > bestDistance || (d == bestDistance && candidate.Index > best.Index))
                    {
                        continue;
                    }

                    if (identities[dense].Owner == owner && !confused && !world.IsConfused(candidate))
                    {
                        continue;
                    }

                    // Across the shore (a ship and something on land) only what is in range now: neither can go there.
                    if (d > range && IsAcrossShore(plane, PlaneOf(world.Ruleset.EntityTypes[identities[dense].TypeId])))
                    {
                        continue;
                    }

                    if (world.Healths.Get(candidate).Current <= 0 || world.IsStone(candidate) || (!confused && !world.MayAttack(owner, candidate)))
                    {
                        continue;
                    }

                    // GAME-12: cloaked units are not acquired unless a detector of the kingdom is near.
                    if (owner < world.Players.Length && world.IsConcealed(candidate)
                        && world.Visibility.IsConcealedFrom(world, owner, candidate, world.Positions.Get(candidate)))
                    {
                        continue;
                    }

                    // A spy disguised as one of the kingdom's (or an ally's) units is left alone (ADR 0184).
                    if (!confused && world.LooksFriendlyTo(candidate, owner))
                    {
                        continue;
                    }

                    bestDistance = d;
                    best = candidate;
                }
            }
        }

        return best;
    }

    // Buckets the combatants (units and buildings on the map; not resources, items, or units inside holders) by
    // position (structures by their center) with a stable counting sort, and records which owners each cell holds.
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

        bool anyHeld = world.Helds.Count != 0;
        for (int i = 0; i < ids.Length; i++)
        {
            if (!IsCombatant(world, identities[i]) || (anyHeld && world.Helds.Has(ids[i])))
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

/// <summary>One hit to apply in pass 2 (or a spell effect carried by a projectile).</summary>
/// <param name="Attacker">Who struck (may be dead by now: projectiles, simultaneous hits).</param>
/// <param name="Target">Who is hit.</param>
/// <param name="Owner">The attacker's owner.</param>
/// <param name="AttackerType">The attacker's type (bonuses).</param>
/// <param name="Attack">Attack before defence.</param>
/// <param name="MaxRoll">Extra damage the hit may roll (0..MaxRoll).</param>
/// <param name="Percent">Share of the damage (splash).</param>
/// <param name="DamageClass">Damage class (holder shares).</param>
/// <param name="FromHero">Whether the attacker is a hero (Honor).</param>
internal readonly record struct HitRecord(EntityId Attacker, EntityId Target, byte Owner, ushort AttackerType, int Attack, int MaxRoll, int Percent, DamageClass DamageClass, bool FromHero)
{
    /// <summary>A spell to apply instead of damage (spell projectiles), or <see cref="Projectile.NoSpell"/>.</summary>
    public ushort Spell { get; init; } = Projectile.NoSpell;
}

/// <summary>A unit or building brought to 0 hit points, killed after the hits of the tick.</summary>
/// <param name="Victim">Who dies.</param>
/// <param name="Killer">Who dealt the last hit.</param>
/// <param name="KillerOwner">The killer's owner (also when the killer died in the same tick).</param>
internal readonly record struct Death(EntityId Victim, EntityId Killer, int KillerOwner);
