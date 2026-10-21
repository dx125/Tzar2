using System;
using System.Collections.Immutable;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Systems;

namespace Tzar2.AI.Executors;

/// <summary>
/// Technology goals → <see cref="CommandKind.Research"/> (REQ-AI-02 research manager, GAME-06; ADR 0052). In goal order:
/// each <c>have te_*</c> goal that is neither researched nor queued is queued in an own building that researches it and
/// whose tree allows it now, within the budget (the goal's locked resources respected). Then, for unit and building
/// goals the tree blocks, the first missing technology of their requirements is researched the same way (UNVERIFIED:
/// the original scripts usually ask for the technology first). At most <see cref="MaxPerRun"/> researches per run.
/// </summary>
public sealed class ResearchExecutor : IAiExecutor
{
    /// <summary>Most researches started per run (UNVERIFIED).</summary>
    public const int MaxPerRun = 2;

    private readonly int[] _tried = new int[Ruleset.MaxTechnologies];
    private int _stamp;

    /// <inheritdoc />
    public string Name => "research";

    /// <inheritdoc />
    public void Run(AiExecutorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.EconomyAi || context.World.Ruleset.Technologies.Count == 0)
        {
            return;
        }

        // A fresh stamp per run; the array is cleared on wrap so stamps never repeat.
        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_tried);
            _stamp = 1;
        }

        int started = 0;
        foreach (AiGoal goal in context.Goals)
        {
            if (started >= MaxPerRun || context.CommandsLeft <= 0)
            {
                return;
            }

            AiSymbol symbol = context.SymbolOf(goal);
            if (goal.Kind == AiGoalKind.Technology && symbol.Id >= 0)
            {
                started += TryResearch(context, symbol.Id, goal.LockedResources) ? 1 : 0;
            }
        }

        // Technologies that unit and building goals wait for.
        foreach (AiGoal goal in context.Goals)
        {
            if (started >= MaxPerRun || context.CommandsLeft <= 0)
            {
                return;
            }

            AiSymbol symbol = context.SymbolOf(goal);
            if (goal.Kind != AiGoalKind.Object || symbol.OwnType < 0 || PlacementQueries.RequirementsMet(context.World, context.Slot, (ushort)symbol.OwnType))
            {
                continue;
            }

            int missing = MissingTechnology(context, context.World.Ruleset.EntityTypes[symbol.OwnType].Traits.Requirements);
            if (missing >= 0)
            {
                started += TryResearch(context, missing, goal.LockedResources) ? 1 : 0;
            }
        }
    }

    // The first technology of the applicable requirement sets the player lacks (ids ascending as compiled), or -1.
    private static int MissingTechnology(AiExecutorContext context, ImmutableArray<RequirementSet> sets)
    {
        World world = context.World;
        string civ = world.PlayerCivilization(context.Slot);
        foreach (RequirementSet set in sets)
        {
            if (!set.AppliesTo(civ) || set.Unsatisfiable)
            {
                continue;
            }

            foreach (ushort tech in set.Technologies)
            {
                if (!world.Players[context.Slot].HasTechnology(tech))
                {
                    return tech;
                }
            }
        }

        return -1;
    }

    private bool TryResearch(AiExecutorContext context, int technology, byte locked)
    {
        if (_tried[technology] == _stamp)
        {
            return false; // asked for by several goals: once per run
        }

        _tried[technology] = _stamp;
        World world = context.World;
        TechnologyDefinition tech = world.Ruleset.Technologies[technology];
        if (world.Players[context.Slot].HasTechnology(technology) || GameplayQueries.IsResearchPending(world, context.Slot, technology)
            || !context.CanAfford(tech.Cost, locked))
        {
            return false;
        }

        // The own building with the shortest queue that may research it now (ties: lower entity index).
        EntityId best = EntityId.None;
        int bestCount = int.MaxValue;
        foreach (EntityId p in context.Player.Producers)
        {
            if (context.IsClaimed(p) || !GameplayQueries.CanResearchAt(world, context.Slot, p, technology))
            {
                continue;
            }

            int count = QueueRuns.Items(world.Productions.Get(p));
            if (count < bestCount)
            {
                bestCount = count;
                best = p;
            }
        }

        if (best.IsNone)
        {
            return false;
        }

        context.Selection.Clear();
        context.Selection.Add(best);
        if (!context.Issue(CommandKind.Research, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(context.Selection), arg0: technology))
        {
            return false;
        }

        context.Spend(tech.Cost);
        return true;
    }
}
