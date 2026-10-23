using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Components;
using Tzar2.Sim.Kingdoms;

namespace Tzar2.AI.Runtime;

/// <summary>A resource node the AI may send gatherers to.</summary>
/// <param name="Id">The node.</param>
/// <param name="X">Center X.</param>
/// <param name="Y">Center Y.</param>
/// <param name="Owner">Owner slot (Nature for trees and rocks, a kingdom for its fields).</param>
/// <param name="Resource">Resource type id.</param>
public readonly record struct AiNode(EntityId Id, int X, int Y, byte Owner, byte Resource);

/// <summary>What one player has, as the AI sees it in the current tick.</summary>
public sealed class AiCensusPlayer
{
    private readonly int[] _completed;
    private readonly int[] _inProgress;

    internal AiCensusPlayer(int classCount)
    {
        _completed = new int[classCount];
        _inProgress = new int[classCount];
    }

    /// <summary>Completed buildings and live units per class.</summary>
    public ReadOnlySpan<int> Completed => _completed;

    /// <summary>Construction sites and queued units per class (at the start of the tick; see <c>AiExecutorContext.InProgress</c>).</summary>
    public ReadOnlySpan<int> InProgress => _inProgress;

    internal int[] CompletedWritable => _completed;

    internal int[] InProgressWritable => _inProgress;

    /// <summary>Live units (category Unit, peasants included): the base of <c>units</c> and of percentages.</summary>
    public int Units { get; internal set; }

    /// <summary>Population of the units queued in producers.</summary>
    public int QueuedPopulation { get; internal set; }

    /// <summary>Peasants and other workers (entities with worker state), ascending ids.</summary>
    public List<EntityId> Workers { get; } = [];

    /// <summary>Soldiers (<see cref="AiRulesetModel.IsArmy"/>), ascending ids.</summary>
    public List<EntityId> Army { get; } = [];

    /// <summary>Ships (fishing boats, transports, warships; not in <see cref="Workers"/> or <see cref="Army"/>), ascending ids.</summary>
    public List<EntityId> Ships { get; } = [];

    /// <summary>Completed buildings with a production queue, ascending ids.</summary>
    public List<EntityId> Producers { get; } = [];

    /// <summary>Construction sites, ascending ids.</summary>
    public List<EntityId> Sites { get; } = [];

    /// <summary>Completed buildings, ascending ids.</summary>
    public List<EntityId> Buildings { get; } = [];

    /// <summary>Whether the player has anything at all.</summary>
    public bool Exists => Workers.Count + Army.Count + Ships.Count + Buildings.Count + Sites.Count > 0 || Units > 0;

    /// <summary>The town center: the completed drop-off accepting the most resources (the castle), else the first
    /// building, else the first unit; the map center when the player has nothing.</summary>
    public (int X, int Y) Town { get; internal set; }

    /// <summary>Whether <see cref="Town"/> is a building.</summary>
    public bool HasTownBuilding { get; internal set; }

    /// <summary>Bounding box of the player's buildings and sites in tiles (inclusive), or empty (X0 &gt; X1).</summary>
    public (int X0, int Y0, int X1, int Y1) TownBox { get; internal set; }

    internal void Clear()
    {
        Array.Clear(_completed);
        Array.Clear(_inProgress);
        Units = 0;
        QueuedPopulation = 0;
        Workers.Clear();
        Army.Clear();
        Ships.Clear();
        Producers.Clear();
        Sites.Clear();
        Buildings.Clear();
        HasTownBuilding = false;
        TownBox = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
    }
}

/// <summary>
/// One deterministic pass over the world per tick that any AI acts in (shared by all AI players; allocation-free in
/// steady state): counts per player and class, the players' workers, soldiers, producers and sites, the harvestable
/// nodes, and how many workers target each node and site. Iteration is in ascending entity order (REQ-SIM-07).
/// </summary>
public sealed class AiCensus
{
    private readonly AiCensusPlayer[] _players;
    private readonly List<AiNode> _nodes = [];
    private readonly int[] _bestTown;
    private int[] _workersOnTarget = [];
    private int _tick = -1;

    internal AiCensus(AiRulesetModel model, int playerCount)
    {
        Model = model;
        _players = new AiCensusPlayer[playerCount];
        _bestTown = new int[playerCount];
        for (int p = 0; p < playerCount; p++)
        {
            _players[p] = new AiCensusPlayer(model.ClassCount);
        }
    }

    /// <summary>The ruleset model.</summary>
    public AiRulesetModel Model { get; }

    /// <summary>Number of player slots.</summary>
    public int PlayerCount => _players.Length;

    /// <summary>A player's census.</summary>
    public AiCensusPlayer Player(int slot) => _players[slot];

    /// <summary>Harvestable resource nodes (stock left, completed), ascending ids.</summary>
    public ReadOnlySpan<AiNode> Nodes => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nodes);

    /// <summary>Workers whose current task (harvest or build) targets the entity at <paramref name="index"/>.</summary>
    public int WorkersOn(int index) => (uint)index < (uint)_workersOnTarget.Length ? _workersOnTarget[index] : 0;

    internal void AddWorkerOn(int index)
    {
        if ((uint)index < (uint)_workersOnTarget.Length)
        {
            _workersOnTarget[index]++;
        }
    }

    /// <summary>
    /// Whether <paramref name="other"/> is an enemy of <paramref name="player"/>: another active, undefeated slot towards
    /// which <paramref name="player"/>'s stance is <see cref="Stance.Enemy"/> (GAME-13). Neutral kingdoms and allies are
    /// never counted by <c>enemy</c> conditions nor attacked by waves.
    /// </summary>
    public static bool IsEnemy(World world, int player, int other)
    {
        ArgumentNullException.ThrowIfNull(world);
        return IsLive(world, player, other) && world.Diplomacy.StanceOf(player, other) == Stance.Enemy;
    }

    /// <summary>
    /// Whether soldiers of <paramref name="other"/> near the town are a threat the AI defends against: any other active,
    /// undefeated slot it may attack military units of (enemies and neutrals, never allies; manual ch. 5 §8).
    /// </summary>
    public static bool IsHostileArmy(World world, int player, int other)
    {
        ArgumentNullException.ThrowIfNull(world);
        return IsLive(world, player, other) && world.Diplomacy.MayAttack(player, other, targetIsMilitary: true);
    }

    private static bool IsLive(World world, int player, int other)
        => other != player && world.HasPlayer(other) && (world.PlayersView[other].Flags & PlayerFlags.Active) != 0
            && (world.PlayersView[other].Flags & PlayerFlags.Defeated) == 0;

    /// <summary>Recomputes the census unless it is already from this tick.</summary>
    internal void Update(World world)
    {
        if (_tick == world.Tick.Value)
        {
            return;
        }

        _tick = world.Tick.Value;
        foreach (AiCensusPlayer player in _players)
        {
            player.Clear();
        }

        _nodes.Clear();
        int highWater = world.Entities.HighWater;
        if (_workersOnTarget.Length < highWater)
        {
            _workersOnTarget = new int[Math.Max(highWater, _workersOnTarget.Length * 2)];
        }
        else
        {
            Array.Clear(_workersOnTarget, 0, highWater);
        }

        IReadOnlyList<EntityTypeDefinition> types = world.Ruleset.EntityTypes;
        ReadOnlySpan<EntityId> ids = world.Identities.Entities;
        ReadOnlySpan<Identity> identities = world.Identities.View;
        int[] bestTown = _bestTown;
        Array.Fill(bestTown, -1);
        for (int i = 0; i < ids.Length; i++)
        {
            EntityId e = ids[i];
            Identity identity = identities[i];
            if (identity.Owner >= _players.Length)
            {
                continue;
            }

            AiCensusPlayer player = _players[identity.Owner];
            EntityTypeDefinition type = types[identity.TypeId];
            int cls = Model.ClassOf(identity.TypeId);
            if (type.HasFootprint)
            {
                Footprint f = world.Footprints.Get(e);
                (int x0, int y0, int x1, int y1) = player.TownBox;
                player.TownBox = (Math.Min(x0, f.TileX), Math.Min(y0, f.TileY), Math.Max(x1, f.TileX + type.FootprintWidth - 1), Math.Max(y1, f.TileY + type.FootprintHeight - 1));
                if (world.Constructions.Has(e))
                {
                    player.Sites.Add(e);
                    if (cls >= 0)
                    {
                        player.InProgressWritable[cls]++;
                    }

                    continue;
                }

                player.Buildings.Add(e);
                if (cls >= 0)
                {
                    player.CompletedWritable[cls]++;
                }

                int score = BitCount(type.Traits.DropOffMask) + 1;
                if (score > bestTown[identity.Owner])
                {
                    bestTown[identity.Owner] = score;
                    Position p = world.Positions.Get(e);
                    player.Town = (p.X, p.Y);
                    player.HasTownBuilding = true;
                }

                int q = world.Productions.IndexOf(e);
                if (q >= 0)
                {
                    player.Producers.Add(e);
                    ref readonly Production production = ref world.Productions.At(q);
                    for (int k = 0; k < production.Count; k++)
                    {
                        ushort item = production.Queue[k].Item;
                        int runCount = production.Queue[k].Count;
                        if (QueueItems.IsResearch(item) || QueueItems.IsUpgrade(item))
                        {
                            continue; // research and upgrades: no unit, no population (GAME-06)
                        }

                        int unit = QueueItems.Id(item);
                        if ((uint)unit >= (uint)types.Count)
                        {
                            continue;
                        }

                        int itemClass = Model.ClassOf(unit);
                        if (itemClass >= 0)
                        {
                            player.InProgressWritable[itemClass] += runCount;
                        }

                        player.QueuedPopulation += types[unit].Traits.PopulationCost * runCount;
                    }
                }

                continue;
            }

            if (type.Traits.Category != EntityCategory.Unit)
            {
                continue;
            }

            player.Units++;
            if (cls >= 0)
            {
                player.CompletedWritable[cls]++;
            }

            if (bestTown[identity.Owner] < 0)
            {
                bestTown[identity.Owner] = 0;
                Position p = world.Positions.Get(e);
                player.Town = (p.X, p.Y);
            }

            if (type.IsMobile && type.Locomotor == Locomotor.Ship)
            {
                player.Ships.Add(e); // the naval executor's (ADR 0144): boats do not gather on land, ships do not march
            }
            else if (world.Workers.Has(e))
            {
                player.Workers.Add(e);
            }
            else if (Model.IsArmy(identity.TypeId))
            {
                player.Army.Add(e);
            }
        }

        for (int p = 0; p < _players.Length; p++)
        {
            if (bestTown[p] < 0)
            {
                _players[p].Town = (world.Map.WorldWidth / 2, world.Map.WorldHeight / 2);
            }
        }

        ReadOnlySpan<EntityId> nodes = world.ResourceStocks.Entities;
        ReadOnlySpan<ResourceStock> stocks = world.ResourceStocks.View;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (stocks[i].Amount == 0 || world.Constructions.Has(nodes[i]) || Tzar2.Sim.Systems.Shore.IsOnWater(world, nodes[i]))
            {
                continue; // fish patches are for boats (the naval executor), not for the land economy
            }

            Identity identity = world.Identities.Get(nodes[i]);
            ResourceNodeDefinition node = types[identity.TypeId].Traits.ResourceNode!;
            Position p = world.Positions.Get(nodes[i]);
            _nodes.Add(new AiNode(nodes[i], p.X, p.Y, identity.Owner, (byte)node.Resource));
        }

        ReadOnlySpan<EntityId> workers = world.Workers.Entities;
        ReadOnlySpan<Worker> states = world.Workers.View;
        for (int i = 0; i < workers.Length; i++)
        {
            Worker w = states[i];
            if (w.Task != WorkerTask.None && w.Target != 0)
            {
                EntityId target = EntityId.FromValue(w.Target);
                if (world.Entities.IsAlive(target))
                {
                    AddWorkerOn(target.Index);
                }
            }
        }
    }

    /// <summary>Completed count of a class over the enemies of <paramref name="player"/>.</summary>
    public int EnemyCompleted(World world, int player, int classId)
    {
        int n = 0;
        for (int q = 0; q < _players.Length; q++)
        {
            if (IsEnemy(world, player, q))
            {
                n += _players[q].CompletedWritable[classId];
            }
        }

        return n;
    }

    /// <summary>Units of the enemies of <paramref name="player"/>.</summary>
    public int EnemyUnits(World world, int player)
    {
        int n = 0;
        for (int q = 0; q < _players.Length; q++)
        {
            if (IsEnemy(world, player, q))
            {
                n += _players[q].Units;
            }
        }

        return n;
    }

    private static int BitCount(int mask) => System.Numerics.BitOperations.PopCount((uint)mask);
}
