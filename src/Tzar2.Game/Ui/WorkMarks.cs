using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Game.Session;

namespace Tzar2.Game.Ui;

/// <summary>Marks of what a kingdom's workers work on, drawn over the world.</summary>
public static class WorkMarks
{
    /// <summary>
    /// The fish patches <paramref name="player"/>'s boats are fishing (patch 1.05: "Blue selection is drawn around the
    /// fishpatch from which your boats are fishing"; HUD-025): the work targets of its ships that harvest.
    /// </summary>
    public static HashSet<uint> FishedPatches(GameFrame frame, Ruleset rules, int player)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(rules);
        var targets = new HashSet<uint>();
        ReadOnlySpan<FrameEntity> entities = frame.Entities;
        for (int i = 0; i < entities.Length; i++)
        {
            ref readonly FrameEntity e = ref entities[i];
            if (e.Owner != player || e.WorkTarget.IsNone || e.TypeId >= rules.EntityTypes.Count)
            {
                continue;
            }

            EntityTypeDefinition type = rules.EntityTypes[e.TypeId];
            if (type.Locomotor == Locomotor.Ship && type.Traits.Harvester is not null)
            {
                targets.Add(e.WorkTarget.Value);
            }
        }

        // Only patches still in the frame (a stale target is dropped).
        var shown = new HashSet<uint>();
        for (int i = 0; i < entities.Length; i++)
        {
            if (targets.Contains(entities[i].Id.Value))
            {
                shown.Add(entities[i].Id.Value);
            }
        }

        return shown;
    }
}
