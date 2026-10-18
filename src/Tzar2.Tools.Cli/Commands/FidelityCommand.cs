using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Tzar2.Import.Editions;
using Tzar2.Tools.Cli.Fidelity.Analysis;
using Tzar2.Tools.Cli.Fidelity.Capture;
using Tzar2.Tools.Cli.Fidelity.Capture.Windows;
using Tzar2.Tools.Cli.Fidelity.Scenarios;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// <c>t2 fidelity</c>: the capture harness of TDD §23.2 (ADR 0030).
/// <list type="bullet">
/// <item><c>capture</c> (Windows): runs scenarios against the original game and writes one zip of raw captures.</item>
/// <item><c>analyze</c>: turns a returned zip into YAML fact files (numbers only).</item>
/// <item><c>check</c>: quick usability summary of a session (blank captures, formats).</item>
/// <item><c>scenarios</c>: validates and lists the scenario files.</item>
/// <item><c>selftest</c> (Windows): window capture, input and memory reading without the game.</item>
/// </list>
/// </summary>
internal static class FidelityCommand
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage(args.Length == 0 ? stderr : stdout);
            return args.Length == 0 ? ExitCodes.UsageError : ExitCodes.Success;
        }

        string[] rest = args[1..];
        try
        {
            return args[0] switch
            {
                "capture" => Capture(rest, stdout, stderr),
                "analyze" => Analyze(rest, stdout, stderr),
                "check" => Check(rest, stdout, stderr),
                "scenarios" => Scenarios(rest, stdout, stderr),
                "selftest" => SelfTestCommand(rest, stdout, stderr),
                "report" => Report(rest, stdout),
                _ => Unknown(args[0], stderr),
            };
        }
        catch (ScenarioException ex)
        {
            stderr.WriteLine($"t2 fidelity: {ex.Message}");
            return ExitCodes.UsageError;
        }
        catch (OptionException ex)
        {
            stderr.WriteLine($"t2 fidelity {args[0]}: {ex.Message}");
            return ExitCodes.UsageError;
        }
    }

    private static int Unknown(string sub, TextWriter stderr)
    {
        stderr.WriteLine($"t2 fidelity: unknown subcommand '{sub}' (capture, analyze, check, scenarios, selftest, report).");
        return ExitCodes.UsageError;
    }

    private static void PrintUsage(TextWriter o)
    {
        o.WriteLine("Usage: t2 fidelity <subcommand> [options]   (capture harness, TDD §23.2, ADR 0030)");
        o.WriteLine();
        o.WriteLine("  capture --tzar <install> [--scenarios id,id|all] [--out <dir>] [--work <dir>]");
        o.WriteLine("          [--renderer cnc-ddraw|existing|native] [--capture-dir <dir>] [--commit <sha>] [--allow-modified]");
        o.WriteLine("        Windows only. Runs the scenarios against the original game and writes <out>/<session>.zip.");
        o.WriteLine("  analyze <session.zip|dir> [--original <install>] [--facts <dir>] [--only id,id] [--extract <dir>]");
        o.WriteLine("        Writes one fact file per scenario (default: tests/fidelity/facts in the repository).");
        o.WriteLine("  check <session.zip|dir>         usability summary (blank captures, formats, failed trials)");
        o.WriteLine("  scenarios [--capture-dir <dir>] validate and list the scenarios with their durations");
        o.WriteLine("  selftest [--allow-no-input]     Windows only: capture, input and memory checks without the game");
        o.WriteLine("  report [--facts <dir>]          the fidelity suite (ADR 0112): pass % per area against the engine");
        o.WriteLine();
        o.WriteLine("<capture-dir> holds session.yaml and scenarios/*.yaml (default: tools/capture in the repository).");
    }

    // ---- scenarios -------------------------------------------------------------------------------------------------

    private static int Scenarios(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var o = Options.Parse(args, ["--capture-dir"], []);
        (SessionConfig session, IReadOnlyList<ScenarioSpec> scenarios) = LoadSpecs(o.Get("--capture-dir"));
        stdout.WriteLine($"Layout {session.Layout.Name} {session.Layout.Width}x{session.Layout.Height}; {scenarios.Count} scenarios:");
        foreach (ScenarioSpec s in scenarios)
        {
            stdout.WriteLine($"  {(s.Default ? "*" : " ")} {s.Id,-20} {s.Minutes,5:0.#} min  {s.Trials.Count} trial(s)  {string.Join(',', s.Resolves),-16} {s.Title}{(s.Experimental ? " [experimental]" : string.Empty)}");
        }

        double total = scenarios.Where(s => s.Default).Sum(s => s.Minutes);
        stdout.WriteLine($"* = run by default (about {total:0} minutes).");
        return ExitCodes.Success;
    }

    internal static (SessionConfig Session, IReadOnlyList<ScenarioSpec> Scenarios) LoadSpecs(string? captureDir)
    {
        string dir = captureDir ?? Path.Combine(RepositoryRoot() ?? throw new OptionException("--capture-dir is required outside the repository"), "tools", "capture");
        string sessionFile = Path.Combine(dir, "session.yaml");
        string scenarioDir = Path.Combine(dir, "scenarios");
        if (!File.Exists(sessionFile) || !Directory.Exists(scenarioDir))
        {
            throw new OptionException($"'{dir}' has no session.yaml and scenarios/ folder");
        }

        SessionConfig session = ScenarioLoader.LoadSession(sessionFile);
        IReadOnlyList<ScenarioSpec> scenarios = ScenarioLoader.LoadDirectory(scenarioDir);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScenarioSpec s in scenarios)
        {
            if (!ids.Add(s.Id))
            {
                throw new ScenarioException($"{s.FileName}: duplicate scenario id '{s.Id}'");
            }

            // Every ${var} must be defined by the session, the built-ins or every trial.
            var known = new HashSet<string>(session.Vars.Keys.Concat(["scenario", "trial"]), StringComparer.Ordinal);
            IEnumerable<string> texts = s.Launch.Concat(s.Steps.SelectMany(StepTexts)).Concat(s.Ini.Values).Concat(s.Wrapper.Values);
            foreach (string v in texts.SelectMany(ScenarioLoader.VariablesIn).Distinct(StringComparer.Ordinal))
            {
                if (!known.Contains(v) && !s.Trials.All(t => t.Vars.ContainsKey(v)))
                {
                    throw new ScenarioException($"{s.FileName}: variable '${{{v}}}' is not defined by the session or by every trial");
                }
            }
        }

        return (session, scenarios);
    }

    private static IEnumerable<string> StepTexts(StepSpec step) => step switch
    {
        ChatStep c => [c.Text],
        TextStep t => [t.Text],
        NoteStep n => [n.Text],
        SnapshotStep s => s.Tags.Values.Prepend(s.Name),
        BurstStep b => b.Tags.Values.Prepend(b.Name),
        MemScanStep m => m.Value is null ? [m.Name] : [m.Name, m.Value],
        MemSampleStep m => [m.Name],
        _ => [],
    };

    // ---- report ----------------------------------------------------------------------------------------------------

    private static int Report(string[] args, TextWriter stdout)
    {
        var o = Options.Parse(args, ["--facts"], []);
        string? root = RepositoryRoot();
        string factsDir = o.Get("--facts") ?? Path.Combine(root ?? throw new OptionException("--facts is required outside the repository"), "tests", "fidelity", "facts");
        var facts = new Fidelity.Suite.FactsIndex(factsDir);
        stdout.WriteLine($"Fidelity suite (ADR 0112): {Fidelity.Suite.FidelitySuite.Checks.Count} checks, facts from {facts.Files} file(s) in {factsDir}");
        var results = Fidelity.Suite.FidelitySuite.Evaluate(facts, Fidelity.Suite.EngineModel.Load(root));
        double? rate = Fidelity.Suite.FidelitySuite.WriteReport(results, stdout);
        stdout.WriteLine();
        stdout.WriteLine(rate is { } r ? $"Suite pass rate: {r * 100:0.#} % of the measured checks (M3 exit: >= 80 %)." : "No measured checks yet.");
        return results.Any(x => x.Result.Status == Fidelity.Suite.CheckStatus.Fail) ? ExitCodes.Mismatch : ExitCodes.Success;
    }

    // ---- analyze / check -------------------------------------------------------------------------------------------

    private static int Analyze(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var o = Options.Parse(args, ["--original", "--facts", "--only", "--extract", "--capture-dir"], [], positional: 1);
        string input = o.Positional[0];
        string? original = o.Get("--original") ?? EnvOriginal();
        string factsDir = o.Get("--facts") ?? Path.Combine(RepositoryRoot() ?? throw new OptionException("--facts is required outside the repository"), "tests", "fidelity", "facts");
        (SessionConfig session, _) = LoadSpecs(o.Get("--capture-dir"));
        using CaptureSession capture = OpenSession(input);
        if (o.Get("--extract") is { } extract)
        {
            int n = Extract(input, extract);
            stdout.WriteLine($"Extracted {n} files to {extract} (local only, never commit: REQ-LEGAL-01).");
        }

        if (original is null)
        {
            stdout.WriteLine("Note: no --original install (or TZAR2_ORIGIN); terrain, shadow and player-colour analyses will be skipped.");
        }

        var ctx = new AnalysisContext(capture, original, session.Layout);
        stdout.WriteLine($"Analysing {capture.Name}: {capture.Trials.Count} trials ...");
        HashSet<string>? only = o.Get("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<FactsFile> files = FidelityAnalyzer.Analyze(ctx, factsDir, only, stdout);
        foreach (FactsFile f in files)
        {
            stdout.WriteLine($"wrote {f.Path}: {string.Join(", ", f.Statuses.Select(s => $"{s.Key}={s.Value}"))}");
        }

        if (files.Count == 0)
        {
            stderr.WriteLine("t2 fidelity analyze: the session contains no scenarios.");
            return ExitCodes.Mismatch;
        }

        return ExitCodes.Success;
    }

    private static int Check(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var o = Options.Parse(args, [], [], positional: 1);
        using CaptureSession capture = OpenSession(o.Positional[0]);
        return PrintQuality(capture, stdout) ? ExitCodes.Success : ExitCodes.Mismatch;
    }

    /// <summary>Prints the per-trial quality table; returns whether the session has usable captures.</summary>
    internal static bool PrintQuality(CaptureSession capture, TextWriter stdout)
    {
        IReadOnlyList<CaptureQuality.ImageQuality> images = CaptureQuality.Inspect(capture, capture.Trials);
        stdout.WriteLine($"{"trial",-34} {"status",-9} {"game",5} {"window",6} {"blank",5} {"bursts",6}  formats");
        foreach (TrialData t in capture.Trials)
        {
            string id = $"{t.Scenario}/{t.Trial}";
            var mine = images.Where(i => i.Trial == id).ToList();
            string formats = string.Join(" ", mine.Where(i => i.Error is null).Select(i => $"{i.Kind}:{i.Format}").Distinct());
            stdout.WriteLine($"{id,-34} {t.Status,-9} {mine.Count(i => i.Kind == "game"),5} {mine.Count(i => i.Kind == "window"),6} {mine.Count(i => i.Blank),5} {t.Of("burst").Count(),6}  {formats}");
        }

        int good = images.Count(i => !i.Blank && i.Error is null);
        stdout.WriteLine($"{good} of {images.Count} images usable; {capture.Trials.Count(t => t.Status is "ok" or "partial")} of {capture.Trials.Count} trials ran.");
        if (images.Count > 0 && images.Where(i => i.Kind == "window").All(i => i.Blank))
        {
            stdout.WriteLine("WARNING: every window capture is blank. The game probably ran full-screen; rerun with -Renderer cnc-ddraw.");
        }

        if (images.Count > 0 && !images.Any(i => i.Kind == "game"))
        {
            stdout.WriteLine("WARNING: no F11 game snapshots were collected.");
        }

        return good > 0 || capture.Trials.Any(t => t.Of("burst").Any() || t.Of("memsample").Any());
    }

    private static CaptureSession OpenSession(string input)
    {
        if (!File.Exists(input) && !Directory.Exists(input))
        {
            throw new OptionException($"'{input}' does not exist");
        }

        try
        {
            return CaptureSession.Open(input);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new OptionException($"'{input}' is not a capture session: {ex.Message}");
        }
    }

    /// <summary>Extracts the archive (zip-slip safe) for manual inspection.</summary>
    private static int Extract(string input, string target)
    {
        if (Directory.Exists(input))
        {
            throw new OptionException("--extract needs a zip file");
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        Directory.CreateDirectory(root);
        int n = 0;
        using ZipArchive zip = ZipFile.OpenRead(input);
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            string path = Path.GetFullPath(Path.Combine(root, e.FullName));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new OptionException($"archive entry '{e.FullName}' escapes the target folder");
            }

            if (e.FullName.EndsWith('/'))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            e.ExtractToFile(path, overwrite: true);
            n++;
        }

        return n;
    }

    // ---- selftest --------------------------------------------------------------------------------------------------

    private static int SelfTestCommand(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var o = Options.Parse(args, [], ["--allow-no-input"]);
        if (!OperatingSystem.IsWindows())
        {
            stderr.WriteLine("t2 fidelity selftest: requires Windows.");
            return ExitCodes.UsageError;
        }

        return SelfTest.Run(stdout, requireInput: !o.Has("--allow-no-input")) == 0 ? ExitCodes.Success : ExitCodes.Mismatch;
    }

    // ---- capture ---------------------------------------------------------------------------------------------------

    private static int Capture(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var o = Options.Parse(args, ["--tzar", "--scenarios", "--out", "--work", "--renderer", "--capture-dir", "--commit"], ["--allow-modified"]);
        string tzar = o.Get("--tzar") ?? throw new OptionException("--tzar <install folder> is required");
        (SessionConfig session, IReadOnlyList<ScenarioSpec> all) = LoadSpecs(o.Get("--capture-dir"));
        IReadOnlyList<ScenarioSpec> selected = Select(all, o.Get("--scenarios"));
        RendererMode renderer = o.Get("--renderer") switch
        {
            null or "cnc-ddraw" => RendererMode.CncDdraw,
            "existing" => RendererMode.Existing,
            "native" => RendererMode.Native,
            { } other => throw new OptionException($"unknown renderer '{other}' (cnc-ddraw, existing, native)"),
        };
        if (!OperatingSystem.IsWindows())
        {
            stderr.WriteLine("t2 fidelity capture: requires Windows (the original game runs there). Use 'analyze' on the returned zip.");
            return ExitCodes.UsageError;
        }

        return CaptureOnWindows(tzar, session, selected, renderer, o, stdout, stderr);
    }

    internal static IReadOnlyList<ScenarioSpec> Select(IReadOnlyList<ScenarioSpec> all, string? list)
    {
        if (list is null)
        {
            return all.Where(s => s.Default).ToList();
        }

        if (list == "all")
        {
            return all;
        }

        var wanted = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<ScenarioSpec>();
        foreach (string id in wanted)
        {
            result.Add(all.FirstOrDefault(s => s.Id == id) ?? throw new OptionException($"unknown scenario '{id}' (known: {string.Join(", ", all.Select(s => s.Id))})"));
        }

        return result;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int CaptureOnWindows(string tzar, SessionConfig session, IReadOnlyList<ScenarioSpec> selected, RendererMode renderer, Options o,
        TextWriter stdout, TextWriter stderr)
    {
        string install = Path.GetFullPath(tzar);
        string? exe = WorkCopy.FindExe(install);
        if (exe is null)
        {
            stderr.WriteLine($"t2 fidelity capture: no Tzar.exe in '{install}'.");
            return ExitCodes.UsageError;
        }

        string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tzar2Capture");
        string outDir = Path.GetFullPath(o.Get("--out") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Tzar2Capture"));
        string workDir = Path.GetFullPath(o.Get("--work") ?? Path.Combine(home, "game"));
        string sessionId = "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string sessionDir = Path.Combine(outDir, sessionId);
        Directory.CreateDirectory(sessionDir);
        DateTime started = DateTime.UtcNow;
        using var log = new CaptureLog(stdout, Path.Combine(sessionDir, "log.txt"));

        log.Info($"Tzar2 capture session {sessionId}");
        log.Info($"Install: {install}");
        log.Info("Checking the install against the GOG v1.10 English reference manifest ...");
        KnownEdition edition = EditionCatalog.Reference;
        InstallVerificationReport report = InstallVerifier.Verify(install, edition.Manifest);
        string exeMd5 = InstallVerifier.ComputeMd5(exe);
        log.Info($"  identical {report.Identical.Count}, modified {report.Modified.Count}, missing {report.Missing.Count}, extra {report.Extra.Count}");
        if (!report.MatchesReference && !o.Has("--allow-modified"))
        {
            foreach (ModifiedFile m in report.Modified.Take(10))
            {
                log.Error($"  modified: {m.Path}");
            }

            foreach (string m in report.Missing.Take(10))
            {
                log.Error($"  missing: {m}");
            }

            log.Error("The install is not the clean reference build (REQ-RE-03). Fix it, or pass --allow-modified (-AllowModified) to capture anyway.");
            return ExitCodes.Mismatch;
        }

        log.Info($"Preparing the work copy {workDir} (your install is not modified) ...");
        WorkCopy.SyncResult sync = WorkCopy.Sync(install, workDir);
        log.Info($"  {sync.Copied} files copied ({sync.BytesCopied / (1 << 20)} MiB), {sync.Unchanged} unchanged");
        string rendererDescription = PrepareRenderer(renderer, install, workDir, home, log, out renderer);
        log.Info($"Renderer: {rendererDescription}");
        foreach (string line in MachineSettings.CompatDatabaseWarning(DeviceProbe.CompatDatabases(Path.GetFileName(exe)), install))
        {
            log.Warn(line);
        }

        // The game refuses to start when tzar.ini's device counts differ from this PC's (MachineSettings); the session
        // file never carries them, an explicit value there still wins.
        IReadOnlyDictionary<string, string> detected = MachineSettings.IniFor(DeviceProbe.Detect());
        log.Info($"This PC (work copy's tzar.ini only): {(detected.Count == 0 ? "no devices detected" : string.Join(", ", detected.Select(kv => $"{kv.Key}={kv.Value}")))}");
        if (detected.Count < 5)
        {
            log.Warn("Some device counts could not be read; the install's values are kept for them. If the game says \"Invalid TZAR configuration\", run Setup.exe in the work copy and put the values it writes into session.yaml (ini:), locally.");
        }

        session = session with { Ini = MachineSettings.Underlay(session.Ini, detected) };

        using var registry = new RegistryGuard();
        registry.Set(RegistryGuard.GameKey, "Path", workDir);
        if (renderer == RendererMode.Native)
        {
            registry.Set(RegistryGuard.LayersKey, Path.Combine(workDir, Path.GetFileName(exe)), "~ HIGHDPIAWARE");
        }

        var environment = new GameEnvironment(install, workDir, renderer, rendererDescription);
        double minutes = selected.Sum(s => s.Minutes);
        log.Info($"Scenarios: {string.Join(", ", selected.Select(s => s.Id))} (about {minutes:0} minutes).");
        log.Info("Please do not touch the mouse or keyboard until the session ends. Ctrl+C in this window stops it safely.");
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
            log.Warn("Stopping after the current step (Ctrl+C) ...");
        };
        Console.CancelKeyPress += handler;
        IReadOnlyList<TrialResult> results;
        NativeMethods.SetThreadExecutionState(NativeMethods.EsContinuous | NativeMethods.EsSystemRequired | NativeMethods.EsDisplayRequired);
        try
        {
            for (int s = 5; s > 0 && !cancel.IsCancellationRequested; s--)
            {
                log.Info($"Starting in {s} ...");
                Thread.Sleep(1000);
            }

            var runner = new CaptureRunner(session, environment, () => new Win32GameDriver(), sessionDir, log) { Cancellation = cancel.Token };
            results = runner.Run(selected);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            NativeMethods.SetThreadExecutionState(NativeMethods.EsContinuous);
        }

        WriteManifest(Path.Combine(sessionDir, "session.json"), sessionId, started, o.Get("--commit"), install, edition, report, exeMd5, rendererDescription,
            session, results);
        bool usable;
        using (CaptureSession check = CaptureSession.Open(sessionDir))
        {
            log.Info("Capture summary:");
            var summary = new StringWriter();
            usable = PrintQuality(check, summary);
            foreach (string line in summary.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                log.Info("  " + line.TrimEnd('\r'));
            }
        }

        log.Dispose();
        string zip = sessionDir + ".zip";
        if (File.Exists(zip))
        {
            File.Delete(zip);
        }

        ZipFile.CreateFromDirectory(sessionDir, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
        long size = new FileInfo(zip).Length;
        stdout.WriteLine();
        stdout.WriteLine($"RESULT: {zip} ({size / (1 << 20)} MiB). Send this one file back.");
        stdout.WriteLine(usable ? "Captures look usable." : "WARNING: the captures look unusable; send the zip anyway (log.txt explains what happened).");
        return usable ? ExitCodes.Success : ExitCodes.Mismatch;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string PrepareRenderer(RendererMode requested, string install, string workDir, string home, CaptureLog log, out RendererMode used)
    {
        IReadOnlyList<string> existing = WorkCopy.WrapperFiles(install);
        if (existing.Count > 0)
        {
            log.Info($"The install has DirectDraw wrapper files: {string.Join(", ", existing.Select(Path.GetFileName))}");
        }

        used = requested;
        switch (requested)
        {
            case RendererMode.Existing:
                return existing.Count > 0 ? $"existing wrapper ({string.Join(", ", existing.Select(Path.GetFileName))}, settings untouched)" : "system DirectDraw (no wrapper in the install)";
            case RendererMode.Native:
                foreach (string f in WorkCopy.WrapperFiles(workDir))
                {
                    File.Delete(f);
                }

                return "system DirectDraw (full-screen; window captures may be black, F11 snapshots still work)";
            default:
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                    string zip = CncDdraw.EnsureDownloaded(Path.Combine(home, "cache"), http, log);
                    CncDdraw.Install(zip, workDir);
                    return $"cnc-ddraw {CncDdraw.Version} (pinned, SHA-256 verified), windowed, GDI renderer, no scaling";
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Threading.Tasks.TaskCanceledException)
                {
                    string dll = Path.Combine(workDir, "ddraw.dll");
                    if (File.Exists(dll) && CncDdraw.LooksLikeCncDdraw(dll))
                    {
                        log.Warn($"cnc-ddraw download failed ({ex.Message}); using the cnc-ddraw already in the install with the harness settings.");
                        return "cnc-ddraw from the install (version unknown), windowed, GDI renderer";
                    }

                    log.Warn($"cnc-ddraw download failed ({ex.Message}); falling back to the system DirectDraw.");
                    foreach (string f in WorkCopy.WrapperFiles(workDir))
                    {
                        File.Delete(f);
                    }

                    used = RendererMode.Native;
                    return "system DirectDraw (cnc-ddraw unavailable)";
                }
        }
    }

    private static void WriteManifest(string path, string sessionId, DateTime started, string? commit, string install, KnownEdition edition,
        InstallVerificationReport report, string exeMd5, string renderer, SessionConfig session, IReadOnlyList<TrialResult> results)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("schema", "tzar2-capture-session-result/1");
        w.WriteString("session_id", sessionId);
        w.WriteString("started_utc", started.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        w.WriteString("finished_utc", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        w.WriteStartObject("harness");
        w.WriteString("version", typeof(FidelityCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?");
        w.WriteString("commit", commit ?? "unknown");
        w.WriteEndObject();
        w.WriteStartObject("machine");
        w.WriteString("os", RuntimeInformation.OSDescription);
        w.WriteString("arch", RuntimeInformation.OSArchitecture.ToString());
        w.WriteNumber("cpus", Environment.ProcessorCount);
        w.WriteString("dotnet", RuntimeInformation.FrameworkDescription);
        w.WriteEndObject();
        w.WriteStartObject("install");
        w.WriteString("path", install);
        w.WriteString("edition", edition.DisplayName);
        w.WriteBoolean("matches_reference", report.MatchesReference);
        w.WriteNumber("identical", report.Identical.Count);
        w.WriteNumber("modified", report.Modified.Count);
        w.WriteNumber("missing", report.Missing.Count);
        w.WriteNumber("extra", report.Extra.Count);
        w.WriteString("exe_md5", exeMd5);
        w.WriteStartArray("modified_files");
        foreach (ModifiedFile m in report.Modified.Take(50))
        {
            w.WriteStringValue(m.Path);
        }

        w.WriteEndArray();
        w.WriteStartArray("extra_files");
        foreach (string e in report.Extra.Take(50))
        {
            w.WriteStringValue(e);
        }

        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteString("renderer", renderer);
        w.WriteString("layout", session.Layout.Name);
        w.WriteStartArray("trials");
        foreach (TrialResult r in results)
        {
            w.WriteStartObject();
            w.WriteString("scenario", r.Scenario);
            w.WriteString("trial", r.Trial);
            w.WriteString("status", r.Status);
            w.WriteNumber("steps_ok", r.StepsOk);
            w.WriteNumber("steps_failed", r.StepsFailed);
            w.WriteNumber("steps_skipped", r.StepsSkipped);
            w.WriteNumber("captures", r.Captures);
            if (r.Error is not null)
            {
                w.WriteString("error", r.Error);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static string? EnvOriginal()
    {
        foreach (string name in new[] { "TZAR2_ORIGIN", "TZAR2_ORIGINAL_PATH" })
        {
            string? v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v) && Directory.Exists(v))
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>The directory containing <c>Tzar2.sln</c> above the current directory or the executable, or null.</summary>
    internal static string? RepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (DirectoryInfo? dir = new(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Tzar2.sln")))
                {
                    return dir.FullName;
                }
            }
        }

        return null;
    }

    private sealed class OptionException(string message) : Exception(message);

    /// <summary>Minimal option parser: valued options, flags and a fixed number of positional arguments.</summary>
    private sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

        public List<string> Positional { get; } = [];

        public string? Get(string name) => _values.TryGetValue(name, out string? v) ? v : null;

        public bool Has(string flag) => _flags.Contains(flag);

        public static Options Parse(string[] args, string[] valued, string[] flags, int positional = 0)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (Array.IndexOf(valued, a) >= 0)
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new OptionException($"'{a}' requires a value");
                    }

                    o._values[a] = args[++i];
                }
                else if (Array.IndexOf(flags, a) >= 0)
                {
                    o._flags.Add(a);
                }
                else if (a.StartsWith('-'))
                {
                    throw new OptionException($"unknown option '{a}'");
                }
                else
                {
                    o.Positional.Add(a);
                }
            }

            if (o.Positional.Count != positional)
            {
                throw new OptionException(positional == 0 ? $"unexpected argument '{o.Positional[0]}'" : $"expected {positional} argument(s)");
            }

            return o;
        }
    }
}
