using System;
using Tzar2.AI.Runtime;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Map;
using Tzar2.Sim.Systems;

namespace Tzar2.AI.Executors;

/// <summary>
/// Building goals → <see cref="CommandKind.Build"/> (REQ-AI-02 build placement; ADR 0052). In goal order: the first
/// unmet building goal that is affordable (with the goal's locked resources), whose requirements are met and that has
/// a site starts one new construction per run with the nearest free builders; a site nobody works on gets a builder.
/// The number of sites at a time grows with the number of workers.
/// </summary>
public sealed class ConstructionExecutor : IAiExecutor
{
    /// <inheritdoc />
    public string Name => "construction";

    /// <inheritdoc />
    public void Run(AiExecutorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Settings.EconomyAi || context.Player.Workers.Count == 0)
        {
            return;
        }

        StaffSites(context);
        StartSites(context);
    }

    /// <summary>Builders per new site by footprint area (UNVERIFIED): 1 up to 4 tiles, 2 up to 9, else 3.</summary>
    internal static int BuildersFor(EntityTypeDefinition type)
    {
        int area = type.FootprintWidth * type.FootprintHeight;
        return area <= 4 ? 1 : area <= 9 ? 2 : 3;
    }

    /// <summary>
    /// The free workers able to build <paramref name="typeId"/> nearest to (<paramref name="x"/>, <paramref name="y"/>):
    /// idle ones first, then gatherers (never builders); ties to the lower entity index. Adds up to
    /// <paramref name="count"/> to <see cref="AiExecutorContext.Selection"/>.
    /// </summary>
    internal static void PickBuilders(AiExecutorContext context, ushort typeId, int x, int y, int count)
    {
        World world = context.World;
        context.Selection.Clear();
        for (int n = 0; n < count; n++)
        {
            EntityId best = EntityId.None;
            long bestScore = long.MaxValue;
            foreach (EntityId w in context.Player.Workers)
            {
                if (context.IsClaimed(w) || context.Selection.Contains(w) || !world.TypeOf(w).Traits.CanBuild(typeId))
                {
                    continue;
                }

                Worker state = world.Workers.Get(w);
                if (state.Task == WorkerTask.Build)
                {
                    continue;
                }

                Position p = world.Positions.Get(w);
                long score = AiExecutorContext.Distance(p.X, p.Y, x, y) + (context.IsIdleWorker(w) ? 0 : 64L * MapState.UnitsPerTile);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = w;
                }
            }

            if (best.IsNone)
            {
                return;
            }

            context.Selection.Add(best);
        }
    }

    private static void StaffSites(AiExecutorContext context)
    {
        int staffed = 0;
        foreach (EntityId site in context.Player.Sites)
        {
            if (staffed >= 2 || context.CommandsLeft <= 0)
            {
                return;
            }

            if (context.Census.WorkersOn(site.Index) > 0)
            {
                continue;
            }

            Position p = context.World.Positions.Get(site);
            ushort typeId = context.World.Identities.Get(site).TypeId;
            PickBuilders(context, typeId, p.X, p.Y, 1);
            if (context.Selection.Count > 0 && context.Issue(CommandKind.Build, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(context.Selection), target: site))
            {
                staffed++;
            }
        }
    }

    private static void StartSites(AiExecutorContext context)
    {
        World world = context.World;
        AiCensusPlayer player = context.Player;
        int maxSites = 2 + (player.Workers.Count / 15);
        if (player.Sites.Count >= maxSites)
        {
            return;
        }

        foreach (AiGoal goal in context.Goals)
        {
            if (context.CommandsLeft <= 0)
            {
                return;
            }

            AiSymbol symbol = context.SymbolOf(goal);
            if (goal.Kind != AiGoalKind.Object || symbol.OwnType < 0)
            {
                continue;
            }

            int deficit = context.TargetOf(goal) - player.Completed[symbol.Id] - context.InProgress(symbol.Id);
            if (deficit <= 0)
            {
                continue;
            }

            // The goal's own building, or for a goal its tree blocks the first required building it lacks (a stable
            // for knights; UNVERIFIED: the original scripts usually ask for it first).
            ushort typeId = (ushort)symbol.OwnType;
            if (!PlacementQueries.RequirementsMet(world, context.Slot, typeId))
            {
                int missing = MissingBuilding(context, world.Ruleset.EntityTypes[typeId]);
                if (missing < 0)
                {
                    continue;
                }

                typeId = (ushort)missing;
            }
            else if (!context.Model.IsBuildingClass(symbol.Id))
            {
                continue; // a unit the production executor trains
            }

            EntityTypeDefinition type = world.Ruleset.EntityTypes[typeId];
            int cls = context.Model.ClassOf(typeId);
            if (cls < 0 || !type.HasFootprint || type.Traits.BuildTimeMs <= 0 || !context.FirstAttempt(cls, 0))
            {
                continue; // not buildable, or already tried this run (a goal repeated by many rules counts once)
            }

            if (!context.CanAfford(type.Traits.Cost, goal.LockedResources) || !PlacementQueries.RequirementsMet(world, context.Slot, typeId))
            {
                continue;
            }

            if (TryStart(context, typeId))
            {
                return; // one new site per run
            }

            if (context.PlacementSearchesLeft <= 0)
            {
                return;
            }
        }
    }

    // The first building type the type's requirements name that the player has neither completed nor under construction,
    // as the civilization's own type of that class, or -1.
    private static int MissingBuilding(AiExecutorContext context, EntityTypeDefinition type)
    {
        World world = context.World;
        string civ = world.PlayerCivilization(context.Slot);
        foreach (ushort required in type.Traits.RequiredTypes)
        {
            if (Lacks(context, required, civ) is >= 0 and var t)
            {
                return t;
            }
        }

        foreach (RequirementSet set in type.Traits.Requirements)
        {
            if (!set.AppliesTo(civ) || set.Unsatisfiable)
            {
                continue;
            }

            foreach (ushort required in set.Types)
            {
                if (Lacks(context, required, civ) is >= 0 and var t)
                {
                    return t;
                }
            }
        }

        return -1;
    }

    private static int Lacks(AiExecutorContext context, ushort required, string civ)
    {
        int cls = context.Model.ClassOf(required);
        if (cls < 0 || context.Player.Completed[cls] > 0 || context.InProgress(cls) > 0)
        {
            return -1;
        }

        // Build the civilization's own type of the class (the requirement may name another civilization's).
        foreach (ushort t in context.Model.TypesOf(cls))
        {
            if (context.Model.IsOfCivilization(t, civ) && !context.Model.BuildersOf(t).IsEmpty)
            {
                return t;
            }
        }

        return -1;
    }

    internal static bool TryStart(AiExecutorContext context, ushort typeId)
    {
        World world = context.World;
        EntityTypeDefinition type = world.Ruleset.EntityTypes[typeId];
        (int ax, int ay, int radius) = AiBuildPlacement.Anchor(context, typeId, AiBuildPlacement.RoleOf(type));
        if (!context.TakePlacementSearch() || !AiBuildPlacement.TryFind(world, typeId, ax, ay, radius, out int tx, out int ty))
        {
            return false;
        }

        int x = (tx * MapState.UnitsPerTile) + (type.FootprintWidth * MapState.UnitsPerTile / 2);
        int y = (ty * MapState.UnitsPerTile) + (type.FootprintHeight * MapState.UnitsPerTile / 2);
        PickBuilders(context, typeId, x, y, BuildersFor(type));
        if (context.Selection.Count == 0)
        {
            return false;
        }

        if (!context.Issue(CommandKind.Build, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(context.Selection), tx * MapState.UnitsPerTile, ty * MapState.UnitsPerTile, arg0: typeId))
        {
            return false;
        }

        context.Spend(type.Traits.Cost);
        if (context.Model.ClassOf(typeId) is >= 0 and var cls)
        {
            context.AddInProgress(cls, 1);
        }

        return true;
    }
}
