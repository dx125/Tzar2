using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Tzar2.Game.Diagnostics;
using Tzar2.Game.Session;

namespace Tzar2.Client.Game;

/// <summary>
/// The S2 render bench (<c>--bench-render</c>, ADR 0244): builds <see cref="RenderBench"/>'s match, renders it with V-Sync
/// off while the camera tours the kingdoms' castles, and prints one <c>RENDER-BENCH</c> line (frame rate, frame-time
/// percentiles, sim tick percentiles, memory, load time, the S2 verdict), also appended to
/// <c>user://bench/render-bench.txt</c>. Headless it runs on the dummy renderer (a check that it works; S2=n/a).
/// </summary>
public partial class GameApp
{
    /// <summary>Seconds of warm-up before the measurement (shaders, caches, the AI's opening).</summary>
    public const int BenchWarmupSeconds = 15;

    /// <summary>The line's first word when the bench could not run.</summary>
    public const string BenchFailed = "RENDER-BENCH FAILED";

    private async Task RenderBenchAsync()
    {
        int exit = 1;
        try
        {
            if (_rules is null || RenderBench.Setup(_rules, _ai) is not { } setup)
            {
                throw new InvalidOperationException("the classic ruleset does not compile from this package, or it has no castles");
            }

            ApplyBenchWindow();
            ShowLoading("the S2 bench map");
            await AfterFrames(2, () => { });
            var load = Stopwatch.StartNew();
            var session = new GameSession(setup, _rules);
            session.SpeedIndex = Array.IndexOf(GameSession.SpeedSteps, 100);
            var phases = new PhaseTimes();
            session.Phases = phases;
            var screen = new GameScreen(_store, _ui!, session, "grass", _args.SoundFont, _settings) { KeepSpeed = true };
            Replace(screen, music: null);
            while (screen.Frame is null && load.Elapsed.TotalSeconds < 300 && session.Fault is null)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            if (screen.Frame is null)
            {
                throw new InvalidOperationException(session.Fault?.Message ?? "the match did not start within 300 s");
            }

            double loadSeconds = load.Elapsed.TotalSeconds;
            screen.EdgeScroll = false;
            screen.Scripted = true;
            List<(int X, int Y)> route = Castles(screen.Frame);

            // Warm-up, then the measurement; the camera moves the whole time.
            var clock = Stopwatch.StartNew();
            await Run(screen, route, clock, BenchWarmupSeconds, null);
            _ = session.TickTimes.Drain();
            phases.Reset();
            Vector2I size = DisplayServer.WindowGetSize();
            var result = new RenderBenchResult
            {
                Width = size.X,
                Height = size.Y,
                Renderer = DisplayServer.GetName() == "headless"
                    ? "dummy/headless"
                    : RenderingServer.GetCurrentRenderingMethod() + "/" + RenderingServer.GetCurrentRenderingDriverName(),
                Gpu = RenderingServer.GetVideoAdapterName(),
                Cpu = $"{OS.GetProcessorName()} ({OS.GetProcessorCount()} threads)",
                LoadSeconds = loadSeconds,
                Units = MobileUnits(screen.Frame),
            };
            await Run(screen, route, clock, _args.BenchSeconds, result);
            result.AddTicks(session.TickTimes.Drain());
            result.Phases = phases.Field();
            if (session.Fault is { } fault)
            {
                throw new InvalidOperationException("the simulation faulted: " + fault.Message);
            }

            string line = result.Line();
            GD.Print(line);
            ClientLog.Info(line);
            try
            {
                string folder = ProjectSettings.GlobalizePath("user://bench");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "render-bench.txt"), line + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                GD.PrintErr("render bench: the result file was not written: " + e.Message);
            }

            exit = result.FrameCount > 0 ? 0 : 1;
        }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException or ArgumentException)
        {
            GD.PrintErr($"{BenchFailed}: {e.Message}");
            ClientLog.Error("render bench failed: " + e);
        }

        if (_current is not null)
        {
            RemoveChild(_current);
            _current.QueueFree();
            _current = null;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetTree().Quit(exit);
    }

    // The window of the bench: the requested size (default 2560 × 1440) or exclusive fullscreen; V-Sync and the frame
    // cap off, so the frame rate is what the machine can draw.
    private void ApplyBenchWindow()
    {
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;
        Window root = GetTree().Root;
        if (_args.Fullscreen)
        {
            root.Mode = Window.ModeEnum.ExclusiveFullscreen;
        }
        else
        {
            root.Mode = Window.ModeEnum.Windowed;
            root.Size = new Vector2I(_args.Resolution.Width, _args.Resolution.Height);
        }

        Relayout();
    }

    // Renders for `seconds`, moving the camera along the castles (one leg per 8 s); records frames when `result` is set.
    private async Task Run(GameScreen screen, List<(int X, int Y)> route, Stopwatch clock, int seconds, RenderBenchResult? result)
    {
        double end = clock.Elapsed.TotalSeconds + seconds;
        long last = Stopwatch.GetTimestamp();
        double nextSample = 0;
        while (clock.Elapsed.TotalSeconds < end && screen.Session.Fault is null)
        {
            double t = clock.Elapsed.TotalSeconds / 8.0;
            if (route.Count > 0)
            {
                (int ax, int ay) = route[(int)t % route.Count];
                (int bx, int by) = route[((int)t + 1) % route.Count];
                double f = t - Math.Floor(t);
                screen.CenterOn((int)(ax + ((bx - ax) * f)), (int)(ay + ((by - ay) * f)));
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            long now = Stopwatch.GetTimestamp();
            if (result is not null)
            {
                result.AddFrame(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
                if (clock.Elapsed.TotalSeconds >= nextSample)
                {
                    nextSample = clock.Elapsed.TotalSeconds + 1;
                    result.SampleMemory(System.Environment.WorkingSet, (long)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed));
                }
            }

            last = now;
        }
    }

    private List<(int X, int Y)> Castles(GameFrame frame)
    {
        var castles = new SortedDictionary<int, (int X, int Y)>();
        foreach (FrameEntity e in frame.Entities)
        {
            if (!castles.ContainsKey(e.Owner) && _rules!.EntityTypes[e.TypeId].Key.EndsWith(".castle", StringComparison.Ordinal))
            {
                castles[e.Owner] = (e.X, e.Y);
            }
        }

        return [.. castles.Values];
    }

    private int MobileUnits(GameFrame frame)
    {
        int n = 0;
        foreach (FrameEntity e in frame.Entities)
        {
            if (_rules!.EntityTypes[e.TypeId].IsMobile)
            {
                n++;
            }
        }

        return n;
    }
}
