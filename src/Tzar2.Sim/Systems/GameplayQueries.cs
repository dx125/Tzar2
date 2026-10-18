using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Read-only gameplay questions for in-sim controllers (the AI, ADR 0051) that <see cref="PlacementQueries"/> does not
/// answer. The commands re-check everything when they run.
/// </summary>
public static class GameplayQueries
{
    /// <summary>Whether a worker of <paramref name="player"/> may gather from <paramref name="node"/> (Nature's nodes, or the player's own fields).</summary>
    public static bool MayHarvest(World world, byte player, EntityId node)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Entities.IsAlive(node) && world.Identities.Has(node) && Interaction.MayHarvest(world, player, node);
    }

    /// <summary>Whether any of <paramref name="player"/>'s queues holds research of <paramref name="technology"/>.</summary>
    public static bool IsResearchPending(World world, byte player, int technology)
    {
        ArgumentNullException.ThrowIfNull(world);
        return (uint)technology < (uint)world.Ruleset.Technologies.Count && ResearchRules.IsResearchPending(world, player, technology);
    }

    /// <summary>
    /// Whether a <see cref="Commands.CommandKind.Research"/> of <paramref name="technology"/> listing only
    /// <paramref name="building"/> would queue there now, apart from the cost: the player's completed building that
    /// researches it, by the building's civilization's tree, with a free queue slot, not researched or pending.
    /// </summary>
    public static bool CanResearchAt(World world, byte player, EntityId building, int technology)
    {
        ArgumentNullException.ThrowIfNull(world);
        if ((uint)technology >= (uint)world.Ruleset.Technologies.Count || !world.HasPlayer(player) || !world.Entities.IsAlive(building)
            || world.Identities.IndexOf(building) is not (>= 0 and var i) || world.Identities.At(i).Owner != player
            || world.Productions.IndexOf(building) is not (>= 0 and var q))
        {
            return false;
        }

        ProducerDefinition? producer = world.TypeOf(building).Traits.Producer;
        return producer is not null && producer.CanResearch((ushort)technology) && world.Productions.At(q).Count < producer.QueueCapacity
            && !world.Players[player].HasTechnology(technology) && !ResearchRules.IsResearchPending(world, player, technology)
            && ResearchRules.RequirementsMet(world, player, world.CivilizationOf(building), world.Ruleset.Technologies[technology].Requirements, []);
    }
}
