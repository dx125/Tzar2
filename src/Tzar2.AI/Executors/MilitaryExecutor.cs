using System;
using System.Collections.Generic;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;

namespace Tzar2.AI.Executors;

/// <summary>
/// Defence, attack waves and targeting (REQ-AI-02 military manager; ADR 0052). The scripts only say what to train; the
/// original's war AI is hard-coded, so this is the most plausible behaviour (UNVERIFIED, docs/mechanics/ai-placeholders.md):
/// <list type="bullet">
/// <item><b>Defence:</b> enemy soldiers near the own buildings are attacked by the soldiers at home (move-advance).</item>
/// <item><b>Gathering:</b> producers rally new soldiers at a gather point in front of the town.</item>
/// <item><b>Waves:</b> after <see cref="MilitaryOptions.FirstWaveMs"/>, when enough soldiers are at home, they all
/// move-advance to the nearest building of the nearest enemy; the threshold grows with every wave.</item>
/// <item><b>Targeting:</b> wave soldiers that stand idle attack the enemy object nearest to the wave (move-advance when
/// it is far); a wave reduced below <see cref="MilitaryOptions.RetreatPercent"/> % of its launch size goes home.</item>
/// </list>
/// </summary>
public sealed class MilitaryExecutor : IAiExecutor
{
    private readonly List<EntityId> _home = [];
    private readonly List<EntityId> _orders = [];
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
            Attack(context, gx, gy);
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

    private static void PruneWave(AiExecutorContext context)
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
        return (x, y);
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

        if (!found)
        {
            return false;
        }

        // Soldiers at home that are not fighting and not already heading there.
        _orders.Clear();
        long near = 6L * MapState.UnitsPerTile;
        foreach (EntityId unit in _home)
        {
            int c = world.Combats.IndexOf(unit);
            int m = world.Motions.IndexOf(unit);
            if (c < 0 || m < 0 || world.Combats.At(c).Target != 0)
            {
                continue;
            }

            Motion motion = world.Motions.At(m);
            if (motion.State == MotionState.Moving && AiExecutorContext.Distance(motion.OrderX, motion.OrderY, threatAt.X, threatAt.Y) <= near)
            {
                continue;
            }

            _orders.Add(unit);
        }

        IssueGroups(context, _orders, CommandKind.Move, EntityId.None, threatAt.X, threatAt.Y, CommandFlags.Ctrl);
        return true;
    }

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

        IssueGroups(context, _home, CommandKind.Move, EntityId.None, x, y, CommandFlags.Ctrl);
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

        // The wave's centroid; idle members get the enemy object nearest to it.
        long sx = 0;
        long sy = 0;
        _orders.Clear();
        foreach (uint v in state.WaveUnits)
        {
            EntityId e = EntityId.FromValue(v);
            Position p = world.Positions.Get(e);
            sx += p.X;
            sy += p.Y;
            if (!context.IsClaimed(e) && IsIdleSoldier(world, e))
            {
                _orders.Add(e);
            }
        }

        if (_orders.Count == 0)
        {
            return;
        }

        int cx = (int)(sx / state.WaveUnits.Count);
        int cy = (int)(sy / state.WaveUnits.Count);
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
        IssueGroups(context, _orders, close ? CommandKind.Attack : CommandKind.Move, close ? entity : EntityId.None, x, y, close ? CommandFlags.None : CommandFlags.Ctrl);
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
