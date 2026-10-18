using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;
using Tzar2.Sim.Systems;

namespace Tzar2.AI.Executors;

/// <summary>Naval tuning (ADR 0144; UNVERIFIED, docs/mechanics/ai-placeholders.md).</summary>
public sealed record NavalOptions
{
    /// <summary>Fishing boats kept when fish is near the harbor.</summary>
    public int FishingBoats { get; init; } = 2;

    /// <summary>Transports kept when an enemy lives overseas.</summary>
    public int Transports { get; init; } = 1;

    /// <summary>How long before the first wave a transport is saved for (the stock is held back from the other executors).</summary>
    public int TransportLeadMs { get; init; } = 240_000;

    /// <summary>Warships kept when an enemy lives overseas: armed ships beyond the transports (ships can be both).</summary>
    public int Warships { get; init; } = 1;

    /// <summary>Fish patches this far from the boat (tiles) are fished.</summary>
    public int FishRadiusTiles { get; init; } = 24;

    /// <summary>A wave that is not aboard after this long sails with those who are (or gives up if none are).</summary>
    public int EmbarkTimeoutMs { get; init; } = 60_000;

    /// <summary>Transports still loaded after this long are told to unload again.</summary>
    public int SailRetryMs { get; init; } = 60_000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(FishingBoats);
        ArgumentOutOfRangeException.ThrowIfNegative(Transports);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Transports, AiPlayerState.MaxTransports);
        ArgumentOutOfRangeException.ThrowIfNegative(Warships);
        ArgumentOutOfRangeException.ThrowIfLessThan(FishRadiusTiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(EmbarkTimeoutMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(SailRetryMs, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(TransportLeadMs);
    }
}

/// <summary>
/// Harbors, fishing, ships and landings (REQ-AI-02 naval manager; ADR 0144). The original's naval AI is hard-coded, so
/// this is the most plausible behaviour (UNVERIFIED):
/// <list type="bullet">
/// <item><b>When:</b> only while an enemy town cannot be reached by land from the own town (Island maps where land
/// connects everyone, and land maps, are left to the other executors), or while ships or a landing are in play.</item>
/// <item><b>Harbor:</b> one, on the coast nearest to the town.</item>
/// <item><b>Ships:</b> fishing boats when fish is near, transports and warships, one at a time.</item>
/// <item><b>Fishing:</b> idle boats fish the nearest patch.</item>
/// <item><b>Landings:</b> a wave whose target is overseas boards the transports (they come to the shore, REQ-PATH-09),
/// sails to the shore nearest the target and unloads there; the military executor takes over again on landing.</item>
/// <item><b>Warships:</b> idle armed ships attack the nearest enemy ship, else sail to the enemy's shore.</item>
/// </list>
/// </summary>
public sealed class NavalExecutor : IAiExecutor
{
    private readonly List<EntityId> _orders = [];

    /// <inheritdoc />
    public string Name => "naval";

    /// <inheritdoc />
    public void Run(AiExecutorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AiCensusPlayer me = context.Player;
        AiPlayerState state = context.State;
        ushort landType;
        if (me.Workers.Count > 0)
        {
            landType = context.World.Identities.Get(me.Workers[0]).TypeId;
        }
        else if (me.Army.Count > 0)
        {
            landType = context.World.Identities.Get(me.Army[0]).TypeId;
        }
        else
        {
            return;
        }

        int home = Shore.LandComponentAt(context.World, landType, me.Town.X, me.Town.Y);
        bool overseas = home >= 0 && AnyEnemyOverseas(context, landType, home);
        if (!overseas && me.Ships.Count == 0 && state.NavalPhase == AiNavalPhase.None)
        {
            return; // a land map: nothing to do
        }

        if (context.Settings.EconomyAi)
        {
            EntityId harbor = Harbor(context, out bool site);
            if (harbor.IsNone && !site && overseas)
            {
                BuildHarbor(context);
            }
            else if (!harbor.IsNone)
            {
                Train(context, harbor, overseas);
            }

            Fish(context);
        }

        if (context.Settings.WarAi)
        {
            Land(context, landType);
            Patrol(context, landType, overseas);
        }
    }

    /// <summary>Whether a ship of <paramref name="type"/> carries land units (a transport).</summary>
    internal static bool IsTransport(EntityTypeDefinition type) => type.Traits.Holder is not null;

    private static bool AnyEnemyOverseas(AiExecutorContext context, ushort landType, int home)
    {
        for (int q = 0; q < context.Census.PlayerCount; q++)
        {
            AiCensusPlayer enemy = context.Census.Player(q);
            if (!AiCensus.IsEnemy(context.World, context.Slot, q) || !enemy.Exists || !enemy.HasTownBuilding)
            {
                continue;
            }

            int there = Shore.LandComponentAt(context.World, landType, enemy.Town.X, enemy.Town.Y);
            if (there >= 0 && there != home)
            {
                return true;
            }
        }

        return false;
    }

    // The first completed harbor (a coast building that trains ships); whether a harbor site exists.
    private static EntityId Harbor(AiExecutorContext context, out bool site)
    {
        site = false;
        World world = context.World;
        foreach (EntityId s in context.Player.Sites)
        {
            site |= world.TypeOf(s).Traits.Placement == PlacementKind.Coast;
        }

        foreach (EntityId b in context.Player.Buildings)
        {
            EntityTypeDefinition t = world.TypeOf(b);
            if (t.Traits.Placement == PlacementKind.Coast && t.Traits.Producer is not null)
            {
                return b;
            }
        }

        return EntityId.None;
    }

    private static void BuildHarbor(AiExecutorContext context)
    {
        World world = context.World;
        string civ = world.PlayerCivilization(context.Slot);
        IReadOnlyList<EntityTypeDefinition> types = world.Ruleset.EntityTypes;
        for (int t = 0; t < types.Count; t++)
        {
            EntityTypeDefinition type = types[t];
            if (type.Traits.Placement != PlacementKind.Coast || type.Traits.Producer is null || !context.Model.IsOfCivilization((ushort)t, civ)
                || context.Model.BuildersOf(t).IsEmpty || !PlacementQueries.RequirementsMet(world, context.Slot, (ushort)t))
            {
                continue;
            }

            if (!context.CanAfford(type.Traits.Cost, 0))
            {
                context.Reserve(type.Traits.Cost); // save up: the other executors do not spend all of it
                return;
            }

            int cls = context.Model.ClassOf(t);
            if (cls >= 0 && !context.FirstAttempt(cls, 0))
            {
                return;
            }

            ConstructionExecutor.TryStart(context, (ushort)t);
            return;
        }
    }

    // One ship per run, in need order: fishing boats, transports, warships.
    private void Train(AiExecutorContext context, EntityId harbor, bool overseas)
    {
        World world = context.World;
        ProducerDefinition producer = world.TypeOf(harbor).Traits.Producer!;
        Production queue = world.Productions.Get(harbor);
        if (queue.Count >= 2)
        {
            return;
        }

        int boats = 0;
        int holders = 0;
        int armed = 0;
        foreach (EntityId ship in context.Player.Ships)
        {
            Count(world.TypeOf(ship), ref boats, ref holders, ref armed);
        }

        for (int k = 0; k < queue.Count; k++)
        {
            ushort item = queue.Queue[k];
            if (!QueueItems.IsResearch(item) && !QueueItems.IsUpgrade(item) && QueueItems.Id(item) < world.Ruleset.EntityTypes.Count)
            {
                Count(world.Ruleset.EntityTypes[QueueItems.Id(item)], ref boats, ref holders, ref armed);
            }
        }

        NavalOptions options = context.Options.Naval;
        int transports = Math.Min(holders, options.Transports);
        int warships = armed - transports; // the first ships that carry are the transports, armed or not
        bool fish = !Shore.NearestFish(world, context.Slot, FoodId(world), world.Positions.Get(harbor).X, world.Positions.Get(harbor).Y, options.FishRadiusTiles).IsNone;
        MilitaryOptions military = context.Options.Military;
        long now = (long)world.Tick.Value * world.Config.TickLengthMilliseconds;
        bool soon = now >= military.FirstWaveMs - options.TransportLeadMs;
        int want = overseas && soon && transports < options.Transports ? 1
            : fish && boats < options.FishingBoats ? 0
            : overseas && transports >= options.Transports && warships < options.Warships ? 2
            : -1;
        int wave = Math.Min(military.MaxWaveSize, military.FirstWaveSize + (military.WaveGrowth * context.State.WaveCount));
        if (want < 0)
        {
            return;
        }

        ref readonly PlayerState player = ref world.Players[context.Slot];
        int room = player.PopulationCap - player.Population - context.Player.QueuedPopulation;
        int bestType = -1;
        long bestScore = long.MinValue;
        foreach (ushort t in producer.Produces)
        {
            EntityTypeDefinition type = world.Ruleset.EntityTypes[t];
            if (type.Locomotor != Locomotor.Ship || Kind(type, want) != want || !PlacementQueries.RequirementsMet(world, context.Slot, t)
                || type.Traits.PopulationCost > room)
            {
                continue;
            }

            // Transports: the cheapest (a big wave makes more trips; ties: more room); warships: the hardest hitters; boats: the first.
            long price = 0;
            foreach (int c in type.Traits.Cost)
            {
                price += Math.Max(0, c);
            }

            long score = want == 1 ? (-price * 1024) + Math.Min(type.Traits.Holder!.Capacity, wave)
                : want == 2 ? type.Traits.Armament!.Attack
                : -t;
            if (score > bestScore)
            {
                bestScore = score;
                bestType = t;
            }
        }

        if (bestType < 0)
        {
            return;
        }

        ImmutableArray<int> cost = world.Ruleset.EntityTypes[bestType].Traits.Cost;
        if (!context.CanAfford(cost, 0))
        {
            if ((want == 1 && holders == 0) || (want == 0 && boats == 0))
            {
                context.Reserve(cost); // the first transport and boat are saved up for across runs; the rest come from spare stock
            }

            return;
        }

        _orders.Clear();
        _orders.Add(harbor);
        if (context.Issue(CommandKind.Train, CollectionsMarshal.AsSpan(_orders), arg0: bestType, arg1: 1))
        {
            context.Spend(cost);
        }
    }

    // What a type is trained as: 0 fishing boat, 1 transport (carries), 2 warship (armed), -1 nothing naval.
    private static int Kind(EntityTypeDefinition type, int want)
        => type.Traits.Harvester is not null ? 0
            : want == 1 ? (IsTransport(type) ? 1 : -1)
            : want == 2 ? (type.Traits.Armament is not null ? 2 : -1)
            : -1;

    private static void Count(EntityTypeDefinition type, ref int boats, ref int holders, ref int armed)
    {
        if (type.Traits.Harvester is not null)
        {
            boats++;
            return;
        }

        holders += IsTransport(type) ? 1 : 0;
        armed += type.Traits.Armament is not null ? 1 : 0;
    }

    // The ships kept as transports: the first that carry, in entity order (the rest of the armed ones are warships).
    private static bool IsKeptTransport(AiExecutorContext context, EntityId ship)
    {
        int n = 0;
        foreach (EntityId s in context.Player.Ships)
        {
            EntityTypeDefinition t = context.World.TypeOf(s);
            if (t.Traits.Harvester is not null || !IsTransport(t))
            {
                continue;
            }

            if (s == ship)
            {
                return n < context.Options.Naval.Transports;
            }

            n++;
        }

        return false;
    }

    private static int FoodId(World world) => world.Ruleset.ResourceId("food");

    private void Fish(AiExecutorContext context)
    {
        World world = context.World;
        int food = FoodId(world);
        foreach (EntityId boat in context.Player.Ships)
        {
            if (context.CommandsLeft <= 0)
            {
                return;
            }

            if (world.TypeOf(boat).Traits.Harvester is null || !context.IsIdleWorker(boat) || context.IsClaimed(boat) || world.IsHeld(boat))
            {
                continue;
            }

            Position at = world.Positions.Get(boat);
            EntityId node = Shore.NearestFish(world, context.Slot, food, at.X, at.Y, context.Options.Naval.FishRadiusTiles);
            if (node.IsNone)
            {
                continue;
            }

            _orders.Clear();
            _orders.Add(boat);
            if (context.Issue(CommandKind.Harvest, CollectionsMarshal.AsSpan(_orders), target: node))
            {
                context.Claim(boat);
            }
        }
    }

    // The landing state machine (see the class remarks).
    private void Land(AiExecutorContext context, ushort landType)
    {
        World world = context.World;
        AiPlayerState state = context.State;
        int tick = world.Tick.Value;
        long elapsedMs = ((long)tick - state.NavalTick) * world.Config.TickLengthMilliseconds;
        PruneTransports(context);
        MilitaryExecutor.PruneWave(context); // the wave may have lost units since the military executor last ran
        switch (state.NavalPhase)
        {
            case AiNavalPhase.None:
            {
                if (state.WavePhase != AiWavePhase.Attacking || state.WaveUnits.Count == 0 || !WaveOverseas(context, landType))
                {
                    return;
                }

                int need = 0;
                foreach (uint v in state.WaveUnits)
                {
                    need += world.IsHeld(EntityId.FromValue(v)) ? 0 : 1;
                }

                int room = 0;
                foreach (EntityId ship in context.Player.Ships)
                {
                    if (room >= need || state.Transports.Count >= AiPlayerState.MaxTransports)
                    {
                        break;
                    }

                    if (IsTransport(world.TypeOf(ship)) && !world.IsHeld(ship) && world.CanAct(ship) && !context.IsClaimed(ship))
                    {
                        state.Transports.Add(ship.Value);
                        room += world.HolderCapacity(ship) - world.Holders.Get(ship).Count;
                    }
                }

                if (state.Transports.Count == 0)
                {
                    return; // no transport yet: the wave waits at the shore
                }

                state.NavalPhase = AiNavalPhase.Embarking;
                state.NavalTick = tick;
                Embark(context);
                return;
            }

            case AiNavalPhase.Embarking:
            {
                int ashore = 0;
                int aboard = 0;
                foreach (uint v in state.WaveUnits)
                {
                    bool held = world.IsHeld(EntityId.FromValue(v));
                    ashore += held ? 0 : 1;
                    aboard += held ? 1 : 0;
                }

                bool late = elapsedMs >= context.Options.Naval.EmbarkTimeoutMs;
                if (state.Transports.Count == 0 || (late && aboard == 0))
                {
                    Reset(state); // nothing to sail with
                    return;
                }

                if (ashore == 0 || late)
                {
                    Sail(context);
                    state.NavalPhase = AiNavalPhase.Sailing;
                    state.NavalTick = tick;
                    return;
                }

                Embark(context);
                return;
            }

            case AiNavalPhase.Sailing:
            {
                bool loaded = false;
                foreach (uint v in state.Transports)
                {
                    loaded |= world.Holders.Get(EntityId.FromValue(v)).Count > 0;
                }

                if (!loaded)
                {
                    state.Landings++;
                    Reset(state); // landed (or sunk): the military executor takes the wave on
                    return;
                }

                if (elapsedMs >= context.Options.Naval.SailRetryMs)
                {
                    Sail(context);
                    state.NavalTick = tick;
                }

                return;
            }
        }
    }

    private static void Reset(AiPlayerState state)
    {
        state.NavalPhase = AiNavalPhase.None;
        state.Transports.Clear();
    }

    // Transports that died, were captured or are frozen no longer count.
    private static void PruneTransports(AiExecutorContext context)
    {
        World world = context.World;
        List<uint> list = context.State.Transports;
        int kept = 0;
        for (int k = 0; k < list.Count; k++)
        {
            EntityId e = EntityId.FromValue(list[k]);
            if (world.Entities.IsAlive(e) && world.Identities.IndexOf(e) is >= 0 and var i && world.Identities.At(i).Owner == context.Slot && world.Holders.Has(e))
            {
                list[kept++] = list[k];
            }
        }

        list.RemoveRange(kept, list.Count - kept);
    }

    // Whether the wave's target is in another land component than the wave's units on the map.
    private static bool WaveOverseas(AiExecutorContext context, ushort landType)
    {
        World world = context.World;
        AiPlayerState state = context.State;
        int target = Shore.LandComponentAt(world, landType, state.TargetX, state.TargetY);
        foreach (uint v in state.WaveUnits)
        {
            EntityId e = EntityId.FromValue(v);
            if (world.IsHeld(e))
            {
                continue;
            }

            Position p = world.Positions.Get(e);
            int at = Shore.LandComponentAt(world, landType, p.X, p.Y);
            if (at >= 0 && at != target)
            {
                return true;
            }
        }

        return false;
    }

    // Wave units ashore without an errand board the transport with the most free room (room counts those on the way).
    private void Embark(AiExecutorContext context)
    {
        World world = context.World;
        AiPlayerState state = context.State;
        Span<int> free = stackalloc int[AiPlayerState.MaxTransports];
        for (int k = 0; k < state.Transports.Count; k++)
        {
            EntityId ship = EntityId.FromValue(state.Transports[k]);
            free[k] = world.HolderCapacity(ship) - world.Holders.Get(ship).Count;
        }

        ReadOnlySpan<Errand> errands = world.Errands.Components;
        for (int i = 0; i < errands.Length; i++)
        {
            if (errands[i].Kind == ErrandKind.Board && state.Transports.IndexOf(errands[i].Target) is >= 0 and var k)
            {
                free[k]--;
            }
        }

        for (int k = 0; k < state.Transports.Count && context.CommandsLeft > 0; k++)
        {
            _orders.Clear();
            foreach (uint v in state.WaveUnits)
            {
                EntityId e = EntityId.FromValue(v);
                if (_orders.Count >= free[k])
                {
                    break;
                }

                if (!world.IsHeld(e) && !world.Errands.Has(e) && !_orders.Contains(e) && !Assigned(context, e))
                {
                    _orders.Add(e);
                }
            }

            if (_orders.Count > 0 && context.Issue(CommandKind.Board, CollectionsMarshal.AsSpan(_orders), target: EntityId.FromValue(state.Transports[k])))
            {
                foreach (EntityId e in _orders)
                {
                    context.Claim(e);
                }
            }
        }
    }

    private static bool Assigned(AiExecutorContext context, EntityId unit) => context.IsClaimed(unit);

    private void Sail(AiExecutorContext context)
    {
        AiPlayerState state = context.State;
        foreach (uint v in state.Transports)
        {
            EntityId ship = EntityId.FromValue(v);
            if (context.CommandsLeft <= 0 || context.World.Holders.Get(ship).Count == 0)
            {
                continue;
            }

            _orders.Clear();
            _orders.Add(ship);
            if (context.Issue(CommandKind.Unload, CollectionsMarshal.AsSpan(_orders), state.TargetX, state.TargetY))
            {
                context.Claim(ship);
            }
        }
    }

    // Idle armed ships that carry no landing: the nearest enemy ship, else the enemy's shore (they fire at what is in range).
    private void Patrol(AiExecutorContext context, ushort landType, bool overseas)
    {
        World world = context.World;
        AiPlayerState state = context.State;
        foreach (EntityId ship in context.Player.Ships)
        {
            if (context.CommandsLeft <= 0)
            {
                return;
            }

            EntityTypeDefinition type = world.TypeOf(ship);
            if (type.Traits.Armament is null || type.Traits.Harvester is not null || state.Transports.Contains(ship.Value) || context.IsClaimed(ship)
                || (IsKeptTransport(context, ship) && state.WavePhase == AiWavePhase.Attacking)
                || world.IsHeld(ship) || context.IsMoving(ship) || world.Combats.IndexOf(ship) is not (>= 0 and var c) || world.Combats.At(c).Target != 0
                || (IsTransport(type) && world.Holders.Get(ship).Count > 0) || world.Dockings.Has(ship))
            {
                continue;
            }

            Position at = world.Positions.Get(ship);
            if (NearestEnemyShip(context, at, out EntityId enemy))
            {
                Order(context, ship, CommandKind.Attack, 0, 0, enemy, CommandFlags.None);
                continue;
            }

            if (!overseas || !EnemyShore(context, ship, landType, out Position water)
                || AiExecutorContext.Distance(at.X, at.Y, water.X, water.Y) <= 3L * MapState.UnitsPerTile)
            {
                continue;
            }

            Order(context, ship, CommandKind.Move, water.X, water.Y, EntityId.None, CommandFlags.Ctrl);
        }
    }

    private void Order(AiExecutorContext context, EntityId ship, CommandKind kind, int x, int y, EntityId target, CommandFlags flags)
    {
        _orders.Clear();
        _orders.Add(ship);
        if (context.Issue(kind, CollectionsMarshal.AsSpan(_orders), x, y, target, flags: flags))
        {
            context.Claim(ship);
        }
    }

    private static bool NearestEnemyShip(AiExecutorContext context, Position from, out EntityId best)
    {
        World world = context.World;
        best = EntityId.None;
        long bestDistance = 30L * MapState.UnitsPerTile;
        for (int q = 0; q < context.Census.PlayerCount; q++)
        {
            if (!AiCensus.IsEnemy(world, context.Slot, q))
            {
                continue;
            }

            foreach (EntityId e in context.Census.Player(q).Ships)
            {
                if (!MilitaryExecutor.MayTargetUnit(world, context.Slot, e))
                {
                    continue;
                }

                Position p = world.Positions.Get(e);
                long d = AiExecutorContext.Distance(from.X, from.Y, p.X, p.Y);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = e;
                }
            }
        }

        return !best.IsNone;
    }

    // The water next to the shore nearest to the town of the enemy the wave goes for (else the nearest enemy).
    private static bool EnemyShore(AiExecutorContext context, EntityId ship, ushort landType, out Position water)
    {
        World world = context.World;
        water = default;
        int target = context.State.TargetPlayer;
        Position at = world.Positions.Get(ship);
        if (target < 0 || target >= context.Census.PlayerCount || !context.Census.Player(target).Exists)
        {
            if (!MilitaryExecutor.NearestEnemyTarget(context, at.X, at.Y, preferBuildings: true, out target, out _, out _, out _))
            {
                return false;
            }
        }

        (int tx, int ty) = context.Census.Player(target).Town;
        return Shore.FindDock(world, world.Identities.Get(ship).TypeId, at, landType, Shore.LandComponentAt(world, landType, tx, ty), tx, ty, out _, out water);
    }
}
