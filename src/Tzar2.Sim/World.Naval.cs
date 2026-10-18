using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Ecs;

namespace Tzar2.Sim;

/// <content>
/// Naval state (REQ-PATH-09, GAME-02/03/09; ADRs 0140-0143): ships sailing to a shore to meet, unload or be repaired,
/// and units exploring on their own.
/// </content>
public sealed partial class World
{
    /// <summary>Ships on their way to a shore point (hot).</summary>
    public ComponentStore<Docking> Dockings { get; private set; } = null!;

    /// <summary>Units exploring (hot).</summary>
    public ComponentStore<Exploring> Explorers { get; private set; } = null!;

    /// <summary>Whether an entity is a ship (a mobile unit of the ship locomotor).</summary>
    public bool IsShip(EntityId entity)
        => Identities.IndexOf(entity) is >= 0 and var i && Ruleset.EntityTypes[Identities.At(i).TypeId] is { IsMobile: true, Locomotor: Locomotor.Ship };

    // Registration order fixes the snapshot and hash layout (ADR 0016): appended after the stream E stores.
    private void RegisterNavalStores()
    {
        Dockings = Components.RegisterHot<Docking>("game.dock");
        Explorers = Components.RegisterHot<Exploring>("game.explore");
    }

    // A new owner or type gives no orders the old one gave.
    private void AfterChangeNaval(EntityId entity)
    {
        Dockings.Remove(entity);
        Explorers.Remove(entity);
    }

    private void ValidateNavalComponents()
    {
        long maxTick = (long)Tick.Value + 1;
        ReadOnlySpan<EntityId> ids = Dockings.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Docking d = Dockings.Components[i];
            if (!IsShip(ids[i]) || !Motions.Has(ids[i]) || IsHeld(ids[i]) || (d.Purpose is DockPurpose.Unload or DockPurpose.UnloadTo or DockPurpose.Board && !Holders.Has(ids[i]))
                || d.Purpose is DockPurpose.None or > DockPurpose.Repair || d.Reserved != 0
                || !Map.ContainsPoint(d.WaterX, d.WaterY) || d.MoveTick < -1 || d.MoveTick > maxTick || d.Retries is < 0 or > Systems.NavalSystem.MaxRetries + 1
                || (d.Purpose is DockPurpose.Unload or DockPurpose.UnloadTo ? !Map.ContainsPoint(d.TowardX, d.TowardY) : d.TowardX != 0 || d.TowardY != 0)
                || (d.Purpose is DockPurpose.Unload or DockPurpose.UnloadTo ? d.Limit > HolderDefinition.MaxCapacity : d.Limit != 0))
            {
                throw new System.IO.InvalidDataException("Snapshot violates a world invariant: invalid docking.");
            }
        }

        ids = Explorers.Entities;
        int tiles = Map.Width * Map.Height;
        for (int i = 0; i < ids.Length; i++)
        {
            Exploring x = Explorers.Components[i];
            if (!Motions.Has(ids[i]) || IsHeld(ids[i]) || TypeOf(ids[i]).Traits.Category != EntityCategory.Unit || x.GoalTile < -1 || x.GoalTile >= tiles
                || x.NextTick < 0 || x.NextTick > maxTick + Systems.NavalSystem.ExploreIntervalTicks)
            {
                throw new System.IO.InvalidDataException("Snapshot violates a world invariant: invalid explorer.");
            }
        }
    }
}
