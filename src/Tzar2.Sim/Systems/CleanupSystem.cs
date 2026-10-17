using System;

namespace Tzar2.Sim.Systems;

/// <summary>Pipeline step 17: recycles the entity slots destroyed during the tick and expires decals.</summary>
public sealed class CleanupSystem : ISimSystem
{
    /// <inheritdoc />
    public SimPhase Phase => SimPhase.Cleanup;

    /// <inheritdoc />
    public string Name => "cleanup";

    /// <inheritdoc />
    public void Execute(SimContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.World.Entities.CommitDestroyed();
        context.World.Decals.Expire(context.World.Tick.Value + 1);
    }
}
