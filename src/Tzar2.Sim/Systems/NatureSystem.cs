using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Core.Rng;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 6 (activities, GAME-15, ADR 0104): Nature's entities. A magic gateway (or a wild cow) owned by Nature
/// becomes the kingdom whose unit touches it first (gateways: a military unit; ties: the nearer, then the lower entity
/// index); animals wander around their home; guarding units (bears, guarded-town defenders) drop the chase and walk back
/// when lured further than <c>guardRadiusTiles</c> from home.
/// </summary>
public sealed class NatureSystem : ISimSystem
{
    private readonly List<(EntityId Entity, byte Owner)> _claims = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Activities;

    /// <inheritdoc />
    public string Name => "nature";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        int tick = world.Tick.Value;
        int every = Math.Max(1, world.Ruleset.World.ClaimIntervalMs / world.Config.TickLengthMilliseconds);
        if (tick % every == 0)
        {
            Claims(world);
        }

        Roam(world, tick);
    }

    private void Claims(World world)
    {
        _claims.Clear();
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        long reach = world.Ruleset.World.ClaimReachUnits;
        for (int i = 0; i < ids.Length; i++)
        {
            if (identities[i].Owner != MapObject.Nature || world.Ruleset.EntityTypes[identities[i].TypeId].Traits.World.Claim is not { } by
                || world.Constructions.Has(ids[i]))
            {
                continue;
            }

            EntityId target = ids[i];
            EntityId best = EntityId.None;
            long bestDistance = long.MaxValue;
            ReadOnlySpan<EntityId> movers = world.Motions.Entities;
            for (int m = 0; m < movers.Length; m++)
            {
                Identity u = world.Identities.Get(movers[m]);
                if (!world.HasPlayer(u.Owner) || (world.Players[u.Owner].Flags & PlayerFlags.Defeated) != 0 || movers[m] == target)
                {
                    continue;
                }

                EntityTraits t = world.Ruleset.EntityTypes[u.TypeId].Traits;
                if (t.Category != EntityCategory.Unit || (by == ClaimBy.Military && !t.World.IsMilitary))
                {
                    continue;
                }

                long d = Interaction.EdgeDistance(world, world.Positions.Get(movers[m]), Interaction.RadiusOf(world, movers[m]), target);
                if (d <= reach && d < bestDistance)
                {
                    bestDistance = d;
                    best = movers[m];
                }
            }

            if (!best.IsNone)
            {
                _claims.Add((target, world.Identities.Get(best).Owner));
            }
        }

        foreach ((EntityId target, byte owner) in _claims)
        {
            world.NatureHomes.Remove(target);
            GameplayCommands.ClearTasks(world, target);
            Interaction.Halt(world, target); // a claimed cow stops wandering and stays by the peasant who milks it
            world.ChangeOwner(target, owner);
            world.Events.Emit(new SimEvent(SimEventKind.Claimed, owner, target, 0, 0, world.Identities.Get(target).TypeId));
        }
    }

    private static void Roam(World world, int tick)
    {
        if (world.NatureHomes.Count == 0)
        {
            return;
        }

        WorldSettings w = world.Ruleset.World;
        long guard = (long)w.GuardRadiusTiles * MapState.UnitsPerTile;
        int intervalTicks = Math.Max(1, w.WanderIntervalMs / world.Config.TickLengthMilliseconds);
        ReadOnlySpan<EntityId> ids = world.NatureHomes.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            EntityId e = ids[i];
            ref NatureHome home = ref world.NatureHomes.At(i);
            WorldTraits traits = world.TypeOf(e).Traits.World;
            Position p = world.Positions.Get(e);
            long away = IntMath.DistanceSquared(p.X, p.Y, home.X, home.Y);
            if (traits.Guards && home.Returning == 0 && away > guard * guard)
            {
                // Lured away: give up the fight and walk home.
                GameplayCommands.ClearTasks(world, e);
                Interaction.MoveTo(world, e, home.X, home.Y);
                home.Returning = 1;
                continue;
            }

            bool moving = Interaction.IsMoving(world, e);
            if (home.Returning != 0)
            {
                if (!moving)
                {
                    home.Returning = 0;
                    home.NextMoveTick = tick + intervalTicks;
                }

                continue;
            }

            bool fighting = world.Combats.IndexOf(e) is >= 0 and var c && world.Combats.At(c).Target != 0;
            if (!traits.Wanders || moving || fighting || tick < home.NextMoveTick || !world.Motions.Has(e))
            {
                continue;
            }

            // A wander target hashed from the match seed, the entity and the tick (no RNG stream is consumed).
            ulong roll = SplitMix64.Mix(world.Config.MatchSeed ^ ((ulong)e.Value << 20) ^ (ulong)tick);
            int r = (w.WanderRadiusTiles * MapState.UnitsPerTile) + 1;
            int x = Math.Clamp(home.X + (int)(roll % (ulong)(2 * r)) - r, 0, world.Map.WorldWidth - 1);
            int y = Math.Clamp(home.Y + (int)((roll >> 32) % (ulong)(2 * r)) - r, 0, world.Map.WorldHeight - 1);
            Interaction.MoveTo(world, e, x, y);
            home.NextMoveTick = tick + (intervalTicks / 2) + (int)((roll >> 48) % (ulong)intervalTicks);
        }
    }
}
