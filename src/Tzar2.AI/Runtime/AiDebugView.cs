using System;
using System.Globalization;
using System.Text;
using Tzar2.AI.Legacy.Program;
using Tzar2.Sim;

namespace Tzar2.AI.Runtime;

/// <summary>
/// Text for the AI debug overlay and tools (Appendix D.6): per AI player the set flags, the goals of the last pass with
/// their rule locations, the pass position and the wave. Presentation only; never part of the sim.
/// </summary>
public static class AiDebugView
{
    /// <summary>Describes one AI player.</summary>
    public static string Describe(World world, LegacyAiSystem ai, int index)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(ai);
        AiPlayerState state = ai.Players[index];
        AiProgram program = state.Settings.Program;
        var text = new StringBuilder();
        CultureInfo inv = CultureInfo.InvariantCulture;
        ref readonly PlayerState player = ref world.PlayersView[state.Settings.Slot];
        text.Append(inv, $"AI slot {state.Settings.Slot} ({program.Root}, {state.Settings.Civilization}, {state.Settings.Difficulty}): passes {state.PassCount}, {(state.InPass ? $"in pass at rule {state.Pc}" : "idle")}, waves {state.Waves}, resign {state.ResignRequested}\n");
        text.Append(inv, $"  population {player.Population}/{player.PopulationCap} (limit {player.PopulationLimit}); stock");
        for (int r = 0; r < world.Ruleset.ResourceTypes.Count; r++)
        {
            text.Append(inv, $" {world.Ruleset.ResourceTypes[r]} {player.Resources[r]}");
        }

        // Workers by task.
        Span<int> gathering = stackalloc int[Content.Compiled.Ruleset.MaxResourceTypes];
        int building = 0;
        int idle = 0;
        ReadOnlySpan<Core.Ids.EntityId> workers = world.Workers.Entities;
        for (int i = 0; i < workers.Length; i++)
        {
            if (world.Identities.Get(workers[i]).Owner != state.Settings.Slot)
            {
                continue;
            }

            Sim.Components.Worker w = world.Workers.View[i];
            switch (w.Task)
            {
                case Sim.Components.WorkerTask.Harvest when w.NodeResource < gathering.Length: gathering[w.NodeResource]++; break;
                case Sim.Components.WorkerTask.Build: building++; break;
                default: idle++; break;
            }
        }

        text.Append(inv, $"\n  workers: building {building}, idle {idle}, gathering");
        for (int r = 0; r < world.Ruleset.ResourceTypes.Count; r++)
        {
            text.Append(inv, $" {world.Ruleset.ResourceTypes[r]} {gathering[r]}");
        }

        text.Append("\n  flags:");
        for (int f = 0; f < program.Flags.Length; f++)
        {
            if (state.IsFlagSet(f))
            {
                text.Append(' ').Append(program.Flags[f]);
            }
        }

        text.Append("\n  goals:\n");
        foreach (AiGoal goal in state.Goals)
        {
            string number = goal.Number < 0 ? string.Empty : string.Create(inv, $"{goal.Number}{(goal.Percent ? "%" : string.Empty)} ");
            string locked = goal.LockedResources == 0 ? string.Empty : string.Create(inv, $" locked 0x{goal.LockedResources:X2}");
            text.Append(inv, $"    {goal.Kind} {number}{program.Symbols[goal.Symbol]}{locked}  ({program.RuleLocations[goal.Rule]})\n");
        }

        return text.ToString();
    }
}
