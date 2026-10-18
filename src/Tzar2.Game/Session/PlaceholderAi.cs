using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Systems;

namespace Tzar2.Game.Session;

/// <summary>
/// ⚠ PLACEHOLDER computer player (ADR 0070) so that a skirmish is playable before the legacy AI (stream B, TDD §16.1)
/// is merged. It is <b>not</b> the original AI: every few seconds idle peasants harvest the nearest resource, a house is
/// built when the population is nearly full, a barracks once, the castle trains peasants and the barracks soldiers, and
/// once the army is big enough it move-advances on the nearest enemy building. Deterministic: it reads only the world
/// and decides in entity order.
/// </summary>
public sealed class PlaceholderAi : IPlayerController
{
    private readonly Difficulty _difficulty;
    private readonly List<EntityId> _idle = [];
    private readonly List<EntityId> _army = [];
    private readonly List<EntityId> _woodcutters = [];
    private int _harvestRotation;

    /// <summary>Creates the controller.</summary>
    public PlaceholderAi(Difficulty difficulty = Difficulty.Normal) => _difficulty = difficulty;

    /// <summary>Ticks between two decisions.</summary>
    public int IntervalTicks => _difficulty switch
    {
        Difficulty.Easy => 80,
        Difficulty.Hard => 20,
        _ => 40,
    };

    /// <summary>Soldiers gathered before an attack.</summary>
    public int AttackArmySize => _difficulty switch
    {
        Difficulty.Easy => 12,
        Difficulty.Hard => 6,
        _ => 8,
    };

    /// <summary>Ticks before the first attack (grace period).</summary>
    public int GraceTicks => _difficulty switch
    {
        Difficulty.Easy => 20 * 60 * 8,
        Difficulty.Hard => 20 * 60 * 3,
        _ => 20 * 60 * 5,
    };

    /// <inheritdoc />
    public void Think(World world, byte player, CommandList batch)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(batch);
        if ((world.Tick.Value + (player * 7)) % IntervalTicks != 0 || !world.HasPlayer(player))
        {
            return;
        }

        Ruleset rules = world.Ruleset;
        _idle.Clear();
        _army.Clear();
        _woodcutters.Clear();
        int soldiers = 0, farmers = 0;
        EntityId castle = EntityId.None, barracks = EntityId.None, site = EntityId.None;
        int peasants = 0, houses = 0, fields = 0;
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        Span<Identity> identities = world.Identities.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            if (identities[i].Owner != player)
            {
                continue;
            }

            EntityId id = ids[i];
            EntityTypeDefinition type = rules.EntityTypes[identities[i].TypeId];
            string cls = type.Traits.LegacyClass;
            bool building = world.Constructions.Has(id);
            if (building)
            {
                site = id;
            }

            switch (cls)
            {
                case "castle" when !building:
                    castle = id;
                    break;
                case "barrack":
                    barracks = id;
                    break;
                case "house":
                    houses++;
                    break;
                case "field":
                    fields++;
                    break;
            }

            if (type.Traits.Harvester is not null)
            {
                peasants++;
                int w = world.Workers.IndexOf(id);
                if (w >= 0 && world.Workers.At(w).Task == WorkerTask.None && !Moving(world, id))
                {
                    _idle.Add(id);
                }
                else if (w >= 0 && world.Workers.At(w).Task == WorkerTask.Harvest)
                {
                    string res = Name(rules, world.Workers.At(w).NodeResource);
                    farmers += res == "food" ? 1 : 0;
                    if (res == "wood")
                    {
                        _woodcutters.Add(id);
                    }
                }
            }
            else if (type.IsMobile && type.Traits.Armament is not null)
            {
                // Every soldier not fighting counts toward the attack; only the ones standing get the order (the others
                // are already on their way).
                int c = world.Combats.IndexOf(id);
                if (c < 0 || world.Combats.At(c).Target == 0)
                {
                    soldiers++;
                    if (!Moving(world, id))
                    {
                        _army.Add(id);
                    }
                }
            }
        }

        if (castle.IsNone)
        {
            return;
        }

        PlayerState stock = world.Players[player]; // a copy: controllers only read the world
        string civ = rules.EntityTypes[world.Identities.Get(castle).TypeId].Traits.Civilization;
        Position home = world.Positions.Get(castle);

        // Idle peasants: help a construction site, else harvest (rotate wood, gold, stone, wood...).
        foreach (EntityId peasant in _idle)
        {
            if (!site.IsNone)
            {
                batch.Add(CommandKind.Build, [peasant], target: site);
                continue;
            }

            string[] order = fields > 0 ? ["food", "wood", "gold", "food", "wood", "stone"] : ["wood", "gold", "wood", "stone"];
            int resource = rules.ResourceId(order[_harvestRotation++ % order.Length]);
            EntityId node = NearestNode(world, world.Positions.Get(peasant), resource, player);
            if (!node.IsNone)
            {
                batch.Add(CommandKind.Harvest, [peasant], target: node);
            }
        }

        // Food shortage with fields: move woodcutters to the fields, one per field at most.
        int foodId = rules.ResourceId("food");
        if (foodId >= 0 && fields > farmers && stock.Resources[foodId] < 150)
        {
            for (int i = 0; i < Math.Min(_woodcutters.Count, Math.Min(2, fields - farmers)); i++)
            {
                EntityId field = NearestNode(world, world.Positions.Get(_woodcutters[i]), foodId, player);
                if (!field.IsNone)
                {
                    batch.Add(CommandKind.Harvest, [_woodcutters[i]], target: field);
                }
            }
        }

        // Houses when the population is nearly full, one barracks, then more peasants and soldiers.
        EntityId builder = _idle.Count > 0 ? _idle[0] : AnyWorker(world, player);
        if (site.IsNone && !builder.IsNone)
        {
            if (stock.Population + 2 >= stock.PopulationCap && stock.PopulationCap < stock.PopulationLimit && houses < 20)
            {
                TryBuild(world, batch, builder, rules.EntityTypeId(civ + ".house"), home, player);
            }
            else if (fields < 3 && stock.Resources[rules.ResourceId("food")] < 200)
            {
                int field = rules.EntityTypeId(civ + ".field");
                TryBuild(world, batch, builder, field >= 0 ? field : rules.EntityTypeId("comm.field"), home, player);
            }
            else if (barracks.IsNone)
            {
                TryBuild(world, batch, builder, rules.EntityTypeId(civ + ".barrack"), home, player);
            }
        }

        int peon = rules.EntityTypeId(civ + ".peon");
        if (peasants < 12 && peon >= 0 && Affordable(rules.EntityTypes[peon], in stock) && world.Productions.IndexOf(castle) >= 0
            && world.Productions.Get(castle).Count == 0)
        {
            batch.Add(CommandKind.Train, [castle], arg0: peon, arg1: 1);
        }

        if (!barracks.IsNone && !world.Constructions.Has(barracks) && world.Productions.IndexOf(barracks) >= 0
            && world.Productions.Get(barracks).Count < 2)
        {
            ProducerDefinition? producer = world.TypeOf(barracks).Traits.Producer;
            if (producer is not null && producer.Produces.Length > 0)
            {
                ushort pick = producer.Produces[(world.Tick.Value / IntervalTicks) % Math.Min(3, producer.Produces.Length)];
                if (Affordable(rules.EntityTypes[pick], in stock) && PlacementQueries.RequirementsMet(world, player, pick))
                {
                    batch.Add(CommandKind.Train, [barracks], arg0: pick, arg1: 1);
                }
            }
        }

        // Attack: move-advance (Ctrl) on the nearest enemy building.
        if (soldiers >= AttackArmySize && _army.Count > 0 && world.Tick.Value >= GraceTicks)
        {
            EntityId enemy = NearestEnemyStructure(world, player, home);
            if (!enemy.IsNone)
            {
                Position at = world.Positions.Get(enemy);
                batch.Add(CommandKind.Move, _army.ToArray(), at.X, at.Y, flags: CommandFlags.Ctrl);
            }
        }
    }

    private static bool Moving(World world, EntityId id)
    {
        int m = world.Motions.IndexOf(id);
        return m >= 0 && (world.Motions.At(m).VelocityX != 0 || world.Motions.At(m).VelocityY != 0 || world.Motions.At(m).State != MotionState.Idle);
    }

    private static string Name(Ruleset rules, byte resource)
        => resource < rules.ResourceTypes.Count ? rules.ResourceTypes[resource] : string.Empty;

    private static bool Affordable(EntityTypeDefinition type, in PlayerState stock)
    {
        for (int r = 0; r < type.Traits.Cost.Length; r++)
        {
            if (stock.Resources[r] < type.Traits.Cost[r])
            {
                return false;
            }
        }

        return true;
    }

    private static EntityId AnyWorker(World world, byte player)
    {
        ReadOnlySpan<EntityId> ids = world.Workers.Entities;
        Span<Worker> workers = world.Workers.Components;
        for (int i = 0; i < ids.Length; i++)
        {
            if (world.Identities.Get(ids[i]).Owner == player && workers[i].Task == WorkerTask.Harvest)
            {
                return ids[i];
            }
        }

        return EntityId.None;
    }

    private static void TryBuild(World world, CommandList batch, EntityId builder, int typeId, Position near, byte player)
    {
        if (typeId < 0)
        {
            return;
        }

        EntityTypeDefinition type = world.Ruleset.EntityTypes[typeId];
        PlayerState stock = world.Players[player];
        if (!Affordable(type, in stock) || !PlacementQueries.RequirementsMet(world, player, (ushort)typeId))
        {
            return;
        }

        int cx = near.X / MapData.UnitsPerTile, cy = near.Y / MapData.UnitsPerTile;
        for (int r = 6; r < 24; r += 2)
        {
            for (int step = 0; step < 16; step++)
            {
                // A ring of candidates in a fixed order.
                int dx = step < 4 ? -r + (step * r / 2) : step < 8 ? r : step < 12 ? r - ((step - 8) * r / 2) : -r;
                int dy = step < 4 ? -r : step < 8 ? -r + ((step - 4) * r / 2) : step < 12 ? r : r - ((step - 12) * r / 2);
                int tx = cx + dx, ty = cy + dy;
                if (Surrounded(world, type, tx, ty))
                {
                    batch.Add(CommandKind.Build, [builder], tx * MapData.UnitsPerTile, ty * MapData.UnitsPerTile, arg0: typeId);
                    return;
                }
            }
        }
    }

    // Legal and with a free tile ring around, so bases do not wall themselves in.
    private static bool Surrounded(World world, EntityTypeDefinition type, int tx, int ty)
    {
        for (int y = ty - 1; y <= ty + type.FootprintHeight; y++)
        {
            for (int x = tx - 1; x <= tx + type.FootprintWidth; x++)
            {
                if (!PlacementQueries.IsBuildableTile(world, x, y))
                {
                    return false;
                }
            }
        }

        return PlacementQueries.CanPlace(world, type.Id, tx, ty);
    }

    private static EntityId NearestNode(World world, Position from, int resource, byte player)
    {
        EntityId best = EntityId.None;
        long bestD = long.MaxValue;
        ReadOnlySpan<EntityId> ids = world.ResourceStocks.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            ResourceNodeDefinition? node = world.TypeOf(ids[i]).Traits.ResourceNode;
            byte owner = world.Identities.Get(ids[i]).Owner;
            if (node is null || node.Resource != resource || (owner != MapObject.Nature && owner != player) || world.Constructions.Has(ids[i]))
            {
                continue;
            }

            Position p = world.Positions.Get(ids[i]);
            long dx = p.X - from.X, dy = p.Y - from.Y, d = (dx * dx) + (dy * dy);
            if (d < bestD)
            {
                bestD = d;
                best = ids[i];
            }
        }

        return best;
    }

    private static EntityId NearestEnemyStructure(World world, byte player, Position from)
    {
        EntityId best = EntityId.None;
        long bestD = long.MaxValue;
        ReadOnlySpan<EntityId> ids = world.Footprints.Entities;
        for (int i = 0; i < ids.Length; i++)
        {
            Identity identity = world.Identities.Get(ids[i]);
            if (identity.Owner == player || identity.Owner == MapObject.Nature)
            {
                continue;
            }

            Position p = world.Positions.Get(ids[i]);
            long dx = p.X - from.X, dy = p.Y - from.Y, d = (dx * dx) + (dy * dy);
            if (d < bestD)
            {
                bestD = d;
                best = ids[i];
            }
        }

        return best;
    }
}
