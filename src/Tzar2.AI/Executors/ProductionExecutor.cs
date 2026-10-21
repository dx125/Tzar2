using System;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Systems;

namespace Tzar2.AI.Executors;

/// <summary>
/// Unit goals → <see cref="CommandKind.Train"/> (REQ-AI-02 production manager; ADR 0052). In goal order, each unmet
/// unit goal queues what is missing (counting units already queued) in the own producer with the shortest queue, as far
/// as population room, the budget (with the goal's locked resources, except for workers) and the queue allow; at most
/// <see cref="MaxPerCommand"/> per command so that several goals share the producers.
/// </summary>
public sealed class ProductionExecutor : IAiExecutor
{
    /// <summary>Most units one train command queues (UNVERIFIED).</summary>
    public const int MaxPerCommand = 2;

    /// <inheritdoc />
    public string Name => "production";

    /// <inheritdoc />
    public void Run(AiExecutorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.EconomyAi)
        {
            return;
        }

        World world = context.World;
        AiCensusPlayer player = context.Player;
        ref readonly PlayerState state = ref world.Players[context.Slot];
        int room = state.PopulationCap - state.Population - player.QueuedPopulation;
        foreach (AiGoal goal in context.Goals)
        {
            if (context.CommandsLeft <= 0)
            {
                return;
            }

            AiSymbol symbol = context.SymbolOf(goal);
            if (goal.Kind != AiGoalKind.Object || symbol.OwnType < 0 || context.Model.IsBuildingClass(symbol.Id))
            {
                continue;
            }

            ushort typeId = (ushort)symbol.OwnType;
            EntityTypeDefinition type = world.Ruleset.EntityTypes[typeId];
            int deficit = context.TargetOf(goal) - player.Completed[symbol.Id] - context.InProgress(symbol.Id);
            int pop = Math.Max(0, type.Traits.PopulationCost);
            if (deficit <= 0 || (pop > 0 && room < pop) || !context.FirstAttempt(symbol.Id, 1) || !PlacementQueries.RequirementsMet(world, context.Slot, typeId))
            {
                continue; // met, no room, already worked on this run (once per class), or not allowed yet
            }

            EntityId producer = ShortestQueue(context, typeId, out int free);
            if (producer.IsNone)
            {
                continue;
            }

            int n = Math.Min(Math.Min(deficit, MaxPerCommand), free);
            if (pop > 0)
            {
                n = Math.Min(n, room / pop);
            }

            // Workers ignore needs_locked (UNVERIFIED): saving must not starve the economy that does the saving.
            byte locked = context.Model.IsWorker(typeId) ? (byte)0 : goal.LockedResources;
            while (n > 0 && !context.CanAfford(type.Traits.Cost, locked, n))
            {
                n--;
            }

            context.Selection.Clear();
            context.Selection.Add(producer);
            if (n > 0 && context.Issue(CommandKind.Train, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(context.Selection), arg0: typeId, arg1: n))
            {
                context.Spend(type.Traits.Cost, n);
                context.AddInProgress(symbol.Id, n);
                room -= n * pop;
            }
        }
    }

    // Items the AI keeps queued per building at most: the sim's old queue length (ai-placeholders E21; the sim queues any
    // number since ADR 0180).
    private const int QueueDepth = 10;

    // The own completed producer of typeId with the most free queue slots (ties: lower entity index).
    private static EntityId ShortestQueue(AiExecutorContext context, ushort typeId, out int free)
    {
        World world = context.World;
        EntityId best = EntityId.None;
        free = 0;
        foreach (EntityId p in context.Player.Producers)
        {
            ProducerDefinition? producer = world.TypeOf(p).Traits.Producer;
            if (producer is null || !producer.CanProduce(typeId))
            {
                continue;
            }

            int slots = QueueDepth - QueueRuns.Items(world.Productions.Get(p));
            if (slots > free)
            {
                free = slots;
                best = p;
            }
        }

        return best;
    }
}
