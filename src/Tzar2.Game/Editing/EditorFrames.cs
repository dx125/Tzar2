using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Editing;
using Tzar2.Core.Ids;
using Tzar2.Game.Session;
using Tzar2.Sim.Components;

namespace Tzar2.Game.Editing;

/// <summary>
/// Render frames of an editor document for the game's world renderer (REQ-EDT-01: "same renderer"; ADR 0150): every
/// placed object becomes a <see cref="FrameEntity"/> as the sim would show it at match start (structures on their
/// footprint, units at their position and facing, full health), with no fog. Frame entity <c>i</c> is object <c>i</c> of the
/// state, and its id is <c>i + 1</c>, so the renderer's picking maps back to objects.
/// </summary>
public static class EditorFrames
{
    /// <summary>Fills <paramref name="frame"/> with the objects of <paramref name="state"/> that <paramref name="visible"/> accepts.</summary>
    /// <param name="frame">The frame to fill (reused between redraws).</param>
    /// <param name="state">The document state.</param>
    /// <param name="ruleset">The ruleset (types the ruleset does not know are left out).</param>
    /// <param name="players">Kingdom slots to give stockpile rows (8).</param>
    /// <param name="visible">Layer filter (hidden layers), or null for all.</param>
    /// <returns>For each frame entity, the object id it shows.</returns>
    public static int[] Fill(GameFrame frame, DocumentState state, Ruleset ruleset, int players = 8, Func<EditorObject, bool>? visible = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ruleset);
        FrameEntity[] entities = frame.EntityBuffer(state.Objects.Count);
        var ids = new List<int>(state.Objects.Count);
        int n = 0;
        foreach (EditorObject o in state.Objects)
        {
            int typeId = ruleset.EntityTypeId(o.TypeKey);
            if (typeId < 0 || (visible is not null && !visible(o)))
            {
                continue;
            }

            EntityTypeDefinition type = ruleset.EntityTypes[typeId];
            ref FrameEntity e = ref entities[n];
            e = default;
            e.Id = EntityId.Create(n + 1, 1);
            e.TypeId = (ushort)typeId;
            e.Owner = o.Owner;
            e.Facing = o.Placement.Facing;
            e.Health = o.Info.Health ?? type.MaxHealth;
            e.MaxHealth = o.Info.MaxHealth ?? type.MaxHealth;
            e.Activity = UnitActivity.Idle;
            e.SwingMs = -1;
            e.ItemResource = -1;
            e.CarryResource = Worker.NoResource;
            e.ConstructionPermille = -1;
            e.ResourceLeft = o.Placement.Amount > 0 ? o.Placement.Amount : type.Traits.ResourceNode?.Amount ?? -1;
            if (type.HasFootprint)
            {
                (int tx, int ty, int w, int h) = Occupancy.Footprint(type, o.Placement);
                e.TileX = tx;
                e.TileY = ty;
                e.X = (tx * MapData.UnitsPerTile) + (w * MapData.UnitsPerTile / 2);
                e.Y = (ty * MapData.UnitsPerTile) + (h * MapData.UnitsPerTile / 2);
            }
            else
            {
                e.TileX = -1;
                e.TileY = -1;
                e.X = o.X;
                e.Y = o.Y;
            }

            ids.Add(o.Id);
            n++;
        }

        frame.EntityCount = n;
        frame.CorpseCount = 0;
        frame.Visibility = [];
        frame.Outcome = MatchOutcome.None;
        frame.Paused = true;
        frame.DayMinute = 720;
        frame.IsNight = false;
        if (frame.Players.Length != players)
        {
            frame.Players = new FramePlayer[players];
        }

        return ids.ToArray();
    }
}
