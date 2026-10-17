using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using RS = Godot.RenderingServer;

namespace Tzar2.Spikes.S1;

/// <summary>
/// Entry point of spike S1. Modes (user arguments after <c>--</c>):
/// <list type="bullet">
/// <item><c>--bench</c>: run the benchmark matrix, print <c>S1RESULT</c> lines, append CSV to <c>--out</c>, quit.</item>
/// <item><c>--verify</c>: render fixed overlapping scenes and compare every pixel with a CPU reference
/// (pixel exactness + y-order), print <c>S1VERIFY</c> lines, quit with exit code 1 on any mismatch.</item>
/// <item>default: interactive view (10k sprites) with an FPS readout.</item>
/// </list>
/// </summary>
public partial class Bench : Node
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Dictionary<string, string> _args = new(StringComparer.Ordinal);
    private SpriteSheet _sheet = null!;
    private Vector2I _view;
    private int _scale;

    public override void _Ready()
    {
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            string s = a.TrimStart('-');
            int eq = s.IndexOf('=', StringComparison.Ordinal);
            _args[eq < 0 ? s : s[..eq]] = eq < 0 ? "true" : s[(eq + 1)..];
        }

        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;
        if (_args.TryGetValue("size", out string? size))
        {
            string[] p = size.Split('x');
            DisplayServer.WindowSetSize(new Vector2I(int.Parse(p[0], Inv), int.Parse(p[1], Inv)));
        }

        _sheet = new SpriteSheet();
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        int exit = 0;
        try
        {
            // Let the window settle to its final size before reading the viewport size.
            await Frames(10);
            _view = (Vector2I)GetViewport().GetVisibleRect().Size;
            _scale = _args.TryGetValue("scale", out string? sc) ? int.Parse(sc, Inv) : Math.Max(1, _view.Y / 600);
            RS.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
            Log("S1INFO " + EnvInfo());

            if (_args.ContainsKey("verify"))
            {
                exit = await VerifyAsync();
            }
            else if (_args.ContainsKey("bench"))
            {
                await BenchAsync();
            }
            else
            {
                await InteractiveAsync();
            }
        }
        catch (Exception ex)
        {
            Log("S1ERROR " + ex);
            exit = 2;
        }

        GetTree().Quit(exit);
    }

    private string EnvInfo()
        => string.Create(Inv, $"os={OS.GetName()} cpu=\"{OS.GetProcessorName()}\" gpu=\"{RS.GetVideoAdapterName()}\" ")
            + string.Create(Inv, $"method={RS.GetCurrentRenderingMethod()} driver={RS.GetCurrentRenderingDriverName()} ")
            + string.Create(Inv, $"window={DisplayServer.WindowGetSize().X}x{DisplayServer.WindowGetSize().Y} ")
            + string.Create(Inv, $"viewport={_view.X}x{_view.Y} scale={_scale} godot={Engine.GetVersionInfo()["string"]}");

    // ---------------------------------------------------------------- benchmark

    private async Task BenchAsync()
    {
        int[] counts = Arg("counts", "1000,5000,10000,20000").Split(',').Select(c => int.Parse(c, Inv)).ToArray();
        string[] modes = Arg("modes", "cpu:array,cpu:atlas,depth:array,depth:atlas").Split(',');
        ShadowMode shadows = Enum.Parse<ShadowMode>(Arg("shadows", "direct"), true);
        double warmup = double.Parse(Arg("warmup", "1.5"), Inv);
        double seconds = double.Parse(Arg("seconds", "4"), Inv);
        string? outPath = _args.GetValueOrDefault("out");
        string? shot = _args.GetValueOrDefault("screenshot");
        int shotCount = int.Parse(Arg("shotcount", "10000"), Inv);

        foreach (string mode in modes)
        {
            string[] mp = mode.Split(':');
            bool depth = mp[0] == "depth";
            bool atlas = mp.Length > 1 && mp[1] == "atlas";
            foreach (int n in counts)
            {
                var entities = new Entities(n, _view.X / (float)_scale, _view.Y / (float)_scale, 0xC0FFEEu);
                using SpriteBatch batch = CreateBatch(depth, new BatchSettings(atlas, shadows, true, _scale, _view, n));
                string result = await MeasureAsync(batch, entities, warmup, seconds);
                Log(result);
                if (outPath is not null)
                {
                    AppendCsv(outPath, result);
                }

                if (shot is not null && n == shotCount && !depth && !atlas)
                {
                    await SaveScreenshotAsync(shot);
                    shot = null;
                }
            }
        }
    }

    private SpriteBatch CreateBatch(bool depth, BatchSettings s)
        => depth ? new SpriteBatch3D(this, s, _sheet) : new SpriteBatch2D(this, s, _sheet);

    private async Task<string> MeasureAsync(SpriteBatch batch, Entities e, double warmup, double seconds)
    {
        var frameMs = new List<double>(4096);
        var stepMs = new List<double>(4096);
        double update = 0, order = 0, fill = 0, upload = 0, gpu = 0, cpuRender = 0;
        int frames = 0;
        bool sortedOk = true;
        Rid vp = GetViewport().GetViewportRid();
        ulong start = Time.GetTicksUsec();
        ulong last = start;
        bool measuring = false;
        long gcBefore = 0;

        while (true)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            ulong now = Time.GetTicksUsec();
            double elapsed = (now - start) / 1e6;
            if (!measuring && elapsed >= warmup)
            {
                measuring = true;
                start = now;
                elapsed = 0;
                gcBefore = GC.GetAllocatedBytesForCurrentThread();
            }
            else if (measuring)
            {
                frameMs.Add((now - last) / 1000.0);
                gpu += RS.ViewportGetMeasuredRenderTimeGpu(vp);
                cpuRender += RS.ViewportGetMeasuredRenderTimeCpu(vp);
            }

            last = now;
            if (measuring && elapsed >= seconds)
            {
                break;
            }

            float dt = (float)Math.Min(GetProcessDeltaTime(), 0.1);
            long t0 = Stopwatch.GetTimestamp();
            e.Update(dt, true);
            long t1 = Stopwatch.GetTimestamp();
            batch.Order(e);
            long t2 = Stopwatch.GetTimestamp();
            batch.Fill(e);
            long t3 = Stopwatch.GetTimestamp();
            batch.Upload(e.Count);
            long t4 = Stopwatch.GetTimestamp();

            if (measuring)
            {
                frames++;
                update += Ms(t0, t1);
                order += Ms(t1, t2);
                fill += Ms(t2, t3);
                upload += Ms(t3, t4);
                stepMs.Add(Ms(t1, t4));
                if (batch is SpriteBatch2D && frames % 60 == 0)
                {
                    sortedOk &= e.IsSorted();
                }
            }
        }

        long gcBytes = GC.GetAllocatedBytesForCurrentThread() - gcBefore;
        int m = Math.Max(1, frameMs.Count);
        double ftMean = frameMs.Average();
        int f = Math.Max(1, frames);
        return "S1RESULT " + string.Create(Inv,
            $"method={RS.GetCurrentRenderingMethod()} driver={RS.GetCurrentRenderingDriverName()} res={_view.X}x{_view.Y} scale={_scale} ")
            + string.Create(Inv, $"sort={batch.SortName} tex={(batch.Settings.Atlas ? "atlas" : "array")} shadows={batch.ShadowName} n={e.Count} frames={frameMs.Count} ")
            + string.Create(Inv, $"fps={1000.0 / ftMean:F1} ft_mean_ms={ftMean:F3} ft_p50_ms={Pct(frameMs, 0.50):F3} ft_p99_ms={Pct(frameMs, 0.99):F3} ")
            + string.Create(Inv, $"gpu_ms={gpu / m:F3} render_cpu_ms={cpuRender / m:F3} ")
            + string.Create(Inv, $"update_ms={update / f:F3} sort_ms={order / f:F3} fill_ms={fill / f:F3} upload_ms={upload / f:F3} ")
            + string.Create(Inv, $"sortupload_ms={stepMs.Average():F3} sortupload_p99_ms={Pct(stepMs, 0.99):F3} sorted_ok={sortedOk} gc_bytes_per_frame={gcBytes / f}");
    }

    private static double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;

    private static double Pct(List<double> v, double p)
    {
        if (v.Count == 0)
        {
            return 0;
        }

        double[] s = [.. v];
        Array.Sort(s);
        return s[Math.Clamp((int)Math.Ceiling(p * s.Length) - 1, 0, s.Length - 1)];
    }

    private static void AppendCsv(string path, string resultLine)
    {
        var kv = resultLine["S1RESULT ".Length..].Split(' ').Select(t => t.Split('=', 2)).ToList();
        bool header = !File.Exists(path);
        var sb = new StringBuilder();
        if (header)
        {
            sb.Append("timestamp,os,gpu,").AppendJoin(',', kv.Select(p => p[0])).Append('\n');
        }

        sb.Append(DateTime.UtcNow.ToString("o", Inv)).Append(',').Append(OS.GetName()).Append(",\"")
            .Append(RS.GetVideoAdapterName()).Append("\",").AppendJoin(',', kv.Select(p => p[1])).Append('\n');
        File.AppendAllText(path, sb.ToString());
    }

    private async Task SaveScreenshotAsync(string path)
    {
        await ToSignal(RS.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        Image img = GetViewport().GetTexture().GetImage();
        Error err = img.SavePng(path);
        Log(string.Create(Inv, $"S1SHOT path={path} size={img.GetWidth()}x{img.GetHeight()} result={err}"));
    }

    // ---------------------------------------------------------------- verification

    /// <summary>
    /// Renders 80 overlapping, stationary sprites (unique foot lines, varied kinds, directions and players) without
    /// ground or shadows and compares the whole frame with a CPU reference composited in painter's order.
    /// </summary>
    private async Task<int> VerifyAsync()
    {
        string[] modes = Arg("modes", "cpu:array,cpu:atlas,depth:array,depth:atlas").Split(',');
        int[] scales = Arg("scales", "1,2,3").Split(',').Select(c => int.Parse(c, Inv)).ToArray();
        string? dir = _args.GetValueOrDefault("shotdir");
        int failures = 0;
        foreach (int k in scales)
        {
            foreach (string mode in modes)
            {
                string[] mp = mode.Split(':');
                bool depth = mp[0] == "depth";
                bool atlas = mp.Length > 1 && mp[1] == "atlas";
                Entities e = VerifyScene(k);
                using SpriteBatch batch = CreateBatch(depth, new BatchSettings(atlas, ShadowMode.Off, false, k, _view, e.Count));
                batch.Order(e);
                e.Sort();
                batch.Fill(e);
                batch.Upload(e.Count);
                await Frames(3);
                await ToSignal(RS.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                Image img = GetViewport().GetTexture().GetImage();
                img.Convert(Image.Format.Rgba8);
                (int mismatches, int maxDiff) = Compare(img, e, k);
                bool ok = mismatches == 0;
                failures += ok ? 0 : 1;
                string tex = atlas ? "atlas" : "array";
                Log("S1VERIFY " + string.Create(Inv,
                    $"method={RS.GetCurrentRenderingMethod()} driver={RS.GetCurrentRenderingDriverName()} scale={k} sort={batch.SortName} tex={tex} ")
                    + string.Create(Inv, $"pixels={img.GetWidth() * img.GetHeight()} mismatches={mismatches} max_channel_diff={maxDiff} result={(ok ? "PASS" : "FAIL")}"));
                if (dir is not null)
                {
                    img.SavePng(Path.Combine(dir, string.Create(Inv, $"verify-{RS.GetCurrentRenderingMethod()}-{batch.SortName}-{tex}-x{k}.png")));
                }
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private Entities VerifyScene(int k)
    {
        const int n = 80;
        float lw = _view.X / (float)k;
        float lh = _view.Y / (float)k;
        var e = new Entities(n, lw, lh, 7u);
        uint s = 99;
        int areaW = Math.Min(260, (int)lw - SpriteSheet.Cell);
        int baseY = SpriteSheet.Cell + 10;
        for (int i = 0; i < n; i++)
        {
            s = (s * 1664525u) + 1013904223u;
            int x = SpriteSheet.Cell + (int)((s >> 8) % (uint)areaW);

            // Unique foot lines (2 px apart) in an order unrelated to the id, so the sort has to reorder.
            int y = baseY + (((i * 37) % n) * 2);
            e.Place(i, x, y, (int)((s >> 4) % SpriteSheet.Kinds), (int)((s >> 12) % SpriteSheet.Directions), i % SpriteSheet.Players);
        }

        return e;
    }

    private (int Mismatches, int MaxDiff) Compare(Image img, Entities e, int k)
    {
        int w = img.GetWidth();
        int h = img.GetHeight();
        byte[] actual = img.GetData();
        byte[] expected = new byte[w * h * 3];
        foreach (int id in e.Order)
        {
            int x0 = ((int)MathF.Round(e.X[id]) - SpriteSheet.PivotX) * k;
            int y0 = ((int)MathF.Round(e.Y[id]) - SpriteSheet.PivotY) * k;
            for (int ty = 0; ty < SpriteSheet.Cell; ty++)
            {
                for (int tx = 0; tx < SpriteSheet.Cell; tx++)
                {
                    if (!_sheet.TryGetTexel(e.Frame[id], tx, ty, e.Player[id], out byte r, out byte g, out byte b))
                    {
                        continue;
                    }

                    for (int dy = 0; dy < k; dy++)
                    {
                        for (int dx = 0; dx < k; dx++)
                        {
                            int sx = x0 + (tx * k) + dx;
                            int sy = y0 + (ty * k) + dy;
                            if (sx < 0 || sy < 0 || sx >= w || sy >= h)
                            {
                                continue;
                            }

                            int i = ((sy * w) + sx) * 3;
                            expected[i] = r;
                            expected[i + 1] = g;
                            expected[i + 2] = b;
                        }
                    }
                }
            }
        }

        int mismatches = 0;
        int maxDiff = 0;
        for (int p = 0; p < w * h; p++)
        {
            int d = Math.Max(Math.Abs(actual[p * 4] - expected[p * 3]),
                Math.Max(Math.Abs(actual[(p * 4) + 1] - expected[(p * 3) + 1]), Math.Abs(actual[(p * 4) + 2] - expected[(p * 3) + 2])));
            if (d != 0)
            {
                mismatches++;
                maxDiff = Math.Max(maxDiff, d);
            }
        }

        return (mismatches, maxDiff);
    }

    // ---------------------------------------------------------------- interactive

    private async Task InteractiveAsync()
    {
        int n = int.Parse(Arg("count", "10000"), Inv);
        bool depth = Arg("sort", "cpu") == "depth";
        bool atlas = Arg("tex", "array") == "atlas";
        ShadowMode shadows = Enum.Parse<ShadowMode>(Arg("shadows", "direct"), true);
        var e = new Entities(n, _view.X / (float)_scale, _view.Y / (float)_scale, 0xC0FFEEu);
        using SpriteBatch batch = CreateBatch(depth, new BatchSettings(atlas, shadows, true, _scale, _view, n));
        var layer = new CanvasLayer();
        var label = new Label { Position = new Vector2(8, 8) };
        layer.AddChild(label);
        AddChild(layer);
        while (!Input.IsKeyPressed(Key.Escape))
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            e.Update((float)Math.Min(GetProcessDeltaTime(), 0.1), true);
            batch.Order(e);
            batch.Fill(e);
            batch.Upload(e.Count);
            label.Text = string.Create(Inv, $"{Engine.GetFramesPerSecond():F0} FPS  {n} sprites  {batch.SortName}/{(atlas ? "atlas" : "array")}  {RS.GetCurrentRenderingMethod()}");
        }
    }

    // ---------------------------------------------------------------- helpers

    private string Arg(string name, string fallback) => _args.GetValueOrDefault(name, fallback);

    private async Task Frames(int count)
    {
        for (int i = 0; i < count; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void Log(string line)
    {
        GD.Print(line);
        Console.Out.Flush();
    }
}
