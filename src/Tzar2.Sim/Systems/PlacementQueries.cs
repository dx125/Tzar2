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
    /// <summary>
    /// Whether a footprint tile at (<paramref name="tileX"/>, <paramref name="tileY"/>) may be covered: walkable land
    /// without a structure, a bridge deck or a field.
    /// </summary>
    public static bool IsBuildableTile(World world, int tileX, int tileY)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Map.ContainsTile(tileX, tileY) && world.Map.IsPassable(Locomotor.Foot, tileX, tileY)
            && world.Map.Bridges[(tileY * world.Map.Width) + tileX] == 0 && !world.IsUnderWalkableFootprint(tileX, tileY);
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
    /// Whether <paramref name="player"/> may place <paramref name="typeId"/> there: <see cref="CanPlace"/>, or a gate over
    /// pieces of the player's own finished wall, which it replaces (BLD-018).
    /// </summary>
    public static bool CanPlaceFor(World world, byte player, ushort typeId, int tileX, int tileY, bool rotated = false)
        => CanPlace(world, typeId, tileX, tileY, rotated)
            || (typeId < world.Ruleset.EntityTypes.Count && GameplayCommands.WallsUnderGate(world, player, world.Ruleset.EntityTypes[typeId], tileX, tileY, rotated) is not null);

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

    /// <summary>
    /// Whether <paramref name="player"/> meets the prerequisites of technology <paramref name="technology"/> by its
    /// civilization's tree (the research command's check before cost and queue room; ADR 0040).
    /// </summary>
    public static bool ResearchRequirementsMet(World world, byte player, int technology)
    {
        ArgumentNullException.ThrowIfNull(world);
        return (uint)technology < (uint)world.Ruleset.Technologies.Count
            && ResearchRules.RequirementsMet(world, player, world.PlayerCivilization(player), world.Ruleset.Technologies[technology].Requirements, []);
    }
}
