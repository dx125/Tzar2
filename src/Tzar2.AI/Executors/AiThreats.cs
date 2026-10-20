using System;
using System.Collections.Generic;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;

namespace Tzar2.AI.Executors;

/// <summary>An armed object of a kingdom the AI may fight.</summary>
/// <param name="Id">The entity.</param>
/// <param name="Owner">Its kingdom.</param>
/// <param name="X">Position x (world units).</param>
/// <param name="Y">Position y (world units).</param>
/// <param name="Reach">How far from its center it hits (world units): range plus half its footprint.</param>
/// <param name="Power">Its combat power (<see cref="AiThreats.PowerOf"/>).</param>
public readonly record struct AiThreat(EntityId Id, byte Owner, int X, int Y, int Reach, int Power);

/// <summary>
/// The armed forces around an AI (ADR 0178): the towers and other armed buildings of the kingdoms it may fight, with the
/// circle they cover, and their soldiers, each with a combat power. Built once per executor run, on first use; the
/// tactics compare powers (Lanchester: a side's strength grows with the sum of √(health × damage rate) of its members).
/// </summary>
public sealed class AiThreats
{
    private readonly List<AiThreat> _towers = [];
    private readonly List<AiThreat> _soldiers = [];

    /// <summary>Armed buildings of the kingdoms the AI may fight (enemy or neutral stance), completed only.</summary>
    public IReadOnlyList<AiThreat> Towers => _towers;

    /// <summary>Soldiers of those kingdoms that are on the map.</summary>
    public IReadOnlyList<AiThreat> Soldiers => _soldiers;

    /// <summary>
    /// The combat power of an armed entity: √(health × attack per second); 0 without an armament. Damage classes,
    /// defence and bonuses are left out on purpose: the tactics only need to tell a hopeless fight from a fair one.
    /// </summary>
    public static int PowerOf(World world, EntityId e)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.TypeOf(e).Traits.Armament is not { } arm || arm.Attack <= 0 || world.Healths.IndexOf(e) is not (>= 0 and var h))
        {
            return 0;
        }

        long rate = (long)world.Healths.At(h).Current * ((arm.Attack + Math.Max(arm.Attack, arm.MaxAttack)) / 2) * 1000 / arm.CooldownMs;
        return (int)IntMath.ISqrt(rate);
    }

    /// <summary>Whether a tower covers (<paramref name="x"/>, <paramref name="y"/>) with <paramref name="margin"/> world units to spare.</summary>
    public bool Covered(int x, int y, int margin)
    {
        foreach (AiThreat t in _towers)
        {
            long r = (long)t.Reach + margin;
            if (AiExecutorContext.Distance(x, y, t.X, t.Y) <= r)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The enemy power that can fight at (<paramref name="x"/>, <paramref name="y"/>): soldiers within
    /// <paramref name="radius"/> and towers whose reach comes within <paramref name="radius"/> of it.
    /// </summary>
    public long PowerNear(int x, int y, int radius)
    {
        long sum = 0;
        foreach (AiThreat t in _towers)
        {
            if (AiExecutorContext.Distance(x, y, t.X, t.Y) <= (long)t.Reach + radius)
            {
                sum += t.Power;
            }
        }

        foreach (AiThreat s in _soldiers)
        {
            if (AiExecutorContext.Distance(x, y, s.X, s.Y) <= radius)
            {
                sum += s.Power;
            }
        }

        return sum;
    }

    internal void Build(AiExecutorContext context)
    {
        _towers.Clear();
        _soldiers.Clear();
        World world = context.World;
        for (int q = 0; q < context.Census.PlayerCount; q++)
        {
            if (!AiCensus.IsHostileArmy(world, context.Slot, q))
            {
                continue;
            }

            AiCensusPlayer enemy = context.Census.Player(q);
            foreach (EntityId b in enemy.Buildings)
            {
                EntityTypeDefinition type = world.TypeOf(b);
                if (type.Traits.Armament is not { RangeUnits: > 0 } arm)
                {
                    continue;
                }

                Position p = world.Positions.Get(b);
                int half = Math.Max(type.FootprintWidth, type.FootprintHeight) * MapState.UnitsPerTile / 2;
                _towers.Add(new AiThreat(b, (byte)q, p.X, p.Y, arm.RangeUnits + half, PowerOf(world, b)));
            }

            foreach (EntityId s in enemy.Army)
            {
                if (!MilitaryExecutor.MayTargetUnit(world, context.Slot, s))
                {
                    continue;
                }

                Position p = world.Positions.Get(s);
                int reach = world.TypeOf(s).Traits.Armament?.RangeUnits ?? 0;
                _soldiers.Add(new AiThreat(s, (byte)q, p.X, p.Y, reach, PowerOf(world, s)));
            }
        }
    }
}
