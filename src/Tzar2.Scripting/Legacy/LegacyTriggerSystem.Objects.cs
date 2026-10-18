using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;

namespace Tzar2.Scripting.Legacy;

public sealed partial class LegacyTriggerSystem
{
    private const int Tile = MapState.UnitsPerTile;

    private readonly List<EntityId> _scratch = [];

    /// <summary>A resolved area in world units: [X0, X1) × [Y0, Y1); empty when the anchoring object is gone.</summary>
    private readonly record struct WorldRect(int X0, int Y0, int X1, int Y1)
    {
        public bool IsEmpty => X1 <= X0 || Y1 <= Y0;

        public int CenterX => X0 + ((X1 - X0) / 2);

        public int CenterY => Y0 + ((Y1 - Y0) / 2);

        public bool Contains(Position p) => p.X >= X0 && p.X < X1 && p.Y >= Y0 && p.Y < Y1;
    }

    private WorldRect Resolve(AreaSpec area)
    {
        switch (area)
        {
            case RectArea r:
            {
                int left = Math.Clamp(Math.Min(r.Left, r.Right), 0, _world.Map.Width - 1);
                int right = Math.Clamp(Math.Max(r.Left, r.Right), 0, _world.Map.Width - 1);
                int top = Math.Clamp(Math.Min(r.Top, r.Bottom), 0, _world.Map.Height - 1);
                int bottom = Math.Clamp(Math.Max(r.Top, r.Bottom), 0, _world.Map.Height - 1);
                return new WorldRect(left * Tile, top * Tile, (right + 1) * Tile, (bottom + 1) * Tile);
            }

            case ObjectArea o when MapEntity(o.ObjectIndex) is var e && e != EntityId.None && _world.Positions.Has(e):
            {
                Position p = _world.Positions.Get(e);
                int radius = Math.Clamp(o.RadiusTiles ?? _options.ObjectAreaRadiusTiles, 0, 64);
                int tx = p.X / Tile;
                int ty = p.Y / Tile;
                return new WorldRect(Math.Max(0, tx - radius) * Tile, Math.Max(0, ty - radius) * Tile, Math.Min(_world.Map.Width, tx + radius + 1) * Tile, Math.Min(_world.Map.Height, ty + radius + 1) * Tile);
            }

            default:
                return default;
        }
    }

    /// <summary>The live entity of a map object, or None (hidden, dead, or out of range).</summary>
    private EntityId MapEntity(int index)
    {
        if ((uint)index >= (uint)_state.MapEntities.Length || _state.MapEntities[index] == 0)
        {
            return EntityId.None;
        }

        EntityId e = EntityId.FromValue(_state.MapEntities[index]);
        return _world.Entities.IsAlive(e) ? e : EntityId.None;
    }

    // Ruleset type of a typed spec (resolved once at attach), or -1.
    private int TypeOf(TypedObjects spec)
    {
        int id = _world.Ruleset.EntityTypeId(spec.TypeKey);
        return id;
    }

    /// <summary>The live entities a spec names, ascending by entity (named: in list order; typed: every match).</summary>
    private List<EntityId> Collect(ObjectSpec spec, List<EntityId> into)
    {
        into.Clear();
        switch (spec)
        {
            case NamedObjects named:
                foreach (int index in named.ObjectIndices)
                {
                    _steps++;
                    if (MapEntity(index) is var e && e != EntityId.None)
                    {
                        into.Add(e);
                    }
                }

                break;
            case TypedObjects typed:
            {
                int type = TypeOf(typed);
                if (type < 0)
                {
                    break;
                }

                ReadOnlySpan<EntityId> ids = _world.Identities.Entities;
                ReadOnlySpan<Identity> identities = _world.Identities.Components;
                for (int i = 0; i < ids.Length && !OverCap; i++)
                {
                    _steps++;
                    if (identities[i].TypeId == type && InMask(typed.Players, identities[i].Owner))
                    {
                        into.Add(ids[i]);
                    }
                }

                break;
            }

            case CategoryObjects category:
            {
                ReadOnlySpan<EntityId> ids = _world.Identities.Entities;
                ReadOnlySpan<Identity> identities = _world.Identities.Components;
                for (int i = 0; i < ids.Length && !OverCap; i++)
                {
                    _steps++;
                    if (InMask(category.Players, identities[i].Owner) && InCategory(category.Category, _world.Ruleset.EntityTypes[identities[i].TypeId]))
                    {
                        into.Add(ids[i]);
                    }
                }

                break;
            }
        }

        return into;
    }

    /// <summary>
    /// The editor's type-filter categories (ADR 0120): units, military units (armed units that neither gather nor
    /// build), buildings, "major objects" = units and buildings (UNVERIFIED, UNK-75), any object.
    /// </summary>
    public static bool InCategory(ObjectCategory category, Content.Compiled.EntityTypeDefinition type)
    {
        Content.Compiled.EntityTraits t = type.Traits;
        bool unit = t.Category == Content.Compiled.EntityCategory.Unit;
        bool building = t.Category == Content.Compiled.EntityCategory.Building;
        return category switch
        {
            ObjectCategory.Units => unit,
            ObjectCategory.MilitaryUnits => unit && t.Armament is not null && t.Harvester is null && !t.IsBuilder,
            ObjectCategory.Buildings => building,
            ObjectCategory.MajorObjects => unit || building,
            _ => true,
        };
    }

    // Legacy mask bit of an owner: kingdoms 1-8 are bits 0-7, Nature is legacy owner 10 (bit 9; ADR 0120).
    private static bool InMask(ushort legacyMask, int owner)
        => owner == Content.Compiled.MapObject.Nature ? (legacyMask & (1 << 9)) != 0 : owner < 8 && (legacyMask & (1 << owner)) != 0;

    /// <summary>
    /// A legacy owner as a sim owner: kingdoms 1-8 are slots 0-7; 0 (none), 9 (Rescue) and 10 (Nature) are Nature
    /// (UNVERIFIED: the rebuild has no Rescue kingdom yet, UNK-61). -1 when invalid.
    /// </summary>
    private int OwnerOf(int legacy) => legacy is 0 or 9 or 10 ? Content.Compiled.MapObject.Nature : _world.HasPlayer(legacy - 1) ? legacy - 1 : -1;

    /// <summary>
    /// Whether a spec holds for a per-object test. Named: All = every listed object exists and passes (a missing one
    /// fails), Any = one does. Typed: per kingdom of the mask, the number of passing objects compared with the count;
    /// All = every kingdom of the mask satisfies it, Any = one does (UNVERIFIED reading of "All/Any" with a type,
    /// UNK-54).
    /// </summary>
    private bool Holds(ObjectSpec spec, Func<EntityId, bool> test)
    {
        switch (spec)
        {
            case NamedObjects named:
            {
                if (named.ObjectIndices.Length == 0)
                {
                    return false;
                }

                bool all = named.Quantifier == Quantifier.All;
                foreach (int index in named.ObjectIndices)
                {
                    _steps++;
                    EntityId e = MapEntity(index);
                    bool pass = e != EntityId.None && test(e);
                    if (all && !pass)
                    {
                        return false;
                    }

                    if (!all && pass)
                    {
                        return true;
                    }
                }

                return all;
            }

            case TypedObjects typed:
                return Compare(typed.Players, typed.Comparison, typed.Count, typed.Quantifier, Counts(typed, test));
            case CategoryObjects category:
                return Compare(category.Players, category.Comparison, category.Count, category.Quantifier, Counts(category, test));

            default:
                return false;
        }
    }

    // Passing objects per legacy owner bit (kingdoms 0-7, Nature 9).
    private int[] Counts(ObjectSpec spec, Func<EntityId, bool> test)
    {
        Array.Clear(_counts);
        foreach (EntityId e in Collect(spec, _scratch))
        {
            if (++_steps > _options.HardStepCapPerTick)
            {
                break;
            }

            if (test(e))
            {
                byte owner = _world.Identities.Get(e).Owner;
                _counts[owner == Content.Compiled.MapObject.Nature ? 9 : owner]++;
            }
        }

        return _counts;
    }

    // Shared scratch of Counts: Holds is not re-entrant (no test function calls Holds).
    private readonly int[] _counts = new int[16];

    // Per kingdom of the mask: count against the spec's comparison, combined by the quantifier.
    private static bool Compare(ushort players, CountComparison comparison, int count, Quantifier quantifier, ReadOnlySpan<int> counts)
    {
        bool all = quantifier == Quantifier.All;
        bool any = false;
        for (int p = 0; p < 16; p++)
        {
            if ((players & (1 << p)) == 0)
            {
                continue;
            }

            int n = p < counts.Length ? counts[p] : 0;
            bool pass = comparison == CountComparison.AtLeast ? n >= count : n <= count;
            if (all && !pass)
            {
                return false;
            }

            any |= pass;
        }

        return all ? players != 0 : any;
    }

    private static bool Compare(TypedObjects typed, ReadOnlySpan<int> counts)
        => Compare(typed.Players, typed.Comparison, typed.Count, typed.Quantifier, counts);
}
