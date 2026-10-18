using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;

namespace Tzar2.Tools.Cli.Fidelity.Capture;

/// <summary>Outcome of one trial.</summary>
internal sealed record TrialResult(string Scenario, string Trial, string Status, int StepsOk, int StepsFailed, int StepsSkipped, int Captures, string? Error);

/// <summary>
/// Runs scenarios against the game (ADR 0030): per trial it applies the settings, launches the game, waits for the
/// window and the map, executes the steps (input, captures, memory probes), closes the game and writes
/// <c>scenarios/&lt;id&gt;/&lt;trial&gt;/trial.json</c>. A failing step is logged and the trial continues; a crashed
/// or hung game ends the trial, and the session moves on to the next one.
/// </summary>
internal sealed class CaptureRunner
{
    private readonly SessionConfig _session;
    private readonly IGameEnvironment _environment;
    private readonly Func<IGameDriver> _driverFactory;
    private readonly string _sessionDir;
    private readonly CaptureLog _log;
    private readonly Action<TimeSpan> _sleep;
    private readonly TimeProvider _time;

    /// <param name="time">The clock for every timestamp and deadline; tests pass a virtual one and a <paramref name="sleep"/> that advances it.</param>
    public CaptureRunner(SessionConfig session, IGameEnvironment environment, Func<IGameDriver> driverFactory, string sessionDir, CaptureLog log,
        Action<TimeSpan>? sleep = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _session = session;
        _environment = environment;
        _driverFactory = driverFactory;
        _sessionDir = sessionDir;
        _log = log;
        _sleep = sleep ?? Thread.Sleep;
    }

    /// <summary>Set when the operator pressed Ctrl+C: the current step finishes, the game closes, nothing else runs.</summary>
    public CancellationToken Cancellation { get; init; }

    public IReadOnlyList<TrialResult> Run(IReadOnlyList<ScenarioSpec> scenarios)
    {
        var results = new List<TrialResult>();
        int total = scenarios.Sum(s => s.Trials.Count);
        int done = 0;
        foreach (ScenarioSpec scenario in scenarios)
        {
            string scenarioDir = Path.Combine(_sessionDir, "scenarios", scenario.Id);
            Directory.CreateDirectory(scenarioDir);
            File.WriteAllText(Path.Combine(scenarioDir, "scenario.yaml"), scenario.SourceText);
            foreach (TrialSpec trial in scenario.Trials)
            {
                if (Cancellation.IsCancellationRequested)
                {
                    results.Add(new TrialResult(scenario.Id, trial.Name, "cancelled", 0, 0, scenario.Steps.Count, 0, "cancelled by the operator"));
                    continue;
                }

                done++;
                _log.Info($"[{done}/{total}] {scenario.Id} / {trial.Name}: {scenario.Title}");
                TrialResult result;
                try
                {
                    result = RunTrial(scenario, trial, Path.Combine(scenarioDir, trial.Name));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log.Error($"{scenario.Id} / {trial.Name} aborted: {ex.Message}");
                    _log.Detail(ex.ToString());
                    result = new TrialResult(scenario.Id, trial.Name, "error", 0, 0, 0, 0, ex.Message);
                }

                _log.Info($"    -> {result.Status}: {result.StepsOk} steps ok, {result.StepsFailed} failed, {result.StepsSkipped} skipped, {result.Captures} captures");
                results.Add(result);
            }
        }

        return results;
    }

    private TrialResult RunTrial(ScenarioSpec scenario, TrialSpec trial, string trialDir)
    {
        Directory.CreateDirectory(trialDir);
        var vars = new Dictionary<string, string>(_session.Vars, StringComparer.Ordinal)
        {
            ["scenario"] = scenario.Id,
            ["trial"] = trial.Name,
        };
        foreach ((string k, string v) in trial.Vars)
        {
            vars[k] = v;
        }

        var ini = Merge(_session.Ini, scenario.Ini, trial.Ini, vars);
        var wrapper = Merge(_session.Wrapper, scenario.Wrapper, trial.Wrapper, vars);
        // An argument that expands to nothing (an empty variable) is dropped, so trials can switch flags on and off.
        var launch = scenario.Launch.Select(a => ScenarioLoader.Expand(a, vars)).Where(a => a.Length > 0).ToList();
        var record = new TrialRecord(scenario.Id, trial.Name, vars, ini, wrapper, launch);
        long epoch = _time.GetTimestamp();
        record.Epoch = epoch;

        _environment.ApplySettings(ini, wrapper);
        using IGameDriver driver = _driverFactory();
        var samplers = new List<(Task<int> Task, CancellationTokenSource Cancel, string Name)>();
        string status = "ok";
        string? error = null;
        try
        {
            _log.Detail($"launch {_environment.ExePath} {string.Join(' ', launch)}");
            driver.Launch(_environment.ExePath, _environment.WorkingDirectory, launch);
            if (!driver.WaitForWindow(TimeSpan.FromSeconds(_session.Timing.WindowTimeoutSeconds)))
            {
                throw new TrialAbortedException(driver.HasExited
                    ? $"the game exited (code {driver.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}) before showing a window"
                    : $"no game window after {_session.Timing.WindowTimeoutSeconds} s");
            }

            driver.Focus();
            int load = scenario.LoadSeconds ?? _session.Timing.LoadSeconds;
            _log.Info($"    waiting {load} s for the map to load");
            Wait(driver, TimeSpan.FromSeconds(load));
            if (driver.HasExited)
            {
                throw new TrialAbortedException($"the game exited while loading (code {driver.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"})");
            }

            // Measured after loading: the game may switch display modes (and window sizes) on the way.
            (int w, int h) = driver.ClientSize;
            record.ClientWidth = w;
            record.ClientHeight = h;
            if (w != _session.Layout.Width || h != _session.Layout.Height)
            {
                _log.Warn($"game window client area is {w}×{h}, expected {_session.Layout.Width}×{_session.Layout.Height} (window captures may be scaled)");
            }

            driver.Focus();

            var context = new StepContext(driver, trialDir, vars, record, epoch, samplers);
            for (int i = 0; i < scenario.Steps.Count; i++)
            {
                StepSpec step = scenario.Steps[i];
                var stepRecord = new StepRecord(i, step.Line, step.Kind, Ms(epoch));
                record.Steps.Add(stepRecord);
                if (driver.HasExited || Cancellation.IsCancellationRequested)
                {
                    stepRecord.Status = "skipped";
                    continue;
                }

                try
                {
                    Execute(step, context);
                    stepRecord.Status = "ok";
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not TrialAbortedException)
                {
                    stepRecord.Status = "failed";
                    stepRecord.Error = ex.Message;
                    _log.Warn($"    step {i + 1} ({step.Kind}, line {step.Line}) failed: {ex.Message}");
                    _log.Detail(ex.ToString());
                }

                _sleep(TimeSpan.FromMilliseconds(_session.Timing.StepGapMs));
            }

            if (driver.HasExited)
            {
                status = "crashed";
                error = $"the game exited during the steps (code {driver.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"})";
                _log.Error($"    {error}");
            }
        }
        catch (TrialAbortedException ex)
        {
            status = "failed";
            error = ex.Message;
            _log.Error($"    {ex.Message}");
        }
        finally
        {
            foreach ((Task<int> task, CancellationTokenSource cancel, string name) in samplers)
            {
                // A faulted sampler (e.g. disk full) must not skip closing the game or writing trial.json.
                try
                {
                    if (!task.Wait(TimeSpan.FromSeconds(120)))
                    {
                        cancel.Cancel();
                        task.Wait(TimeSpan.FromSeconds(5));
                    }

                    record.Notes.Add($"memsample {name}: {(task.IsCompletedSuccessfully ? task.Result.ToString(CultureInfo.InvariantCulture) : "no")} samples");
                }
                catch (AggregateException ex)
                {
                    record.Notes.Add($"memsample {name}: failed ({ex.InnerException?.Message ?? ex.Message})");
                    _log.Warn($"    memsample {name} failed: {ex.InnerException?.Message ?? ex.Message}");
                }
                finally
                {
                    cancel.Dispose();
                }
            }

            record.ExitCode = driver.HasExited ? driver.ExitCode : null;
            driver.Close(TimeSpan.FromSeconds(_session.Timing.CloseTimeoutSeconds));
            record.Status = status;
            record.Error = error;
            record.Write(Path.Combine(trialDir, "trial.json"));
        }

        int ok = record.Steps.Count(s => s.Status == "ok");
        int failed = record.Steps.Count(s => s.Status == "failed");
        int skipped = scenario.Steps.Count - ok - failed;
        if (status == "ok" && failed > 0)
        {
            status = "partial";
        }

        return new TrialResult(scenario.Id, trial.Name, status, ok, failed, skipped, record.Captures.Count, error);
    }

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b,
        IReadOnlyDictionary<string, string> c, IReadOnlyDictionary<string, string> vars)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (IReadOnlyDictionary<string, string> layer in new[] { a, b, c })
        {
            foreach ((string k, string v) in layer)
            {
                result[k] = ScenarioLoader.Expand(v, vars);
            }
        }

        return result;
    }

    private void Wait(IGameDriver driver, TimeSpan duration)
    {
        long start = _time.GetTimestamp();
        while (_time.GetElapsedTime(start) < duration && !driver.HasExited && !Cancellation.IsCancellationRequested)
        {
            TimeSpan left = duration - _time.GetElapsedTime(start);
            _sleep(left < TimeSpan.FromMilliseconds(250) ? left : TimeSpan.FromMilliseconds(250));
        }
    }

    private double Ms(long epoch) => Math.Round(_time.GetElapsedTime(epoch).TotalMilliseconds, 3);

    private sealed record StepContext(IGameDriver Driver, string TrialDir, IReadOnlyDictionary<string, string> Vars, TrialRecord Record, long Epoch,
        List<(Task<int> Task, CancellationTokenSource Cancel, string Name)> Samplers)
    {
        public (double U, double V)? Camera { get; set; }

        public Dictionary<string, MemoryScanResult> Scans { get; } = new(StringComparer.Ordinal);
    }

    private void Execute(StepSpec step, StepContext ctx)
    {
        IGameDriver d = ctx.Driver;
        GameLayout layout = _session.Layout;
        switch (step)
        {
            case WaitStep wait:
                Wait(d, TimeSpan.FromSeconds(wait.Seconds));
                break;
            case NoteStep note:
                _log.Info($"    note: {ScenarioLoader.Expand(note.Text, ctx.Vars)}");
                break;
            case KeyStep key:
                for (int i = 0; i < key.Repeat; i++)
                {
                    Tap(d, KeyNames.Get(key.Key), key.Ctrl, key.Shift, key.Alt, Math.Max(key.HoldMs, _session.Timing.KeyHoldMs));
                    _sleep(TimeSpan.FromMilliseconds(_session.Timing.KeyHoldMs));
                }

                break;
            case TextStep text:
                d.Type(ScenarioLoader.Expand(text.Text, ctx.Vars));
                break;
            case ChatStep chat:
                Tap(d, KeyNames.Get("Enter"), false, false, false, _session.Timing.KeyHoldMs);
                _sleep(TimeSpan.FromMilliseconds(_session.Timing.ChatOpenMs));
                d.Type(ScenarioLoader.Expand(chat.Text, ctx.Vars));
                _sleep(TimeSpan.FromMilliseconds(100));
                Tap(d, KeyNames.Get("Enter"), false, false, false, _session.Timing.KeyHoldMs);
                _sleep(TimeSpan.FromMilliseconds(_session.Timing.ChatOpenMs));
                break;
            case MoveStep move:
                d.MouseMove(move.At);
                break;
            case ClickStep click:
                Click(d, click.At, click.Button, click.Double, click.Ctrl, click.Shift, click.Alt);
                break;
            case DragStep drag:
                d.MouseMove(drag.From);
                _sleep(TimeSpan.FromMilliseconds(60));
                d.MouseButton(drag.Button, true);
                for (int i = 1; i <= 10; i++)
                {
                    _sleep(TimeSpan.FromMilliseconds(30));
                    d.MouseMove(new ScreenPoint(drag.From.X + ((drag.To.X - drag.From.X) * i / 10), drag.From.Y + ((drag.To.Y - drag.From.Y) * i / 10)));
                }

                _sleep(TimeSpan.FromMilliseconds(60));
                d.MouseButton(drag.Button, false);
                break;
            case MinimapStep minimap:
            {
                ScreenRect m = layout.Minimap;
                var at = new ScreenPoint(
                    Math.Clamp(m.X + (int)Math.Round(minimap.U * m.Width), m.X, m.Right - 1),
                    Math.Clamp(m.Y + (int)Math.Round(minimap.V * m.Height), m.Y, m.Bottom - 1));
                Click(d, at, MouseButton.Left, false, false, false, false);
                ctx.Camera = (minimap.U, minimap.V);
                _sleep(TimeSpan.FromMilliseconds(300));
                d.MouseMove(layout.Park);
                break;
            }

            case SnapshotStep snapshot:
                Snapshot(snapshot, ctx);
                break;
            case BurstStep burst:
                Burst(burst, ctx);
                break;
            case MemScanStep scan:
                MemScan(scan, ctx);
                break;
            case MemSampleStep sample:
                MemSample(sample, ctx);
                break;
            default:
                throw new InvalidOperationException($"unsupported step {step.Kind}");
        }
    }

    private void Tap(IGameDriver d, ushort vk, bool ctrl, bool shift, bool alt, int holdMs)
    {
        Modifiers(d, ctrl, shift, alt, true);
        d.Key(vk, true);
        _sleep(TimeSpan.FromMilliseconds(holdMs));
        d.Key(vk, false);
        Modifiers(d, ctrl, shift, alt, false);
    }

    private void Modifiers(IGameDriver d, bool ctrl, bool shift, bool alt, bool down)
    {
        foreach ((bool on, string name) in new[] { (ctrl, "Ctrl"), (shift, "Shift"), (alt, "Alt") })
        {
            if (on)
            {
                d.Key(KeyNames.Get(name), down);
                _sleep(TimeSpan.FromMilliseconds(20));
            }
        }
    }

    private void Click(IGameDriver d, ScreenPoint at, MouseButton button, bool twice, bool ctrl, bool shift, bool alt)
    {
        d.MouseMove(at);
        _sleep(TimeSpan.FromMilliseconds(80));
        Modifiers(d, ctrl, shift, alt, true);
        for (int i = 0; i < (twice ? 2 : 1); i++)
        {
            d.MouseButton(button, true);
            _sleep(TimeSpan.FromMilliseconds(50));
            d.MouseButton(button, false);
            _sleep(TimeSpan.FromMilliseconds(twice ? 60 : 30));
        }

        Modifiers(d, ctrl, shift, alt, false);
    }

    private void Snapshot(SnapshotStep step, StepContext ctx)
    {
        string name = ScenarioLoader.Expand(step.Name, ctx.Vars);
        var tags = step.Tags.ToDictionary(t => t.Key, t => ScenarioLoader.Expand(t.Value, ctx.Vars), StringComparer.Ordinal);
        ctx.Driver.MouseMove(_session.Layout.Park);
        _sleep(TimeSpan.FromMilliseconds(200));
        var problems = new List<string>();
        if (step.Window)
        {
            double t = Ms(ctx.Epoch);
            CaptureImage? image = ctx.Driver.CaptureWindow();
            if (image is null)
            {
                problems.Add("window capture failed");
            }
            else
            {
                string file = Path.Combine("window", name + ".bmp");
                Save(ctx.TrialDir, file, image.EncodeBmp());
                ctx.Record.Captures.Add(new CaptureRecord("window", name, file, t, tags, ctx.Camera, image.Width, image.Height, image.Format.Name));
            }
        }

        if (step.Game)
        {
            double t = Ms(ctx.Epoch);
            string? found = GameSnapshot(ctx.Driver);
            if (found is null)
            {
                problems.Add($"no F11 snapshot appeared within {_session.Timing.SnapshotTimeoutMs} ms");
            }
            else
            {
                string file = Path.Combine("game", name + ".bmp");
                byte[] data = File.ReadAllBytes(found);
                Save(ctx.TrialDir, file, data);
                TryDelete(found);
                string format = "unreadable";
                int w = 0, h = 0;
                try
                {
                    CaptureImage image = CaptureImage.DecodeBmp(data);
                    (format, w, h) = (image.Format.Name, image.Width, image.Height);
                }
                catch (InvalidDataException ex)
                {
                    problems.Add($"F11 snapshot is not a readable BMP: {ex.Message}");
                }

                ctx.Record.Captures.Add(new CaptureRecord("game", name, file, t, tags, ctx.Camera, w, h, format));
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", problems));
        }
    }

    /// <summary>Presses F11 and waits for the game to write a new BMP into one of its snapshot folders.</summary>
    private string? GameSnapshot(IGameDriver driver)
    {
        var before = new HashSet<string>(ListSnapshots(), StringComparer.OrdinalIgnoreCase);
        long started = _time.GetTimestamp();
        Tap(driver, KeyNames.Get("F11"), false, false, false, _session.Timing.KeyHoldMs);
        var deadline = TimeSpan.FromMilliseconds(_session.Timing.SnapshotTimeoutMs);
        long lastSize = -1;
        string? candidate = null;
        while (_time.GetElapsedTime(started) < deadline)
        {
            candidate ??= ListSnapshots().FirstOrDefault(f => !before.Contains(f));
            if (candidate is not null)
            {
                long size = new FileInfo(candidate).Length;
                if (size > 0 && size == lastSize && CanOpen(candidate))
                {
                    return candidate;
                }

                lastSize = size;
            }

            _sleep(TimeSpan.FromMilliseconds(150));
        }

        return candidate is not null && CanOpen(candidate) ? candidate : null;
    }

    private IEnumerable<string> ListSnapshots() => _environment.SnapshotDirectories
        .Where(Directory.Exists)
        .SelectMany(dir => Directory.EnumerateFiles(dir, "*.bmp"))
        .ToList();

    private static bool CanOpen(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind in the work copy; harmless.
        }
    }

    private void Burst(BurstStep step, StepContext ctx)
    {
        string name = ScenarioLoader.Expand(step.Name, ctx.Vars);
        var tags = step.Tags.ToDictionary(t => t.Key, t => ScenarioLoader.Expand(t.Value, ctx.Vars), StringComparer.Ordinal);
        var frames = new List<(string File, double TimeMs)>();
        int failures = 0;
        long start = _time.GetTimestamp();
        for (int i = 0; i < step.Count && !ctx.Driver.HasExited; i++)
        {
            double t = Ms(ctx.Epoch);
            CaptureImage? image = ctx.Driver.CaptureWindow();
            if (image is null)
            {
                failures++;
            }
            else
            {
                if (step.Region is { } r)
                {
                    image = Crop(image, r);
                }

                // Bursts are for motion, not colour: store them as canonical RGB555 (half the size of 32-bit).
                image = new CaptureImage(image.Width, image.Height, PixelFormat.Rgb555, Array.ConvertAll(image.ToCanonical555(), v => (uint)v));

                string file = Path.Combine("burst", name, i.ToString("D4", CultureInfo.InvariantCulture) + ".bmp");
                Save(ctx.TrialDir, file, image.EncodeBmp());
                frames.Add((file, t));
            }

            TimeSpan next = TimeSpan.FromMilliseconds(step.IntervalMs * (i + 1)) - _time.GetElapsedTime(start);
            if (next > TimeSpan.Zero)
            {
                _sleep(next);
            }
        }

        ctx.Record.Captures.Add(new CaptureRecord("burst", name, Path.Combine("burst", name), frames.Count > 0 ? frames[0].TimeMs : 0, tags, ctx.Camera, 0, 0, "rgb555")
        {
            Frames = frames,
            Region = step.Region,
        });
        if (failures > 0)
        {
            throw new InvalidOperationException($"{failures} of {step.Count} burst frames could not be captured");
        }
    }

    internal static CaptureImage Crop(CaptureImage image, ScreenRect r)
    {
        int x0 = Math.Clamp(r.X, 0, image.Width);
        int y0 = Math.Clamp(r.Y, 0, image.Height);
        int w = Math.Clamp(r.Width, 0, image.Width - x0);
        int h = Math.Clamp(r.Height, 0, image.Height - y0);
        if (w == 0 || h == 0)
        {
            throw new InvalidOperationException("burst region is outside the window");
        }

        uint[] pixels = new uint[w * h];
        for (int y = 0; y < h; y++)
        {
            Array.Copy(image.Native, ((y0 + y) * image.Width) + x0, pixels, y * w, w);
        }

        return new CaptureImage(w, h, image.Format, pixels);
    }

    private void MemScan(MemScanStep step, StepContext ctx)
    {
        IMemoryReader reader = ctx.Driver.Memory ?? throw new InvalidOperationException("the game's memory cannot be read");
        string name = ScenarioLoader.Expand(step.Name, ctx.Vars);
        double t = Ms(ctx.Epoch);
        long? value = step.Value is null ? null : ParseValue(ScenarioLoader.Expand(step.Value, ctx.Vars));
        MemoryScanResult result = step.From is { } from
            ? MemoryProbe.Filter(reader, name, ctx.Scans[from], step.Mode, value, step.MaxResults)
            : MemoryProbe.Scan(reader, name, value!.Value, step.Width, step.MaxResults);
        ctx.Scans[step.Name] = result;
        string file = Path.Combine("mem", name + ".json");
        string path = Path.Combine(ctx.TrialDir, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = File.Create(path))
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteNumber("width", result.Width);
            w.WriteBoolean("truncated", result.Truncated);
            w.WriteNumber("bytes_scanned", result.BytesScanned);
            w.WriteNumber("regions", result.Regions);
            w.WriteNumber("count", result.Addresses.Count);
            w.WriteStartArray("addresses");
            foreach (ulong a in result.Addresses.Take(100000))
            {
                w.WriteNumberValue(a);
            }

            w.WriteEndArray();
            w.WriteStartArray("values");
            foreach (long v in result.Values.Take(100000))
            {
                w.WriteNumberValue(v);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        ctx.Record.Captures.Add(new CaptureRecord("memscan", name, file, t, new Dictionary<string, string>(), ctx.Camera, 0, 0, $"int{step.Width * 8}"));
        _log.Info($"    memscan {name}: {result.Addresses.Count} addresses{(result.Truncated ? " (truncated)" : string.Empty)}, {result.BytesScanned / (1 << 20)} MiB scanned");
    }

    private static long ParseValue(string text)
    {
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return long.TryParse(hex ? text[2..] : text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v)
            ? v
            : throw new InvalidOperationException($"memscan value '{text}' is not an integer");
    }

    private void MemSample(MemSampleStep step, StepContext ctx)
    {
        IMemoryReader reader = ctx.Driver.Memory ?? throw new InvalidOperationException("the game's memory cannot be read");
        string name = ScenarioLoader.Expand(step.Name, ctx.Vars);
        MemoryScanResult scan = ctx.Scans[step.Scan];
        var addresses = scan.Addresses.Take(step.MaxAddresses).ToList();
        if (addresses.Count == 0)
        {
            throw new InvalidOperationException($"memscan {step.Scan} found no addresses to sample");
        }

        string file = Path.Combine("mem", name + ".bin");
        string path = Path.Combine(ctx.TrialDir, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var cancel = new CancellationTokenSource();
        long epoch = ctx.Epoch;
        Task<int> task = Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            return MemoryProbe.Sample(reader, addresses, scan.Width, TimeSpan.FromMilliseconds(step.IntervalMs), TimeSpan.FromSeconds(step.DurationSeconds),
                stream, epoch, cancel.Token, _time, _time == TimeProvider.System ? null : (d, c) =>
                {
                    _sleep(d);
                    return c.IsCancellationRequested;
                });
        });
        ctx.Samplers.Add((task, cancel, name));
        ctx.Record.Captures.Add(new CaptureRecord("memsample", name, file, Ms(ctx.Epoch), new Dictionary<string, string> { ["scan"] = step.Scan }, ctx.Camera, 0, 0,
            $"int{scan.Width * 8}"));
    }

    private static void Save(string trialDir, string relative, byte[] data)
    {
        string path = Path.Combine(trialDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private sealed class TrialAbortedException(string message) : Exception(message);
}
