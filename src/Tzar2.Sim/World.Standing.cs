using System;
using System.Collections.Generic;
using Tzar2.Core.Ids;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;

namespace Tzar2.Sim;

/// <content>
/// Where units stand still (WORLD-019: a worker's place next to what it works at is free only where nobody stands). A
/// cache of the state, not state: built on first use in a tick from the units' motions and positions, so every caller in
/// the tick sees the same; a restored world is a new object.
/// </content>
public sealed partial class World
{
    private readonly Dictionary<int, int> _standing = [];
    private int _standingTick = -1;

    /// <summary>How many units stand still (not walking, not inside a holder) on tile (<paramref name="tileX"/>, <paramref name="tileY"/>) this tick.</summary>
    internal int StandingAt(int tileX, int tileY)
    {
        if (_standingTick != Tick.Value)
        {
            _standingTick = Tick.Value;
            _standing.Clear();
            ReadOnlySpan<EntityId> units = Motions.Entities;
            Span<Motion> motions = Motions.Components;
            for (int i = 0; i < units.Length; i++)
            {
                if (motions[i].State == MotionState.Moving || IsHeld(units[i]))
                {
                    continue;
                }

                Position at = Positions.Get(units[i]);
                int key = ((at.Y / MapState.UnitsPerTile) * Map.Width) + (at.X / MapState.UnitsPerTile);
                _standing[key] = _standing.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }

        return _standing.TryGetValue((tileY * Map.Width) + tileX, out int count) ? count : 0;
    }
}
