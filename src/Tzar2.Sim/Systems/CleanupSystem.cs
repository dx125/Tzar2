using System;
using System.Collections.Generic;
using Tzar2.Core.Ids;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Pipeline step 17: removes entities whose lifetime ended (meat rots, trait <c>Lifetime</c>), recycles the entity slots
/// destroyed during the tick and expires decals.
/// </summary>
public sealed class CleanupSystem : ISimSystem
{
    private readonly List<EntityId> _expired = [];

    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Cleanup;

    /// <inheritdoc />
    public string Name => "cleanup";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        World world = context.World;
        _expired.Clear();
        ReadOnlySpan<EntityId> ids = world.Lifetimes.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            if (world.Lifetimes.Components[i].ExpiresTick <= world.Tick.Value)
            {
                _expired.Add(ids[i]);
            }
        }

        foreach (EntityId e in _expired)
        {
            world.Destroy(e);
        }

        world.Entities.CommitDestroyed();
        world.Decals.Expire(world.Tick.Value);
    }
}
