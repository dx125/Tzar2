using System;
using Tzar2.Core.Ids;

namespace Tzar2.Sim;

/// <summary>
/// Walkable footprints (fields, trait <c>WalkableFootprint</c>): they put no blockers on the map, so units cross them
/// and a farmer works on the field, but nothing may be built on them. The cover per tile is derived from the
/// footprints (not state): kept up to date as structures come and go, rebuilt after a snapshot is restored.
/// </summary>
public sealed partial class World
{
    private byte[] _walkableCover = [];

    /// <summary>Changes whenever a walkable footprint appears or goes (derived, not state): a cache key for presentations.</summary>
    public int WalkableFootprintsVersion { get; private set; }

    /// <summary>Whether tile (<paramref name="tileX"/>, <paramref name="tileY"/>) lies under a walkable footprint (a field).</summary>
    public bool IsUnderWalkableFootprint(int tileX, int tileY)
        => _walkableCover.Length != 0 && Map.ContainsTile(tileX, tileY) && _walkableCover[(tileY * Map.Width) + tileX] != 0;

    // A walkable structure appears (add) or goes: its rectangle's cover count changes.
    private void ApplyWalkable(EntityId entity, bool add)
    {
        if (_walkableCover.Length != Map.Width * Map.Height)
        {
            _walkableCover = new byte[Map.Width * Map.Height];
        }

        (int tx, int ty, int w, int h) = FootprintRect(entity);
        for (int y = ty; y < ty + h; y++)
        {
            for (int x = tx; x < tx + w; x++)
            {
                ref byte cover = ref _walkableCover[(y * Map.Width) + x];
                cover = add ? checked((byte)(cover + 1)) : checked((byte)(cover - 1));
            }
        }

        WalkableFootprintsVersion++;
    }

    // After a restore: the cover from the restored footprints.
    private void RebuildWalkableCover()
    {
        _walkableCover = [];
        ReadOnlySpan<EntityId> structures = Footprints.Entities;
        for (int i = 0; i < structures.Length; i++)
        {
            if (TypeOf(structures[i]).Traits.WalkableFootprint)
            {
                ApplyWalkable(structures[i], add: true);
            }
        }
    }
}
