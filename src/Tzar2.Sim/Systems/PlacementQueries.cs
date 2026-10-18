using System;
using Tzar2.Content.Compiled;

namespace Tzar2.Sim.Systems;

/// <summary>
/// Read-only placement queries for hosts (build-placement ghosts, AI players; ADR 0070). They answer exactly what the
/// <see cref="Commands.CommandKind.Build"/> command checks at execution, so a ghost shown as legal is accepted unless the
/// state changes before the command's tick. Call them between ticks on the sim thread (the world is not thread-safe).
/// </summary>
public static class PlacementQueries
{
    /// <summary>Whether a footprint tile at (<paramref name="tileX"/>, <paramref name="tileY"/>) may be covered: walkable land without a structure.</summary>
    public static bool IsBuildableTile(World world, int tileX, int tileY)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Map.ContainsTile(tileX, tileY) && world.Map.IsPassable(Locomotor.Foot, tileX, tileY);
    }

    /// <summary>
    /// Whether <paramref name="typeId"/>'s footprint fits with its top-left at the tile (<paramref name="tileX"/>,
    /// <paramref name="tileY"/>), under its placement rule (land, coast, bridge); <paramref name="rotated"/> turns it by 90°.
    /// </summary>
    public static bool CanPlace(World world, ushort typeId, int tileX, int tileY, bool rotated = false)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (typeId >= world.Ruleset.EntityTypes.Count)
        {
            return false;
        }

        EntityTypeDefinition type = world.Ruleset.EntityTypes[typeId];
        return type.HasFootprint && GameplayCommands.CanPlace(world, type, tileX, tileY, rotated);
    }

    /// <summary>
    /// Whether <paramref name="player"/> has every building and technology <paramref name="typeId"/> requires, by the
    /// player's civilization's tree (ADR 0040; the acting builder's or producer's civilization decides in commands).
    /// </summary>
    public static bool RequirementsMet(World world, byte player, ushort typeId)
    {
        ArgumentNullException.ThrowIfNull(world);
        return typeId < world.Ruleset.EntityTypes.Count
            && GameplayCommands.RequirementsMet(world, player, world.Ruleset.EntityTypes[typeId], world.PlayerCivilization(player));
    }
}
