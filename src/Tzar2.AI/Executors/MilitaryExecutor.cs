using System;
using System.Collections.Generic;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;
using Tzar2.Sim.Navigation;

namespace Tzar2.AI.Executors;

/// <summary>
/// Defence, attack waves and targeting (REQ-AI-02 military manager; ADR 0052, tactics ADR 0178). The scripts only say
/// what to train; the original's war AI is hard-coded, so this is Tzar2's own, scaled by difficulty
/// (<see cref="TacticsLevel"/>; docs/mechanics/ai-placeholders.md):
/// <list type="bullet">
/// <item><b>Defence:</b> enemy soldiers near the own buildings are attacked by the soldiers at home (move-advance); so are
/// enemy towers, buildings and sites there, once the soldiers at home are strong enough.</item>
/// <item><b>Gathering:</b> producers rally new soldiers at a gather point in front of the town.</item>
/// <item><b>Waves:</b> after <see cref="MilitaryOptions.FirstWaveMs"/>, when enough soldiers are at home and strong enough
/// for the defence of the target, they all move-advance to the nearest (Hard: the least defended) building of the
/// nearest enemy; the threshold grows with every wave.</item>
/// <item><b>Targeting:</b> wave soldiers that stand idle attack what is dangerous around them, else the enemy object
/// nearest to the wave (move-advance when it is far); a wave reduced below <see cref="MilitaryOptions.RetreatPercent"/> %
/// of its launch size, or outmatched where it stands, goes home.</item>
/// </list>
/// </summary>
public sealed class MilitaryExecutor : IAiExecutor
{
    private readonly List<EntityId> _home = [];
    private readonly List<EntityId> _orders = [];
    private readonly List<(long Distance, EntityId Unit)> _candidates = [];
    private readonly HashSet<uint> _inWave = [];

    /// <inheritdoc />
    public string Name => "military";

    /// <inheritdoc />
    public void Run(AiExecutorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Settings.WarAi)
        {
            return;
        }

        AiPlayerState state = context.State;
        PruneWave(context);
        _inWave.Clear();
        foreach (uint v in state.WaveUnits)
        {
            _inWave.Add(v);
        }

        _home.Clear();
        foreach (EntityId unit in context.Player.Army)
        {
            if (!_inWave.Contains(unit.Value) && !context.IsClaimed(unit))
            {
                _home.Add(unit);
            }
        }

        (int gx, int gy) = GatherPoint(context);
        Rally(context, gx, gy);
        bool threatened = Defend(context);
        if (state.WavePhase == AiWavePhase.Attacking)
        {
            if (state.NavalPhase == AiNavalPhase.None)
            {
                Attack(context, gx, gy); // a wave at sea is the naval executor's until it lands (ADR 0144)
            }
        }
        else if (!threatened)
        {
            Launch(context);
        }
    }

    /// <summary>
    /// The enemy object nearest to (<paramref name="x0"/>, <paramref name="y0"/>): with <paramref name="preferBuildings"/>
    /// a building (or, if it has none, anything) of the enemy whose town is nearest; otherwise any building, site, soldier
    /// or worker of any enemy. Ties: lower slot, then lower entity index.
    /// </summary>
    internal static bool NearestEnemyTarget(AiExecutorContext context, int x0, int y0, bool preferBuildings, out int player, out EntityId entity, out int x, out int y)
    {
        World world = context.World;
        player = -1;
        entity = EntityId.None;
        x = y = 0;
        long best = long.MaxValue;
        int onlyPlayer = -1;
        if (preferBuildings)
        {
            for (int q = 0; q < context.Census.PlayerCount; q++)
            {
                AiCensusPlayer enemy = context.Census.Player(q);
                if (!AiCensus.IsEnemy(world, context.Slot, q) || !enemy.Exists)
                {
                    continue;
                }

                long d = AiExecutorContext.Distance(x0, y0, enemy.Town.X, enemy.Town.Y);
                if (d < best)
                {
                    best = d;
                    onlyPlayer = q;
                }
            }

            if (onlyPlayer < 0)
            {
                return false;
            }

            best = long.MaxValue;
        }

        for (int q = 0; q < context.Census.PlayerCount; q++)
        {
            if ((onlyPlayer >= 0 && q != onlyPlayer) || !AiCensus.IsEnemy(world, context.Slot, q))
            {
                continue;
            }

            AiCensusPlayer enemy = context.Census.Player(q);
            Consider(world, context.Slot, enemy.Buildings, q, x0, y0, ref best, ref player, ref entity, ref x, ref y);
            Consider(world, context.Slot, enemy.Sites, q, x0, y0, ref best, ref player, ref entity, ref x, ref y);
            if (!preferBuildings || player < 0)
            {
                Consider(world, context.Slot, enemy.Army, q, x0, y0, ref best, ref player, ref entity, ref x, ref y);
                Consider(world, context.Slot, enemy.Workers, q, x0, y0, ref best, ref player, ref entity, ref x, ref y);
            }
        }

        return player >= 0;
    }

    internal static void PruneWave(AiExecutorContext context)
    {
        World world = context.World;
        List<uint> wave = context.State.WaveUnits;
        int kept = 0;
        for (int k = 0; k < wave.Count; k++)
        {
            EntityId e = EntityId.FromValue(wave[k]);
            if (world.Entities.IsAlive(e) && world.Identities.IndexOf(e) is >= 0 and var i && world.Identities.At(i).Owner == context.Slot)
            {
                wave[kept++] = wave[k];
            }
        }

        wave.RemoveRange(kept, wave.Count - kept);
    }

    // In front of the town, toward the map center.
    private static (int X, int Y) GatherPoint(AiExecutorContext context)
    {
        World world = context.World;
        (int tx, int ty) = context.Player.Town;
        long dx = (world.Map.WorldWidth / 2) - tx;
        long dy = (world.Map.WorldHeight / 2) - ty;
        long length = Math.Max(1, AiExecutorContext.Distance(0, 0, (int)dx, (int)dy));
        long reach = Math.Min(length, (long)context.Options.Military.GatherDistanceTiles * MapState.UnitsPerTile);
        int x = (int)Math.Clamp(tx + (dx * reach / length), 0, world.Map.WorldWidth - 1);
        int y = (int)Math.Clamp(ty + (dy * reach / length), 0, world.Map.WorldHeight - 1);
        return Reachable(context, x, y);
    }

    // The walkable point of the town's land nearest to (x, y): a gather point or wave goal inside a building, a forest or
    // water made every unit sent there report an unreachable goal (second play-test log, 2026-10-20).
    private static (int X, int Y) Reachable(AiExecutorContext context, int x, int y)
    {
        World world = context.World;
        AiCensusPlayer me = context.Player;
        EntityId walker = me.Army.Count > 0 ? me.Army[0] : me.Workers.Count > 0 ? me.Workers[0] : EntityId.None;
        if (walker.IsNone || world.Navigation.ForType(world.Identities.Get(walker).TypeId) is not { } layer)
        {
            return (x, y);
        }

        Position from = world.Positions.Get(walker);
        int start = NavQueries.NearestWalkable(layer, layer.TileX(from.X), layer.TileY(from.Y), 4);
        if (start < 0)
        {
            return (x, y);
        }

        int tile = layer.TileOf(x, y);
        int component = layer.ComponentOf(start);
        if (layer.IsWalkablePoint(x, y) && layer.ComponentOf(tile) == component)
        {
            return (x, y);
        }

        int nearest = NavQueries.NearestInComponent(layer, component, x, y, out _);
        return nearest < 0 ? (x, y) : (layer.CenterX(nearest), layer.CenterY(nearest));
    }

    // Producers of soldiers rally at the gather point (one command for all that are not set yet).
    private void Rally(AiExecutorContext context, int x, int y)
    {
        World world = context.World;
        _orders.Clear();
        foreach (EntityId p in context.Player.Producers)
        {
            ProducerDefinition? producer = world.TypeOf(p).Traits.Producer;
            if (producer is null)
            {
                continue;
            }

            bool soldiers = false;
            foreach (ushort t in producer.Produces)
            {
                soldiers |= context.Model.IsArmy(t);
            }

            Production q = world.Productions.Get(p);
            if (soldiers && (q.HasRally == 0 || q.RallyX != x || q.RallyY != y))
            {
                _orders.Add(p);
            }
        }

        if (_orders.Count > 0)
        {
            context.Issue(CommandKind.SetRally, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_orders), x, y);
        }
    }

    // Soldiers at home attack the enemy soldier nearest to the town inside the town box plus the margin.
    private bool Defend(AiExecutorContext context)
    {
        World world = context.World;
        AiCensusPlayer player = context.Player;
        (int x0, int y0, int x1, int y1) = player.TownBox;
        if (x0 > x1)
        {
            return false;
        }

        int margin = context.Options.Military.DefenceMarginTiles;
        long bx0 = (long)(x0 - margin) * MapState.UnitsPerTile;
        long by0 = (long)(y0 - margin) * MapState.UnitsPerTile;
        long bx1 = (long)(x1 + 1 + margin) * MapState.UnitsPerTile;
        long by1 = (long)(y1 + 1 + margin) * MapState.UnitsPerTile;
        bool found = false;
        long best = long.MaxValue;
        Position threatAt = default;
        for (int q = 0; q < context.Census.PlayerCount; q++)
        {
            if (!AiCensus.IsHostileArmy(world, context.Slot, q))
            {
                continue;
            }

            foreach (EntityId e in context.Census.Player(q).Army)
            {
                if (!MayTarget(world, context.Slot, e))
                {
                    continue;
                }

                Position p = world.Positions.Get(e);
                if (p.X < bx0 || p.X >= bx1 || p.Y < by0 || p.Y >= by1)
                {
                    continue;
                }

                long d = AiExecutorContext.Distance(p.X, p.Y, player.Town.X, player.Town.Y);
                if (d < best)
                {
                    best = d;
                    found = true;
                    threatAt = p;
                }
            }
        }

        TacticsLevel tactics = context.Tactics;
        EntityId building = EntityId.None;
        if (!found && (!tactics.DefendBuildings || !BuildingThreat(context, bx0, by0, bx1, by1, out building, out threatAt)))
        {
            return false;
        }

        // The enemy power there (towers in reach, soldiers around, the building itself), and what is already on its way.
        long need = 0;
        if (tactics.DefendPercent > 0)
        {
            need = context.Threats.PowerNear(threatAt.X, threatAt.Y, RadiusOf(context) / 2) * tactics.DefendPercent / 100;
        }

        long committed = 0;
        long near = 6L * MapState.UnitsPerTile;
        _candidates.Clear();
        foreach (EntityId unit in _home)
        {
            int c = world.Combats.IndexOf(unit);
            int m = world.Motions.IndexOf(unit);
            if (c < 0 || m < 0)
            {
                continue;
            }

            uint target = world.Combats.At(c).Target;
            Motion motion = world.Motions.At(m);
            if ((!building.IsNone && target == building.Value)
                || (target == 0 && motion.State == MotionState.Moving && AiExecutorContext.Distance(motion.OrderX, motion.OrderY, threatAt.X, threatAt.Y) <= near))
            {
                committed += AiThreats.PowerOf(world, unit);
                continue;
            }

            if (target != 0)
            {
                continue; // fighting something else
            }

            Position p = world.Positions.Get(unit);
            _candidates.Add((AiExecutorContext.Distance(p.X, p.Y, threatAt.X, threatAt.Y), unit));
        }

        // The nearest free soldiers until their power is enough (all of them when nothing is weighed).
        _candidates.Sort(static (a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Unit.Index.CompareTo(b.Unit.Index));
        _orders.Clear();
        long sent = committed;
        foreach ((long _, EntityId unit) in _candidates)
        {
            if (need > 0 && sent >= need)
            {
                break;
            }

            _orders.Add(unit);
            sent += AiThreats.PowerOf(world, unit);
        }

        if (!building.IsNone && sent < need)
        {
            return true; // too strong for now: the army stays home and grows
        }

        if (building.IsNone)
        {
            IssueGroups(context, _orders, CommandKind.Move, EntityId.None, threatAt.X, threatAt.Y, CommandFlags.Ctrl);
        }
        else
        {
            IssueGroups(context, _orders, CommandKind.Attack, building, threatAt.X, threatAt.Y, CommandFlags.None);
        }

        return true;
    }

    // An enemy tower whose reach comes into the box (the one nearest to the town), else the enemy building or site in the
    // box nearest to the town.
    private static bool BuildingThreat(AiExecutorContext context, long bx0, long by0, long bx1, long by1, out EntityId building, out Position at)
    {
        World world = context.World;
        (int tx, int ty) = context.Player.Town;
        building = EntityId.None;
        at = default;
        long best = long.MaxValue;
        foreach (AiThreat t in context.Threats.Towers)
        {
            long dx = Math.Max(Math.Max(bx0 - t.X, t.X - (bx1 - 1)), 0);
            long dy = Math.Max(Math.Max(by0 - t.Y, t.Y - (by1 - 1)), 0);
            long d = AiExecutorContext.Distance(tx, ty, t.X, t.Y);
            if (AiCensus.IsEnemy(world, context.Slot, t.Owner) && (dx * dx) + (dy * dy) <= (long)t.Reach * t.Reach && d < best)
            {
                best = d;
                building = t.Id;
                at = new Position(t.X, t.Y);
            }
        }

        if (!building.IsNone)
        {
            return true;
        }

        for (int q = 0; q < context.Census.PlayerCount; q++)
        {
            if (!AiCensus.IsEnemy(world, context.Slot, q))
            {
                continue;
            }

            AiCensusPlayer enemy = context.Census.Player(q);
            Inside(world, enemy.Buildings, tx, ty, (bx0, by0, bx1, by1), ref best, ref building, ref at);
            Inside(world, enemy.Sites, tx, ty, (bx0, by0, bx1, by1), ref best, ref building, ref at);
        }

        return !building.IsNone;

        static void Inside(World world, List<EntityId> list, int tx, int ty, (long X0, long Y0, long X1, long Y1) box, ref long best, ref EntityId building, ref Position at)
        {
            foreach (EntityId b in list)
            {
                Position p = world.Positions.Get(b);
                if (p.X < box.X0 || p.X >= box.X1 || p.Y < box.Y0 || p.Y >= box.Y1)
                {
                    continue;
                }

                long d = AiExecutorContext.Distance(tx, ty, p.X, p.Y);
                if (d < best)
                {
                    best = d;
                    building = b;
                    at = p;
                }
            }
        }
    }

    private static int RadiusOf(AiExecutorContext context) => context.Options.Tactics.ThreatRadiusTiles * MapState.UnitsPerTile;

    private void Launch(AiExecutorContext context)
    {
        World world = context.World;
        AiPlayerState state = context.State;
        MilitaryOptions options = context.Options.Military;
        int tick = world.Tick.Value;
        int tickMs = world.Config.TickLengthMilliseconds;
        int threshold = Math.Min(options.MaxWaveSize, options.FirstWaveSize + (options.WaveGrowth * state.WaveCount));
        if ((long)tick * tickMs < options.FirstWaveMs || ((long)tick - state.LastWaveTick) * tickMs < options.WaveCooldownMs || _home.Count < threshold)
        {
            return;
        }

        if (!NearestEnemyTarget(context, context.Player.Town.X, context.Player.Town.Y, preferBuildings: true, out int target, out _, out int x, out int y))
        {
            return;
        }

        // Tactics: the least defended building of that enemy (Hard), and only with enough power for its defence.
        TacticsLevel tactics = context.Tactics;
        if (tactics.WeakestTarget)
        {
            (x, y) = WeakestBuilding(context, target, x, y);
        }

        if (tactics.LaunchPercent > 0 && _home.Count < options.MaxWaveSize)
        {
            long power = 0;
            for (int i = 0; i < _home.Count && i < AiPlayerState.MaxWaveUnits; i++)
            {
                power += AiThreats.PowerOf(world, _home[i]);
            }

            if (power * 100 < context.Threats.PowerNear(x, y, RadiusOf(context)) * tactics.LaunchPercent)
            {
                return; // wait for more soldiers
            }
        }

        state.WaveUnits.Clear();
        for (int i = 0; i < _home.Count && state.WaveUnits.Count < AiPlayerState.MaxWaveUnits; i++)
        {
            state.WaveUnits.Add(_home[i].Value);
        }

        state.WavePhase = AiWavePhase.Attacking;
        state.WaveCount++;
        state.WaveLaunchSize = state.WaveUnits.Count;
        state.LastWaveTick = tick;
        state.TargetPlayer = target;
        state.TargetX = x;
        state.TargetY = y;
        if (_home.Count > state.WaveUnits.Count)
        {
            _home.RemoveRange(state.WaveUnits.Count, _home.Count - state.WaveUnits.Count); // only tracked units go
        }

        (int gx, int gy) = Reachable(context, x, y);
        IssueGroups(context, _home, CommandKind.Move, EntityId.None, gx, gy, CommandFlags.Ctrl);
    }

    private void Attack(AiExecutorContext context, int gx, int gy)
    {
        World world = context.World;
        AiPlayerState state = context.State;
        if (state.WaveUnits.Count == 0 || state.WaveUnits.Count * 100L < (long)state.WaveLaunchSize * context.Options.Military.RetreatPercent)
        {
            Recall(context, gx, gy);
            return;
        }

        // The wave's centroid and power; idle members get a target near it.
        long sx = 0;
        long sy = 0;
        long power = 0;
        _orders.Clear();
        foreach (uint v in state.WaveUnits)
        {
            EntityId e = EntityId.FromValue(v);
            Position p = world.Positions.Get(e);
            sx += p.X;
            sy += p.Y;
            power += AiThreats.PowerOf(world, e);
            if (!context.IsClaimed(e) && IsIdleSoldier(world, e))
            {
                _orders.Add(e);
            }
        }

        int cx = (int)(sx / state.WaveUnits.Count);
        int cy = (int)(sy / state.WaveUnits.Count);
        TacticsLevel tactics = context.Tactics;
        if (tactics.RetreatPercent > 0 && power * 100 < context.Threats.PowerNear(cx, cy, RadiusOf(context)) * tactics.RetreatPercent)
        {
            Recall(context, gx, gy); // outmatched where it stands
            return;
        }

        if (_orders.Count == 0)
        {
            return;
        }

        if (tactics.FocusTargets && DangerNear(context, cx, cy, out int owner, out EntityId danger, out int dx, out int dy))
        {
            state.TargetPlayer = owner;
            state.TargetX = dx;
            state.TargetY = dy;
            IssueGroups(context, _orders, CommandKind.Attack, danger, dx, dy, CommandFlags.None);
            return;
        }

        if (!NearestEnemyTarget(context, cx, cy, preferBuildings: false, out int target, out EntityId entity, out int x, out int y))
        {
            Recall(context, gx, gy); // nothing left to fight
            return;
        }

        state.TargetPlayer = target;
        state.TargetX = x;
        state.TargetY = y;

        // Close by: attack it outright (a building is not always in an idle soldier's sight); else move-advance to it.
        bool close = AiExecutorContext.Distance(cx, cy, x, y) <= 12L * MapState.UnitsPerTile;
        if (close)
        {
            IssueGroups(context, _orders, CommandKind.Attack, entity, x, y, CommandFlags.None);
        }
        else
        {
            (int ax, int ay) = Reachable(context, x, y);
            IssueGroups(context, _orders, CommandKind.Move, EntityId.None, ax, ay, CommandFlags.Ctrl);
        }
    }

    // What idle wave soldiers attack first (tactics FocusTargets), within the threat radius of the wave: the nearest
    // enemy soldier, else the nearest tower that reaches the wave, else the nearest other tower, else the nearest worker.
    private static bool DangerNear(AiExecutorContext context, int cx, int cy, out int owner, out EntityId entity, out int x, out int y)
    {
        World world = context.World;
        long radius = RadiusOf(context);
        owner = -1;
        entity = EntityId.None;
        x = y = 0;
        long best = long.MaxValue;
        foreach (AiThreat s in context.Threats.Soldiers)
        {
            long d = AiExecutorContext.Distance(cx, cy, s.X, s.Y);
            if (d <= radius && d < best && AiCensus.IsEnemy(world, context.Slot, s.Owner))
            {
                (best, owner, entity, x, y) = (d, s.Owner, s.Id, s.X, s.Y);
            }
        }

        for (int pass = 0; pass < 2 && entity.IsNone; pass++)
        {
            foreach (AiThreat t in context.Threats.Towers)
            {
                long d = AiExecutorContext.Distance(cx, cy, t.X, t.Y);
                if (d <= (pass == 0 ? t.Reach : radius) && d < best && AiCensus.IsEnemy(world, context.Slot, t.Owner))
                {
                    (best, owner, entity, x, y) = (d, t.Owner, t.Id, t.X, t.Y);
                }
            }
        }

        for (int q = 0; q < context.Census.PlayerCount && entity.IsNone; q++)
        {
            if (!AiCensus.IsEnemy(world, context.Slot, q))
            {
                continue;
            }

            foreach (EntityId w in context.Census.Player(q).Workers)
            {
                if (!MayTarget(world, context.Slot, w))
                {
                    continue;
                }

                Position p = world.Positions.Get(w);
                long d = AiExecutorContext.Distance(cx, cy, p.X, p.Y);
                if (d <= radius && d < best)
                {
                    (best, owner, entity, x, y) = (d, q, w, p.X, p.Y);
                }
            }
        }

        return !entity.IsNone;
    }

    // The building of player q with the least enemy power around it (ties: the nearest to the own town).
    private static (int X, int Y) WeakestBuilding(AiExecutorContext context, int q, int x, int y)
    {
        World world = context.World;
        AiThreats threats = context.Threats;
        int radius = RadiusOf(context);
        (int tx, int ty) = context.Player.Town;
        long bestDefence = threats.PowerNear(x, y, radius);
        long bestDistance = AiExecutorContext.Distance(tx, ty, x, y);
        foreach (EntityId b in context.Census.Player(q).Buildings)
        {
            Position p = world.Positions.Get(b);
            long defence = threats.PowerNear(p.X, p.Y, radius);
            long d = AiExecutorContext.Distance(tx, ty, p.X, p.Y);
            if (defence < bestDefence || (defence == bestDefence && d < bestDistance))
            {
                (bestDefence, bestDistance, x, y) = (defence, d, p.X, p.Y);
            }
        }

        return (x, y);
    }

    private void Recall(AiExecutorContext context, int gx, int gy)
    {
        AiPlayerState state = context.State;
        _orders.Clear();
        foreach (uint v in state.WaveUnits)
        {
            _orders.Add(EntityId.FromValue(v));
        }

        state.WaveUnits.Clear();
        state.WavePhase = AiWavePhase.Gathering;
        state.TargetPlayer = -1;
        IssueGroups(context, _orders, CommandKind.Move, EntityId.None, gx, gy, CommandFlags.None);
    }

    private static bool IsIdleSoldier(World world, EntityId e)
    {
        int c = world.Combats.IndexOf(e);
        int m = world.Motions.IndexOf(e);
        return c >= 0 && m >= 0 && world.Combats.At(c).Target == 0 && world.Motions.At(m).State != MotionState.Moving;
    }

    private static void Consider(World world, int slot, List<EntityId> entities, int owner, int x0, int y0, ref long best, ref int player, ref EntityId entity, ref int x, ref int y)
    {
        foreach (EntityId e in entities)
        {
            if (!MayTarget(world, slot, e))
            {
                continue;
            }

            Position p = world.Positions.Get(e);
            long d = AiExecutorContext.Distance(x0, y0, p.X, p.Y);
            if (d < best)
            {
                best = d;
                player = owner;
                entity = e;
                x = p.X;
                y = p.Y;
            }
        }
    }

    // Units inside holders are not on the map; invisible ones only where a detector of the AI's kingdom sees them
    // (GAME-09/11/12): the AI never aims at what its player could not see or hit.
    internal static bool MayTargetUnit(World world, int slot, EntityId e) => MayTarget(world, slot, e);

    private static bool MayTarget(World world, int slot, EntityId e)
        => !world.IsHeld(e) && !(world.IsCloaked(e) && world.Visibility.IsConcealedFrom(world, slot, e, world.Positions.Get(e)));

    private static void IssueGroups(AiExecutorContext context, List<EntityId> units, CommandKind kind, EntityId target, int x, int y, CommandFlags flags)
    {
        int size = context.Options.Military.GroupSize;
        ReadOnlySpan<EntityId> all = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(units);
        for (int start = 0; start < all.Length; start += size)
        {
            if (!context.Issue(kind, all.Slice(start, Math.Min(size, all.Length - start)), x, y, target, flags: flags))
            {
                return;
            }
        }
    }
}
