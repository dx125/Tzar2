using System;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Map;

namespace Tzar2.Sim.Systems;

/// <summary>
/// The gameplay commands of pipeline step 1 (ADRs 0039-0044): harvest, build (and drag walls), train, research,
/// upgrade, cancel, rally, attack, repair, capture, gates. Each is re-validated when it executes (TDD §8.6: ownership,
/// existence, costs, prerequisites, placement); costs are paid when the command is applied, so two commands of one tick
/// cannot spend the same resources twice.
/// </summary>
internal static class GameplayCommands
{
    /// <summary>Most units one <see cref="CommandKind.Train"/> may queue per producer.</summary>
    public const int MaxTrainCount = ProducerDefinition.MaxQueueCapacity;

    /// <summary>How far from a coastal building (harbor) the water for its ships may be, in tiles.</summary>
    public const int CoastReachTiles = 2;

    /// <summary>Clears what a unit was doing for its own reasons before a new order (worker task, fight).</summary>
    public static void ClearTasks(World world, EntityId unit, CombatMode newMode = CombatMode.None)
    {
        TradeSystem.Cancel(world, unit); // GAME-14: another order ends a trade route
        world.CancelErrandAndCast(unit); // GAME-09…11: errands and spells in progress end too (ADR 0096)
        world.Dockings.Remove(unit); // a ship's way to the shore and exploring end too (ADRs 0140, 0143)
        world.Explorers.Remove(unit);
        int w = world.Workers.IndexOf(unit);
        if (w >= 0)
        {
            ref Worker worker = ref world.Workers.At(w);
            worker.Task = WorkerTask.None;
            worker.Phase = WorkerPhase.None;
            worker.Target = 0;
            worker.DropTarget = 0;
            worker.Retries = 0;
            worker.TimerMs = 0;
        }

        int c = world.Combats.IndexOf(unit);
        if (c >= 0)
        {
            ref Combat combat = ref world.Combats.At(c);
            combat.Target = 0;
            combat.HitInMs = -1;
            combat.ChaseTick = -1;
            combat.Resume = 0;
            combat.Mode = newMode;
        }
    }

    public static CommandRejection? Harvest(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        EntityId node = command.Target;
        int s = world.ResourceStocks.IndexOf(node);
        if (!world.Entities.IsAlive(node) || s < 0 || world.ResourceStocks.At(s).Amount == 0 || world.Constructions.Has(node)
            || !Interaction.MayHarvest(world, command.Player, node))
        {
            return CommandRejection.InvalidArgument; // gone, empty, unfinished, or another kingdom's field or cow
        }

        int applied = 0;
        foreach (EntityId entity in entities)
        {
            if (StartHarvest(world, command.Player, entity, node))
            {
                applied++;
            }
        }

        return applied > 0 ? null : CommandRejection.NoValidEntities;
    }

    /// <summary>Gives an own worker that can gather the node's resource the task of harvesting it.</summary>
    public static bool StartHarvest(World world, byte player, EntityId entity, EntityId node)
    {
        int w = OwnedWorker(world, entity, player);
        int resource = world.TypeOf(node).Traits.ResourceNode!.Resource;
        if (w < 0 || world.TypeOf(entity).Traits.Harvester is not { } harvester || !harvester.CanGather(resource)
            || !Interaction.CanReachNode(world, world.TypeOf(entity).Locomotor, node))
        {
            return false; // boats fish, peasants gather on land (GAME-02, ADR 0141)
        }

        ClearTasks(world, entity);
        Interaction.Halt(world, entity);
        Position at = world.Positions.Get(node);
        ref Worker worker = ref world.Workers.At(world.Workers.IndexOf(entity));
        worker.Task = WorkerTask.Harvest;
        worker.Phase = worker.CarryAmount >= CarryCapacity(world, entity) ? WorkerPhase.ToDropOff : WorkerPhase.ToNode;
        worker.Target = node.Value;
        worker.NodeResource = (byte)resource;
        worker.NodeX = at.X;
        worker.NodeY = at.Y;
        return true;
    }

    /// <summary>The carry capacity of a harvester after its owner's technology effects.</summary>
    public static int CarryCapacity(World world, EntityId worker)
    {
        EntityTypeDefinition type = world.TypeOf(worker);
        return Math.Max(1, world.Stats.Apply(world.Identities.Get(worker).Owner, ModifierStat.CarryCapacity, type.Id, type.Traits.Harvester?.Capacity ?? 0));
    }

    public static CommandRejection? Build(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        EntityId site = command.Target;
        ushort typeId;
        if (!site.IsNone)
        {
            // Help build an existing site.
            if (!world.Entities.IsAlive(site) || !world.Constructions.Has(site) || world.Identities.Get(site).Owner != command.Player)
            {
                return CommandRejection.InvalidArgument;
            }

            typeId = world.Identities.Get(site).TypeId;
            return Assign(world, command.Player, site, typeId, entities, WorkerTask.Build) > 0 ? null : CommandRejection.NoValidEntities;
        }

        if ((uint)command.Arg0 >= (uint)world.Ruleset.EntityTypes.Count || command.Arg1 is < 0 or > 1)
        {
            return CommandRejection.InvalidArgument;
        }

        typeId = (ushort)command.Arg0;
        EntityTypeDefinition type = world.Ruleset.EntityTypes[typeId];
        if (!type.HasFootprint || type.Traits.BuildTimeMs <= 0)
        {
            return CommandRejection.InvalidArgument;
        }

        if (!AnyBuilder(world, command.Player, typeId, entities, out string civilization))
        {
            return CommandRejection.NoValidEntities;
        }

        if (!world.Map.ContainsPoint(command.X, command.Y))
        {
            return CommandRejection.OutOfMap;
        }

        if (!ResearchRules.CanMake(world, command.Player, civilization, type))
        {
            return CommandRejection.RequirementNotMet;
        }

        int tileX = command.X / MapState.UnitsPerTile;
        int tileY = command.Y / MapState.UnitsPerTile;
        bool rotated = command.Arg1 == 1;
        if (!CanPlace(world, type, tileX, tileY, rotated))
        {
            return CommandRejection.IllegalPlacement;
        }

        if (world.Entities.RemainingCapacity < 1)
        {
            return CommandRejection.EntityLimit;
        }

        if (!ResearchRules.TryPayFor(world, command.Player, type, out PaidModifier paid))
        {
            return CommandRejection.NotAffordable;
        }

        site = world.PlaceStructure(typeId, command.Player, tileX, tileY, underConstruction: true, rotated: rotated);
        world.Constructions.Get(site).Paid = paid;
        Position p = world.Positions.Get(site);
        world.Events.Emit(new SimEvent(SimEventKind.ConstructionStarted, command.Player, site, p.X, p.Y, typeId));
        Assign(world, command.Player, site, typeId, entities, WorkerTask.Build);
        return null;
    }

    /// <summary>
    /// Drags a wall (GAME-03; ADR 0043): one site per tile on the straight line from the start tile to the end tile
    /// (Bresenham, at most <c>maxWallSegments</c>), each paid when placed; tiles that do not fit are skipped, and the
    /// line stops at the first segment the player cannot pay. The builders start on the first segment and move on to
    /// the next unfinished one when it completes.
    /// </summary>
    public static CommandRejection? BuildWall(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if ((uint)command.Arg0 >= (uint)world.Ruleset.EntityTypes.Count)
        {
            return CommandRejection.InvalidArgument;
        }

        ushort typeId = (ushort)command.Arg0;
        EntityTypeDefinition type = world.Ruleset.EntityTypes[typeId];
        if (!type.Traits.IsWall || type.Traits.BuildTimeMs <= 0)
        {
            return CommandRejection.InvalidArgument;
        }

        if (!world.Map.ContainsPoint(command.X, command.Y))
        {
            return CommandRejection.OutOfMap;
        }

        int x1 = command.Arg1 & 0xFFFF;
        int y1 = (command.Arg1 >> 16) & 0xFFFF;
        if (!world.Map.ContainsTile(x1, y1))
        {
            return CommandRejection.OutOfMap;
        }

        if (!AnyBuilder(world, command.Player, typeId, entities, out string civilization))
        {
            return CommandRejection.NoValidEntities;
        }

        if (!ResearchRules.CanMake(world, command.Player, civilization, type))
        {
            return CommandRejection.RequirementNotMet;
        }

        int x0 = command.X / MapState.UnitsPerTile;
        int y0 = command.Y / MapState.UnitsPerTile;
        int dx = Math.Abs(x1 - x0);
        int dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        EntityId first = EntityId.None;
        CommandRejection reason = CommandRejection.IllegalPlacement;
        for (int n = 0; n < world.Ruleset.Gameplay.MaxWallSegments; n++)
        {
            if (CanPlace(world, type, x0, y0, rotated: false))
            {
                if (world.Entities.RemainingCapacity < 1)
                {
                    reason = CommandRejection.EntityLimit;
                    break;
                }

                if (!ResearchRules.TryPayFor(world, command.Player, type, out PaidModifier paid))
                {
                    reason = CommandRejection.NotAffordable;
                    break;
                }

                EntityId segment = world.PlaceStructure(typeId, command.Player, x0, y0, underConstruction: true);
                world.Constructions.Get(segment).Paid = paid;
                Position p = world.Positions.Get(segment);
                world.Events.Emit(new SimEvent(SimEventKind.ConstructionStarted, command.Player, segment, p.X, p.Y, typeId));
                first = first.IsNone ? segment : first;
            }

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int e2 = 2 * error;
            if (e2 >= dy)
            {
                error += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }

        if (first.IsNone)
        {
            return reason;
        }

        Assign(world, command.Player, first, typeId, entities, WorkerTask.Build);
        return null;
    }

    public static CommandRejection? Train(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if ((uint)command.Arg0 >= (uint)world.Ruleset.EntityTypes.Count || command.Arg1 is < 1 or > MaxTrainCount)
        {
            return CommandRejection.InvalidArgument;
        }

        ushort typeId = (ushort)command.Arg0;
        EntityTypeDefinition type = world.Ruleset.EntityTypes[typeId];
        CommandRejection? reason = CommandRejection.NoValidEntities;
        int queued = 0;
        foreach (EntityId entity in entities)
        {
            if (!IsOwned(world, entity, command.Player) || world.Productions.IndexOf(entity) is not (>= 0 and var q)
                || !world.TypeOf(entity).Traits.Producer!.CanProduce(typeId))
            {
                continue;
            }

            if (!ResearchRules.CanMake(world, command.Player, world.CivilizationOf(entity), type))
            {
                reason = CommandRejection.RequirementNotMet;
                continue;
            }

            int capacity = world.TypeOf(entity).Traits.Producer!.QueueCapacity;
            for (int n = 0; n < command.Arg1; n++)
            {
                ref Production production = ref world.Productions.At(q);
                if (production.Count >= capacity)
                {
                    reason = queued > 0 ? null : CommandRejection.QueueFull;
                    break;
                }

                if (!ResearchRules.TryPayFor(world, command.Player, type, out PaidModifier paid))
                {
                    reason = queued > 0 ? null : CommandRejection.NotAffordable;
                    break;
                }

                production.Paid[production.Count] = paid;
                production.Queue[production.Count++] = typeId;
                queued++;
            }
        }

        return queued > 0 ? null : reason;
    }

    /// <summary>
    /// Research (GAME-06; ADR 0040): technology Arg0 is queued in the first listed own building that researches it and
    /// whose civilization's tree allows it now; one research per technology and player at a time.
    /// </summary>
    public static CommandRejection? Research(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if ((uint)command.Arg0 >= (uint)world.Ruleset.Technologies.Count)
        {
            return CommandRejection.InvalidArgument;
        }

        int technology = command.Arg0;
        TechnologyDefinition tech = world.Ruleset.Technologies[technology];
        if (world.Players[command.Player].HasTechnology(technology) || ResearchRules.IsResearchPending(world, command.Player, technology))
        {
            return CommandRejection.AlreadyResearched;
        }

        CommandRejection reason = CommandRejection.NoValidEntities;
        foreach (EntityId entity in entities)
        {
            if (!IsOwned(world, entity, command.Player) || world.Productions.IndexOf(entity) is not (>= 0 and var q)
                || !world.TypeOf(entity).Traits.Producer!.CanResearch((ushort)technology))
            {
                continue;
            }

            if (!ResearchRules.RequirementsMet(world, command.Player, world.CivilizationOf(entity), tech.Requirements, []))
            {
                reason = CommandRejection.RequirementNotMet;
                continue;
            }

            ref Production production = ref world.Productions.At(q);
            if (production.Count >= world.TypeOf(entity).Traits.Producer!.QueueCapacity)
            {
                reason = CommandRejection.QueueFull;
                continue;
            }

            if (!ResearchRules.TryPay(world, command.Player, tech.Cost))
            {
                return CommandRejection.NotAffordable;
            }

            production.Queue[production.Count++] = QueueItems.Research(technology);
            return null;
        }

        return reason;
    }

    /// <summary>
    /// Upgrade (GAME-06): upgrade Arg0 is queued on each listed own completed entity that offers it, has not got it (level
    /// upgrades), has its earlier levels, and whose civilization's tree allows it.
    /// </summary>
    public static CommandRejection? Upgrade(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if ((uint)command.Arg0 >= (uint)world.Ruleset.Upgrades.Count)
        {
            return CommandRejection.InvalidArgument;
        }

        int upgrade = command.Arg0;
        UpgradeDefinition u = world.Ruleset.Upgrades[upgrade];
        CommandRejection? reason = CommandRejection.NoValidEntities;
        int queued = 0;
        foreach (EntityId entity in entities)
        {
            if (!IsOwned(world, entity, command.Player) || world.Productions.IndexOf(entity) is not (>= 0 and var q)
                || !world.TypeOf(entity).Traits.Producer!.CanUpgrade((ushort)upgrade)
                || (u.IsConversion && u.ConvertedType(world.Identities.Get(entity).TypeId) < 0))
            {
                continue;
            }

            if (ResearchRules.HasOrQueuedUpgrade(world, entity, upgrade) || (u.IsConversion && QueuedConversion(world, q)))
            {
                reason = queued > 0 ? null : CommandRejection.AlreadyResearched;
                continue;
            }

            if (!ResearchRules.HasPrecedingLevels(world, entity, u)
                || !ResearchRules.RequirementsMet(world, command.Player, world.CivilizationOf(entity), u.Requirements, []))
            {
                reason = queued > 0 ? null : CommandRejection.RequirementNotMet;
                continue;
            }

            ref Production production = ref world.Productions.At(q);
            if (production.Count >= world.TypeOf(entity).Traits.Producer!.QueueCapacity)
            {
                reason = queued > 0 ? null : CommandRejection.QueueFull;
                continue;
            }

            if (!ResearchRules.TryPay(world, command.Player, u.Cost))
            {
                reason = queued > 0 ? null : CommandRejection.NotAffordable;
                break;
            }

            production.Queue[production.Count++] = QueueItems.Upgrade(upgrade);
            queued++;
        }

        return queued > 0 ? null : reason;
    }

    public static CommandRejection? Cancel(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        int cancelled = 0;
        foreach (EntityId entity in entities)
        {
            if (!IsOwned(world, entity, command.Player))
            {
                continue;
            }

            if (world.Constructions.Has(entity))
            {
                ushort typeId = world.Identities.Get(entity).TypeId;
                ResearchRules.RefundFor(world, command.Player, world.Ruleset.EntityTypes[typeId], world.Constructions.Get(entity).Paid, world.Ruleset.Gameplay.ConstructionRefundBasisPoints);
                world.Events.Emit(new SimEvent(SimEventKind.ConstructionCancelled, command.Player, entity, 0, 0, typeId));
                world.Destroy(entity);
                cancelled++;
                continue;
            }

            int q = world.Productions.IndexOf(entity);
            if (q < 0)
            {
                continue;
            }

            ref Production production = ref world.Productions.At(q);
            int slot = command.Arg0 < 0 ? production.Count - 1 : command.Arg0;
            if (slot < 0 || slot >= production.Count)
            {
                continue;
            }

            ushort item = production.Queue[slot];
            PaidModifier itemPaid = production.Paid[slot];
            for (int k = slot + 1; k < production.Count; k++)
            {
                production.Queue[k - 1] = production.Queue[k];
                production.Paid[k - 1] = production.Paid[k];
            }

            production.Count--;
            production.Queue[production.Count] = 0;
            production.Paid[production.Count] = default;
            if (slot == 0)
            {
                production.ProgressMs = 0;
                production.Stall = 0;
            }

            GameplaySettings g = world.Ruleset.Gameplay;
            if (QueueItems.IsResearch(item))
            {
                ResearchRules.Refund(world, command.Player, world.Ruleset.Technologies[QueueItems.Id(item)].Cost, g.ResearchRefundBasisPoints);
            }
            else if (QueueItems.IsUpgrade(item))
            {
                ResearchRules.Refund(world, command.Player, world.Ruleset.Upgrades[QueueItems.Id(item)].Cost, g.ResearchRefundBasisPoints);
            }
            else
            {
                ResearchRules.RefundFor(world, command.Player, world.Ruleset.EntityTypes[item], itemPaid, g.ProductionRefundBasisPoints);
            }

            cancelled++;
        }

        return cancelled > 0 ? null : CommandRejection.NoValidEntities;
    }

    public static CommandRejection? SetRally(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        bool clear = command.Arg0 == 1;
        if (!clear && !world.Map.ContainsPoint(command.X, command.Y))
        {
            return CommandRejection.OutOfMap;
        }

        if (command.Arg1 is < 0 or > (int)RallyMode.MoveAdvance)
        {
            return CommandRejection.InvalidArgument;
        }

        int applied = 0;
        foreach (EntityId entity in entities)
        {
            if (!IsOwned(world, entity, command.Player) || world.Productions.IndexOf(entity) is not (>= 0 and var q))
            {
                continue;
            }

            ref Production production = ref world.Productions.At(q);
            production.HasRally = clear ? (byte)0 : (byte)1;
            production.RallyX = clear ? 0 : command.X;
            production.RallyY = clear ? 0 : command.Y;
            production.RallyMode = clear ? RallyMode.Move : (RallyMode)command.Arg1;
            production.RallyTarget = clear || command.Arg1 != (int)RallyMode.DefaultAction ? 0 : command.Target.Value;
            applied++;
        }

        return applied > 0 ? null : CommandRejection.NoValidEntities;
    }

    public static CommandRejection? Attack(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        EntityId target = command.Target;
        int t = world.Identities.IndexOf(target);
        if (!world.Entities.IsAlive(target) || t < 0)
        {
            return CommandRejection.InvalidArgument;
        }

        Identity identity = world.Identities.At(t);
        EntityTraits traits = world.Ruleset.EntityTypes[identity.TypeId].Traits;
        // Nature's units are hostile to everyone and can be attacked; resources cannot (combat targets, ADR 0038). Own
        // animals that leave meat (cows) may be butchered (GAME-02, ADR 0042).
        bool butcher = identity.Owner == command.Player && traits.SpawnOnDeath >= 0 && traits.Category == EntityCategory.Unit;
        bool training = CombatSystem.IsTrainingTarget(world, command.Player, target); // own dummies (GAME-08)
        if ((identity.Owner == command.Player && !butcher && !training) || traits.Category is not (EntityCategory.Unit or EntityCategory.Building)
            || world.IsHeld(target))
        {
            return CommandRejection.InvalidArgument;
        }

        if (!butcher && !training && !world.MayAttack(command.Player, target))
        {
            return CommandRejection.NotAllied; // allies (and neutrals' non-military units) cannot be attacked (GAME-13)
        }

        int applied = 0;
        foreach (EntityId entity in entities)
        {
            // Shared unit control (GAME-13) covers fighting; economic orders stay with the owner (their costs and refunds).
            if (!world.MayCommand(command.Player, entity) || world.Combats.IndexOf(entity) < 0 || entity == target
                || (world.Identities.Get(entity).Owner != command.Player && !world.MayAttack(world.Identities.Get(entity).Owner, target)))
            {
                continue;
            }

            ClearTasks(world, entity, CombatMode.Explicit);
            Interaction.Halt(world, entity);
            world.Combats.Get(entity).Target = target.Value;
            applied++;
        }

        return applied > 0 ? null : CommandRejection.NoValidEntities;
    }

    /// <summary>Repair (GAME-03; ADR 0043): own completed, damaged, repairable target; builders walk there and restore it.</summary>
    public static CommandRejection? Repair(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        EntityId target = command.Target;
        if (!world.Entities.IsAlive(target) || world.Identities.IndexOf(target) is not (>= 0 and var t) || world.Identities.At(t).Owner != command.Player
            || !world.TypeOf(target).Traits.IsRepairable || world.Constructions.Has(target))
        {
            return CommandRejection.InvalidArgument;
        }

        Health health = world.Healths.Get(target);
        if (health.Current >= health.Max)
        {
            return CommandRejection.InvalidArgument;
        }

        if (Assign(world, command.Player, target, 0, entities, WorkerTask.Repair) == 0)
        {
            return CommandRejection.NoValidEntities;
        }

        NavalCommands.MeetAtShore(world, target, entities, DockPurpose.Repair); // a ship comes to its repairers (GAME-03)
        return null;
    }

    /// <summary>
    /// Capture (GAME-04; ADR 0044): another kingdom's completed capturable building (peasants dig at it), or an instant
    /// capture (cows under Cow Poaching: the capturing player needs the technology).
    /// </summary>
    public static CommandRejection? Capture(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        EntityId target = command.Target;
        if (!world.Entities.IsAlive(target) || world.Identities.IndexOf(target) is not (>= 0 and var t))
        {
            return CommandRejection.InvalidArgument;
        }

        byte owner = world.Identities.At(t).Owner;
        CapturableDefinition? capturable = world.TypeOf(target).Traits.Capturable;
        if (capturable is null || owner == command.Player || !world.HasPlayer(owner) || world.Constructions.Has(target))
        {
            return CommandRejection.InvalidArgument;
        }

        if (world.Diplomacy.StanceOf(command.Player, owner) != Kingdoms.Stance.Enemy)
        {
            return CommandRejection.NotAllied; // only enemies' buildings are captured (GAME-13)
        }

        if (capturable.RequiredTechnology >= 0 && !world.Players[command.Player].HasTechnology(capturable.RequiredTechnology))
        {
            return CommandRejection.RequirementNotMet;
        }

        int applied = 0;
        foreach (EntityId entity in entities)
        {
            if (OwnedWorker(world, entity, command.Player) < 0 || !world.TypeOf(entity).Traits.IsCapturer)
            {
                continue;
            }

            ClearTasks(world, entity);
            Interaction.Halt(world, entity);
            ref Worker worker = ref world.Workers.Get(entity);
            worker.Task = WorkerTask.Capture;
            worker.Phase = WorkerPhase.ToTarget;
            worker.Target = target.Value;
            applied++;
        }

        return applied > 0 ? null : CommandRejection.NoValidEntities;
    }

    /// <summary>
    /// Gates (GAME-03; ADR 0043): Arg0 0 = close, 1 = open (manual control), 2 = automatic. An open/close order on a gate
    /// that changed less than <c>gateChangeIntervalMs</c> ago is rejected for that gate (<see cref="CommandRejection.GateBusy"/>
    /// when no listed gate takes it); step 10 carries the order out within the per-tick change budget, and never closes a
    /// gate on a unit standing in it.
    /// </summary>
    public static CommandRejection? SetGate(World world, in Command command, ReadOnlySpan<EntityId> entities)
    {
        if (command.Arg0 is < 0 or > 2)
        {
            return CommandRejection.InvalidArgument;
        }

        int interval = GateIntervalTicks(world);
        int applied = 0;
        bool busy = false;
        foreach (EntityId entity in entities)
        {
            if (!IsOwned(world, entity, command.Player) || world.Gates.IndexOf(entity) is not (>= 0 and var g))
            {
                continue;
            }

            ref GateState gate = ref world.Gates.At(g);
            if (command.Arg0 == 2)
            {
                gate.Mode = GateMode.Automatic;
                applied++;
                continue;
            }

            if ((long)world.Tick.Value - gate.LastChangeTick < interval)
            {
                busy = true;
                continue;
            }

            gate.Mode = GateMode.Manual;
            gate.Desired = command.Arg0 == 1 ? (byte)1 : (byte)0;
            applied++;
        }

        return applied > 0 ? null : busy ? CommandRejection.GateBusy : CommandRejection.NoValidEntities;
    }

    /// <summary>The least ticks between two state changes of one gate.</summary>
    internal static int GateIntervalTicks(World world)
        => (world.Ruleset.Gameplay.GateChangeIntervalMs + world.Config.TickLengthMilliseconds - 1) / world.Config.TickLengthMilliseconds;

    /// <summary>
    /// Whether the type's footprint (turned by 90° when <paramref name="rotated"/>) fits at (tileX, tileY) under its
    /// placement rule: land (every blocked tile walkable land, no structure, no bridge), coast (also water next to it),
    /// or bridge (ends on land, a deck over water, ADR 0043).
    /// </summary>
    internal static bool CanPlace(World world, EntityTypeDefinition type, int tileX, int tileY, bool rotated = false)
    {
        var f = new Footprint { TileX = tileX, TileY = tileY, Rotated = rotated && type.FootprintWidth != type.FootprintHeight ? (byte)1 : (byte)0 };
        int w = f.Width(type.FootprintWidth, type.FootprintHeight);
        int h = f.Height(type.FootprintWidth, type.FootprintHeight);
        MapState map = world.Map;
        if (!map.ContainsTile(tileX, tileY) || !map.ContainsTile(tileX + w - 1, tileY + h - 1))
        {
            return false;
        }

        if (type.Traits.Placement == PlacementKind.Bridge)
        {
            return CanPlaceBridge(map, tileX, tileY, w, h);
        }

        ReadOnlySpan<byte> mask = type.Footprint;
        for (int dy = 0; dy < h; dy++)
        {
            for (int dx = 0; dx < w; dx++)
            {
                // Free mask cells (cut corners) may hold anything; blocked ones need walkable land without a structure.
                if (mask[f.MaskIndex(dx, dy, type.FootprintWidth)] != 0
                    && (!map.IsPassable(Locomotor.Foot, tileX + dx, tileY + dy) || map.Bridges[((tileY + dy) * map.Width) + tileX + dx] != 0))
                {
                    return false;
                }
            }
        }

        if (type.Traits.Placement == PlacementKind.Coast)
        {
            // Water for ships within two tiles of the footprint (a shore tile between land and water may carry neither).
            for (int y = tileY - CoastReachTiles; y < tileY + h + CoastReachTiles; y++)
            {
                for (int x = tileX - CoastReachTiles; x < tileX + w + CoastReachTiles; x++)
                {
                    if (map.IsPassable(Locomotor.Ship, x, y))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        return true;
    }

    // A bridge runs along its long axis: its first and last rows across that axis stand on walkable land, every tile is
    // free (land, water or shore; no cliff, structure or other bridge), and at least one tile is open water.
    private static bool CanPlaceBridge(MapState map, int tileX, int tileY, int w, int h)
    {
        bool horizontal = w >= h;
        bool water = false;
        for (int dy = 0; dy < h; dy++)
        {
            for (int dx = 0; dx < w; dx++)
            {
                int x = tileX + dx;
                int y = tileY + dy;
                int i = (y * map.Width) + x;
                bool land = map.IsPassable(Locomotor.Foot, x, y);
                bool sea = map.IsPassable(Locomotor.Ship, x, y);
                bool end = horizontal ? dx == 0 || dx == w - 1 : dy == 0 || dy == h - 1;
                if (map.Data.CliffTiles[i] != 0 || map.Blockers[i] != 0 || (end && !land) || map.Bridges[i] != 0)
                {
                    return false;
                }

                water |= sea;
            }
        }

        return water;
    }

    /// <summary>Whether the player has completed every required building and researched every required technology for <paramref name="civilization"/>.</summary>
    internal static bool RequirementsMet(World world, byte player, EntityTypeDefinition type, string civilization)
        => ResearchRules.CanMake(world, player, civilization, type);

    private static bool QueuedConversion(World world, int productionIndex)
    {
        ref Production q = ref world.Productions.At(productionIndex);
        for (int k = 0; k < q.Count; k++)
        {
            if (QueueItems.IsUpgrade(q.Queue[k]) && world.Ruleset.Upgrades[QueueItems.Id(q.Queue[k])].IsConversion)
            {
                return true; // one conversion at a time: the next one depends on what the entity becomes
            }
        }

        return false;
    }

    private static int Assign(World world, byte player, EntityId target, ushort typeId, ReadOnlySpan<EntityId> entities, WorkerTask task)
    {
        int assigned = 0;
        foreach (EntityId entity in entities)
        {
            if (OwnedWorker(world, entity, player) < 0 || (task == WorkerTask.Build ? !world.TypeOf(entity).Traits.CanBuild(typeId) : !world.TypeOf(entity).Traits.IsBuilder))
            {
                continue;
            }

            ClearTasks(world, entity);
            Interaction.Halt(world, entity);
            ref Worker worker = ref world.Workers.Get(entity);
            worker.Task = task;
            worker.Phase = task == WorkerTask.Build ? WorkerPhase.ToSite : WorkerPhase.ToTarget;
            worker.Target = target.Value;
            assigned++;
        }

        return assigned;
    }

    // Whether one of the listed own workers can build the type; its civilization's tree decides the requirements.
    private static bool AnyBuilder(World world, byte player, ushort typeId, ReadOnlySpan<EntityId> entities, out string civilization)
    {
        foreach (EntityId entity in entities)
        {
            if (OwnedWorker(world, entity, player) >= 0 && world.TypeOf(entity).Traits.CanBuild(typeId))
            {
                civilization = world.CivilizationOf(entity);
                return true;
            }
        }

        civilization = string.Empty;
        return false;
    }

    private static int OwnedWorker(World world, EntityId entity, byte player)
        => IsOwned(world, entity, player) ? world.Workers.IndexOf(entity) : -1;

    private static bool IsOwned(World world, EntityId entity, byte player)
    {
        if (!world.Entities.IsAlive(entity))
        {
            return false;
        }

        int position = world.Identities.IndexOf(entity);
        return position >= 0 && world.Identities.At(position).Owner == player;
    }
}
