using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// The original's choice of targets and its calls for help (ReTzar's reading of the original): what a unit or tower takes
/// on by itself is ranked by its category and then by the original's tie-breaks (WAR-015); a unit whose target falls or
/// cannot be reached looks for the next one at once (WAR-016); a unit or building that is hit, or a unit that takes on
/// an enemy by itself, calls the fighters around it and its guards to answer (WAR-017).
/// </summary>
public sealed partial class CombatSystem
{
    // How many units attack each entity (by entity index) and the owner bits of the kingdoms whose units strike it now:
    // counted at the start of the tick and kept up as units take new targets.
    private int[] _attackers = [];
    private uint[] _struck = [];

    // Units guarding something, by what they guard (guarded value, guard value), sorted; rebuilt every tick.
    private readonly List<(uint Guarded, uint Guard)> _guards = [];

    // Calls for help of this tick (victim, attacker), and the victims that called already this tick.
    private readonly List<(EntityId Victim, EntityId Attacker)> _alarms = [];
    private readonly List<uint> _called = [];

    // Who is looking for a target, for the comparisons of WAR-015.
    private readonly struct Scanner(EntityId id, byte owner, EntityTypeDefinition type, int rangeTiles, (int X0, int Y0, int X1, int Y1) tiles, uint guarded, Position position, int radius, int reach)
    {
        public EntityId Id { get; } = id;

        public byte Owner { get; } = owner;

        public EntityTypeDefinition Type { get; } = type;

        public int RangeTiles { get; } = rangeTiles;

        public (int X0, int Y0, int X1, int Y1) Tiles { get; } = tiles;

        public uint Guarded { get; } = guarded;

        public Position Position { get; } = position;

        public int Radius { get; } = radius;

        // What the unit strikes from where it stands (world units): its range, or hand-to-hand reach.
        public int Reach { get; } = reach;
    }

    private void CountAttackers(World world)
    {
        int n = world.Entities.HighWater;
        if (_attackers.Length < n)
        {
            _attackers = new int[Math.Max(n, _attackers.Length * 2)];
            _struck = new uint[_attackers.Length];
        }

        Array.Clear(_attackers, 0, n);
        Array.Clear(_struck, 0, n);
        ReadOnlySpan<EntityId> ids = world.Combats.Entities;
        Span<Combat> combats = world.Combats.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            EntityId t = EntityId.FromValue(combats[i].Target);
            if (combats[i].Target == 0 || !world.Entities.IsAlive(t) || t.Index >= n)
            {
                continue;
            }

            _attackers[t.Index]++;
            if (combats[i].Striking != 0)
            {
                _struck[t.Index] |= OwnerBit(world.Identities.Get(ids[i]).Owner);
            }
        }

        _guards.Clear();
        ReadOnlySpan<EntityId> movers = world.Motions.Entities;
        Span<Motion> motions = world.Motions.Components;
        for (int i = 0; i < movers.Length; i++)
        {
            if (motions[i].Order == MotionOrder.Follow && motions[i].FollowTarget != 0)
            {
                _guards.Add((motions[i].FollowTarget, movers[i].Value));
            }
        }

        _guards.Sort();
        _alarms.Clear();
        _called.Clear();
    }

    // Gives the unit a new target and keeps the attacker counts up.
    private void Aim(World world, ref Combat c, EntityId target)
    {
        EntityId old = EntityId.FromValue(c.Target);
        if (c.Target != 0 && world.Entities.IsAlive(old) && old.Index < _attackers.Length && _attackers[old.Index] > 0)
        {
            _attackers[old.Index]--;
        }

        c.Target = target.Value;
        c.ChaseTick = -1;
        c.Striking = 0;
        if (target.Index < _attackers.Length)
        {
            _attackers[target.Index]++;
        }
    }

    private static Scanner ScannerOf(World world, EntityId unit, byte owner, EntityTypeDefinition type, int range)
    {
        uint guarded = world.Motions.IndexOf(unit) is >= 0 and var m && world.Motions.At(m).Order == MotionOrder.Follow ? world.Motions.At(m).FollowTarget : 0;
        int reach = range > 0 ? range : world.Ruleset.Gameplay.MeleeReachUnits;
        return new Scanner(unit, owner, type, range / MapState.UnitsPerTile, Tiles(world, unit), guarded, world.Positions.Get(unit), Interaction.RadiusOf(world, unit), reach);
    }

    // The tiles an entity stands on, inclusive: a structure's footprint, a unit's own tile.
    private static (int X0, int Y0, int X1, int Y1) Tiles(World world, EntityId entity)
    {
        if (world.Footprints.Has(entity))
        {
            (int tx, int ty, int w, int h) = world.FootprintRect(entity);
            return (tx, ty, tx + w - 1, ty + h - 1);
        }

        Position p = world.Positions.Get(entity);
        return (p.X / MapState.UnitsPerTile, p.Y / MapState.UnitsPerTile, p.X / MapState.UnitsPerTile, p.Y / MapState.UnitsPerTile);
    }

    // The empty tiles between two rectangles along the axis where they are farthest apart, the side chosen by their
    // centres; 0 when they touch (ReTzar 0x401b30).
    private static int Gap((int X0, int Y0, int X1, int Y1) a, (int X0, int Y0, int X1, int Y1) b)
    {
        int ax = (a.X0 + a.X1) / 2;
        int ay = (a.Y0 + a.Y1) / 2;
        int bx = (b.X0 + b.X1) / 2;
        int by = (b.Y0 + b.Y1) / 2;
        int dx = ax < bx ? b.X0 - a.X1 - 1 : a.X0 - b.X1 - 1;
        int dy = ay < by ? b.Y0 - a.Y1 - 1 : a.Y0 - b.Y1 - 1;
        return Math.Max(Math.Max(dx, dy), 0);
    }

    // WAR-015 (ReTzar 0x412730): the category of target t for the scanner.
    private static TargetCategory CategoryOf(World world, in Scanner s, EntityId t, int depth = 0)
    {
        EntityTypeDefinition type = world.TypeOf(t);
        CombatSettings settings = world.Ruleset.Combat;
        if (type.Traits.IsBuilder && type.IsMobile)
        {
            // A peasant: by what it does.
            if (world.Workers.IndexOf(t) is >= 0 and var w)
            {
                Worker work = world.Workers.At(w);
                EntityId job = EntityId.FromValue(work.Target);
                bool jobAlive = work.Target != 0 && world.Entities.IsAlive(job) && world.Identities.Has(job);
                switch (work.Task)
                {
                    case WorkerTask.Build when jobAlive && world.TypeOf(job) is { HasFootprint: true, Traits.Armament: not null }:
                        return Gap(s.Tiles, Tiles(world, t)) <= s.RangeTiles + 2 ? TargetCategory.TowerBuilder : TargetCategory.Unarmed;
                    case WorkerTask.Capture:
                        return TargetCategory.Capturer;
                    case WorkerTask.Repair when jobAlive && depth == 0 && world.Healths.IndexOf(job) is >= 0 and var hp
                        && world.Healths.At(hp).Current > 0 && world.Healths.At(hp).Current < world.Healths.At(hp).Max:
                        return CategoryOf(world, s, job, depth + 1); // the repaired object's own category
                }
            }

            return world.Combats.IndexOf(t) is >= 0 and var c && world.Combats.At(c).Target != 0 ? TargetCategory.Fighter : TargetCategory.Unarmed;
        }

        if (settings.TargetMages.AsSpan().BinarySearch(type.Id) >= 0)
        {
            return TargetCategory.Mage;
        }

        if (settings.TargetSeats.AsSpan().BinarySearch(type.Id) >= 0)
        {
            return TargetCategory.Seat;
        }

        if (settings.TargetTrainers.AsSpan().BinarySearch(type.Id) >= 0)
        {
            return TargetCategory.Trainer;
        }

        if ((type.Traits.Abilities & AbilityFlags.TrainingDummy) != 0)
        {
            return TargetCategory.Dummy;
        }

        if (type.IsMobile)
        {
            return type.Traits.Armament is { AutoAcquire: true } ? TargetCategory.Fighter : TargetCategory.Unarmed;
        }

        if (!type.HasFootprint)
        {
            return TargetCategory.None;
        }

        if (world.Constructions.IndexOf(t) is >= 0 and var k && world.Constructions.At(k).ProgressMs == 0
            && Gap(s.Tiles, Tiles(world, t)) <= s.RangeTiles + 4)
        {
            return TargetCategory.NewSite; // a site not begun yet, near enough to be reached soon
        }

        return type.Traits.Armament is not null ? TargetCategory.Tower : type.Traits.IsWall ? TargetCategory.Wall : TargetCategory.Building;
    }

    private static int Priority(World world, in Scanner s, TargetCategory category)
    {
        if (category == TargetCategory.None)
        {
            return 0;
        }

        CombatSettings settings = world.Ruleset.Combat;
        ImmutableArrayView table = settings.SiegeScanners.AsSpan().BinarySearch(s.Type.Id) >= 0 ? new(settings.SiegeTargetPriority) : new(settings.TargetPriority);
        return table[(int)category - 1];
    }

    // Whether candidate t may be taken on by the scanner at all (beyond hostility, sight and reach): bridges and gates
    // never, a building another unit of the kingdom strikes already not, and only what has a priority (WAR-015).
    private bool Eligible(World world, in Scanner s, EntityId t)
    {
        EntityTypeDefinition type = world.TypeOf(t);
        if (type.Traits.IsBridge || type.Traits.Gate is not null)
        {
            return false;
        }

        if (type.HasFootprint && t.Index < _struck.Length && (_struck[t.Index] & OwnerBit(s.Owner)) != 0)
        {
            return false;
        }

        return Priority(world, s, CategoryOf(world, s, t)) > 0;
    }

    // WAR-015 (ReTzar 0x412bf0): whether candidate t ranks above the best so far for the scanner: the higher priority;
    // then the one attacking what the scanner guards; then the one in reach; then, at about the same distance, the
    // better matchup, the one nearer two attackers and the weaker by more than 20; then the nearer.
    private bool Better(World world, in Scanner s, EntityId best, EntityId t)
    {
        int pt = Priority(world, s, CategoryOf(world, s, t));
        if (pt == 0)
        {
            return false;
        }

        if (best.IsNone)
        {
            return true;
        }

        int pb = Priority(world, s, CategoryOf(world, s, best));
        if (pt != pb)
        {
            return pt > pb;
        }

        bool gt = AttacksGuarded(world, t, s.Guarded);
        bool gb = AttacksGuarded(world, best, s.Guarded);
        if (gt != gb)
        {
            return gt;
        }

        // In reach by this build's own test (the original counts cells; Tzar2 measures from the body).
        int gapT = Gap(s.Tiles, Tiles(world, t));
        int gapB = Gap(s.Tiles, Tiles(world, best));
        bool reachT = Interaction.EdgeDistance(world, s.Position, s.Radius, t) <= s.Reach;
        bool reachB = Interaction.EdgeDistance(world, s.Position, s.Radius, best) <= s.Reach;
        if (reachT != reachB)
        {
            return reachT;
        }

        if (Math.Abs(gapT - gapB) < 2)
        {
            int mt = Matchup(world, s, t);
            int mb = Matchup(world, s, best);
            if (mt != mb)
            {
                return mt > mb;
            }

            if (world.TypeOf(t).IsMobile && world.TypeOf(best).IsMobile)
            {
                int nt = Math.Abs(Attackers(t) - 2);
                int nb = Math.Abs(Attackers(best) - 2);
                if (nt != nb)
                {
                    return nb > nt;
                }
            }

            int d = world.Healths.Get(t).Current - world.Healths.Get(best).Current;
            if (Math.Abs(d) > 20)
            {
                return d < 0;
            }
        }

        return gapT < gapB;
    }

    private int Attackers(EntityId t) => t.Index < _attackers.Length ? _attackers[t.Index] : 0;

    // Whether x attacks what the scanner guards.
    private static bool AttacksGuarded(World world, EntityId x, uint guarded)
        => guarded != 0 && world.Combats.IndexOf(x) is >= 0 and var c && world.Combats.At(c).Target == guarded;

    // The scanner's edge over t: its BONUS.INI share of its attack against t, less t's against it (ReTzar 0x412b30).
    private static int Matchup(World world, in Scanner s, EntityId t) => Edge(world, s.Id, s.Type, t) - Edge(world, t, world.TypeOf(t), s.Id);

    private static int Edge(World world, EntityId a, EntityTypeDefinition type, EntityId b)
    {
        if (type.Traits.Armament is not { } arm)
        {
            return 0;
        }

        long attack = CombatStats.Attack(world, a, type, arm);
        return (int)(attack * world.Ruleset.BonusBasisPoints(type.Id, world.TypeOf(b).Id) / 10_000);
    }

    // WAR-017 (ReTzar 0x413950): the fighters and towers of the victim's kingdom (and its allies) within the help radius,
    // and the victim's guards wherever they are, answer the attacker. Once per victim and tick.
    private void CallForHelp(World world, EntityId victim, EntityId attacker)
    {
        if (!world.Entities.IsAlive(victim) || !world.Entities.IsAlive(attacker) || !world.Identities.Has(attacker) || _called.Contains(victim.Value))
        {
            return;
        }

        _called.Add(victim.Value);
        byte owner = world.Identities.Get(victim).Owner;
        int r = world.Ruleset.Combat.HelpRadiusTiles;
        (int x0, int y0, int x1, int y1) = Tiles(world, victim);
        (x0, y0, x1, y1) = (x0 - r, y0 - r, x1 + r, y1 + r);
        int slack = EntityTypeDefinition.MaxFootprintTiles * MapState.UnitsPerTile;
        int cx0 = Math.Max(0, ((x0 * MapState.UnitsPerTile) - slack) / CellSize);
        int cy0 = Math.Max(0, ((y0 * MapState.UnitsPerTile) - slack) / CellSize);
        int cx1 = Math.Min(_gridW - 1, (((x1 + 1) * MapState.UnitsPerTile) + slack) / CellSize);
        int cy1 = Math.Min(_gridH - 1, (((y1 + 1) * MapState.UnitsPerTile) + slack) / CellSize);
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int cell = (cy * _gridW) + cx;
                for (int k = _cellStart[cell]; k < _cellStart[cell + 1]; k++)
                {
                    int dense = _cellItems[k];
                    EntityId h = ids[dense];
                    if (h == attacker || !Friendly(world, owner, identities[dense].Owner))
                    {
                        continue;
                    }

                    (int hx0, int hy0, int hx1, int hy1) = Tiles(world, h);
                    if (hx1 < x0 || hx0 > x1 || hy1 < y0 || hy0 > y1)
                    {
                        continue;
                    }

                    Retaliate(world, h, attacker);
                }
            }
        }

        // Its guards, wherever they are.
        int i = _guards.BinarySearch((victim.Value, 0u));
        for (i = i < 0 ? ~i : i; i < _guards.Count && _guards[i].Guarded == victim.Value; i++)
        {
            EntityId guard = EntityId.FromValue(_guards[i].Guard);
            if (guard != attacker && world.Entities.IsAlive(guard))
            {
                Retaliate(world, guard, attacker);
            }
        }
    }

    private static bool Friendly(World world, byte a, byte b)
        => a == b || (a < world.Players.Length && b < world.Players.Length && world.Diplomacy.StanceOf(a, b) == Kingdoms.Stance.Ally);

    // Retaliation of a fighter or tower against an attacker (ReTzar 0x413410): a tower within its range band, a unit
    // outside its minimum range and, holding its place, within reach. An idle unit takes the better of the attacker and
    // its own scan's best; one on a move-advance, a sneak or a patrol turns at once; one fighting by itself changes only
    // for a better target, one on the player's order never. Whether it took the attacker on.
    private bool Retaliate(World world, EntityId unit, EntityId attacker)
    {
        if (!world.Entities.IsAlive(unit) || world.IsHeld(unit) || !world.CanAct(unit) || world.Combats.IndexOf(unit) is not (>= 0 and var ci)
            || world.Healths.Get(unit).Current <= 0)
        {
            return false;
        }

        EntityTypeDefinition type = world.TypeOf(unit);
        if (type.Traits.Armament is not { AutoAcquire: true } arm || StanceOf(world, unit) == CombatStance.HoldFire)
        {
            return false;
        }

        Identity self = world.Identities.Get(unit);
        if (!IsHostileTarget(world, unit, self.Owner, attacker) || world.Healths.Get(attacker).Current <= 0)
        {
            return false;
        }

        ref Combat c = ref world.Combats.At(ci);
        if (c.Mode == CombatMode.Ground || c.Mode == CombatMode.Scripted)
        {
            return false;
        }

        Position position = world.Positions.Get(unit);
        int distance = Interaction.EdgeDistance(world, position, Interaction.RadiusOf(world, unit), attacker);
        int range = Range(world, unit, self, type, arm);
        int reach = Math.Max(range, world.Ruleset.Gameplay.MeleeReachUnits);
        if (distance < arm.MinRangeUnits || ((!type.IsMobile || StanceOf(world, unit) == CombatStance.StandGround) && distance > reach))
        {
            return false;
        }

        EntityId current = EntityId.FromValue(c.Target);
        Scanner s = ScannerOf(world, unit, self.Owner, type, range);
        if (c.Target != 0 && IsHostileTarget(world, unit, self.Owner, current))
        {
            if (current == attacker || c.Mode == CombatMode.Explicit || !Better(world, s, current, attacker))
            {
                return false; // keeps its target: the player's order, or a target as good
            }

            Engage(world, ref c, attacker);
            return true;
        }

        bool patrol = world.Motions.IndexOf(unit) is >= 0 and var m && world.Motions.At(m).Order == MotionOrder.Patrol;
        if (c.Mode is CombatMode.AttackMove or CombatMode.Sneak || patrol)
        {
            Engage(world, ref c, attacker);
            return true;
        }

        if (HasTask(world, unit) || Interaction.IsMoving(world, unit))
        {
            return false; // walking somewhere on an order, or at work
        }

        // Idle: the better of the attacker and its own scan's best.
        EntityId best = Search(world, unit, self, type, arm, position, EntityId.None);
        Engage(world, ref c, !best.IsNone && best != attacker && Better(world, s, attacker, best) ? best : attacker);
        return true;
    }

    // The unit takes on a target by itself; a move-advance keeps its way to resume after the fight.
    private void Engage(World world, ref Combat c, EntityId target)
    {
        Aim(world, ref c, target);
        if (c.Mode == CombatMode.AttackMove)
        {
            c.Resume = 1;
        }
        else
        {
            c.Mode = CombatMode.Auto;
        }
    }

    // The best target in the unit's sight (its reach when it stands its ground), or none; never `except`.
    private EntityId Search(World world, EntityId unit, Identity self, EntityTypeDefinition type, ArmamentDefinition arm, Position position, EntityId except)
    {
        int range = Range(world, unit, self, type, arm);
        long sight = Math.Max(Sight(world, self, type), range);
        if (StanceOf(world, unit) == CombatStance.StandGround || !type.IsMobile)
        {
            sight = Math.Max(range, world.Ruleset.Gameplay.MeleeReachUnits); // a tower looks within its range (ReTzar 0x4db8d0)
        }

        return Acquire(world, ScannerOf(world, unit, self.Owner, type, range), position, Interaction.RadiusOf(world, unit), sight, arm.MinRangeUnits, PlaneOf(type), range, except);
    }

    // A read-only view of a priority table.
    private readonly ref struct ImmutableArrayView(System.Collections.Immutable.ImmutableArray<int> items)
    {
        private readonly ReadOnlySpan<int> _items = items.AsSpan();

        public int this[int i] => i >= 0 && i < _items.Length ? _items[i] : 0;
    }
}
