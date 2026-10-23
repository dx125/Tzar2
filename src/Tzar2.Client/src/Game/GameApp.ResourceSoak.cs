using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Godot;

namespace Tzar2.Client.Game;

/// <summary>
/// <c>--resource-soak &lt;file&gt;</c> (code review 2026-10, item 16): plays <c>--matches</c> skirmishes (the command-line
/// map and players; the computers play, the local kingdom idles) of <c>--ticks</c> ticks each at top speed, returns to
/// the main menu after each, and writes the engine's counters every two seconds to a TSV: managed heap and GC counts,
/// Godot's static memory, objects, resources, nodes, orphan nodes, video, texture and buffer memory, draw calls. A leak
/// between matches shows as counters that do not come back after a match is left (the "menu" rows after a full GC).
/// </summary>
public partial class GameApp
{
    private const string SoakHeader =
        "wall s\tphase\tmatch\tticks\tfps\theap MB\tgen0\tgen1\tgen2\tworking set MB\tstatic MB\tobjects\tresources\tnodes\torphans\tvideo MB\ttexture MB\tbuffer MB\tdraw calls\tobjects drawn";

    private async Task ResourceSoakAsync(string path)
    {
        int exit = 1;
        var wall = Stopwatch.StartNew();
        using var output = new StreamWriter(path) { NewLine = "\n" };
        output.WriteLine(SoakHeader);
        try
        {
            for (int match = 1; match <= _args.Matches; match++)
            {
                if (match > 1)
                {
                    StartSkirmish(_skirmish);
                }

                var watch = Stopwatch.StartNew();
                while (Game is null && watch.Elapsed.TotalSeconds < 120)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }

                GameScreen game = Game ?? throw new InvalidOperationException("No game screen.");
                Sample(output, wall, "loaded", match, 0);
                game.Session.SpeedIndex = Tzar2.Game.Session.GameSession.SpeedSteps.Length - 1;
                double next = wall.Elapsed.TotalSeconds + 2;
                while (Game == game && game.Session.TicksRun < _args.Ticks && game.Session.Fault is null)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    if (wall.Elapsed.TotalSeconds >= next)
                    {
                        next += 2;
                        Sample(output, wall, "match", match, game.Session.TicksRun);
                    }
                }

                if (game.Session.Fault is { } fault)
                {
                    throw new InvalidOperationException("The match faulted: " + fault.Message, fault);
                }

                Sample(output, wall, "end", match, game.Session.TicksRun);
                ShowMainMenu();
                for (int frame = 0; frame < 120; frame++)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                for (int frame = 0; frame < 10; frame++)
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }

                Sample(output, wall, "menu", match, 0);
            }

            GD.Print($"RESOURCE SOAK OK matches={_args.Matches} ticks={_args.Ticks} file={path}");
            exit = 0;
        }
        catch (InvalidOperationException e)
        {
            GD.PrintErr("RESOURCE SOAK FAILED: " + e.Message);
        }

        output.Flush();
        GetTree().Quit(exit);
    }

    private static void Sample(StreamWriter output, Stopwatch wall, string phase, int match, int ticks)
    {
        static double Monitor(Performance.Monitor m) => Performance.GetMonitor(m);
        const double Mb = 1024.0 * 1024.0;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{wall.Elapsed.TotalSeconds:F1}\t{phase}\t{match}\t{ticks}\t{Monitor(Performance.Monitor.TimeFps):F0}\t{GC.GetTotalMemory(false) / Mb:F1}\t{GC.CollectionCount(0)}\t{GC.CollectionCount(1)}\t{GC.CollectionCount(2)}\t{System.Environment.WorkingSet / Mb:F1}\t{Monitor(Performance.Monitor.MemoryStatic) / Mb:F1}\t{Monitor(Performance.Monitor.ObjectCount):F0}\t{Monitor(Performance.Monitor.ObjectResourceCount):F0}\t{Monitor(Performance.Monitor.ObjectNodeCount):F0}\t{Monitor(Performance.Monitor.ObjectOrphanNodeCount):F0}\t{Monitor(Performance.Monitor.RenderVideoMemUsed) / Mb:F1}\t{Monitor(Performance.Monitor.RenderTextureMemUsed) / Mb:F1}\t{Monitor(Performance.Monitor.RenderBufferMemUsed) / Mb:F1}\t{Monitor(Performance.Monitor.RenderTotalDrawCallsInFrame):F0}\t{Monitor(Performance.Monitor.RenderTotalObjectsInFrame):F0}"));
        output.Flush();
    }
}
