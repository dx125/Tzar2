using System;
using Tzar2.Core.Hashing;
using Tzar2.Sim.Ecs;

namespace Tzar2.Sim.Snapshots;

/// <summary>
/// Per-tick and full-state hashes (REQ-SIM-12, ADR 0016). Both are xxHash64 through <see cref="StateHasher"/>, whose
/// length-prefix contract (ADR 0005) keeps adjacent spans from aliasing.
/// </summary>
/// <remarks>
/// <b>Tick hash</b>: version tag, tick, entity registry, every hot store (in registration order: store index, then
/// its entity and component spans), player states, RNG streams. <b>Full hash</b>: the tick hash's inputs plus every
/// cold store and the map's dynamic layers (obstacle version, blockers, occupancy), the decals, the group counter, the paths, the path-request queue
/// and the extension state of in-sim controllers such as the AI (only when there is any, ADR 0051).
/// </remarks>
public static class StateHashing
{
    /// <summary>Changes whenever the hashed layout changes; part of every hash.</summary>
    public const int LayoutVersion = 10;

    /// <summary>Computes the per-tick hash of <paramref name="world"/>.</summary>
    public static ulong ComputeTickHash(World world, StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Reset();
        AppendHot(world, hasher, tag: 1);
        return hasher.GetHashAndReset();
    }

    /// <summary>Computes the full-state hash of <paramref name="world"/>.</summary>
    public static ulong ComputeFullHash(World world, StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Reset();
        AppendHot(world, hasher, tag: 2);
        int index = 0;
        foreach (IComponentStore store in world.Components.All)
        {
            if (store.Tier == ComponentTier.Cold)
            {
                hasher.Add(index);
                store.AppendTo(hasher);
            }

            index++;
        }

        world.Map.AppendTo(hasher);
        world.Decals.AppendTo(hasher);
        hasher.Add(world.NextGroupId);
        world.Paths.AppendTo(hasher);
        world.PathQueue.AppendTo(hasher);
        world.Visibility.AppendTo(hasher);
        world.Extensions.AppendTo(hasher); // nothing without extensions (ADR 0051)
        return hasher.GetHashAndReset();
    }

    private static void AppendHot(World world, StateHasher hasher, int tag)
    {
        ArgumentNullException.ThrowIfNull(world);
        hasher.Add(LayoutVersion).Add(tag).Add(world.Tick.Value);
        world.Entities.AppendTo(hasher);
        int index = 0;
        foreach (IComponentStore store in world.Components.All)
        {
            if (store.Tier == ComponentTier.Hot)
            {
                hasher.Add(index);
                store.AppendTo(hasher);
            }

            index++;
        }

        hasher.AddSpan<PlayerState>(world.Players);
        world.AppendKingdomsTo(hasher);
        world.Rng.AppendTo(hasher);
    }
}
