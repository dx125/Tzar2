using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Tzar2.Content.Rules;
using Tzar2.Game.Maps;
using Tzar2.Game.Session;
using Tzar2.Replay;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// <c>t2 replay verify|record|info</c> (TDD §19.4, ADR 0164): verify re-simulates replays headlessly from their setup
/// description and compares every stored hash (the M6 criterion "replays verify on Windows and headless Linux/macOS");
/// record plays a computer-only match headlessly and writes its replay; info prints the header.
/// </summary>
internal static class ReplayCommand
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Usage(args.Length == 0 ? stderr : stdout);
            return args.Length == 0 ? ExitCodes.UsageError : ExitCodes.Success;
        }

        try
        {
            return args[0] switch
            {
                "verify" => Verify(args[1..], stdout, stderr),
                "record" => Record(args[1..], stdout, stderr),
                "info" => Info(args[1..], stdout, stderr),
                _ => Unknown(args[0], stderr),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or RulesException or UnauthorizedAccessException or ArgumentException)
        {
            stderr.WriteLine($"t2 replay {args[0]}: {e.Message}");
            return ExitCodes.UsageError;
        }
    }

    private static int Unknown(string verb, TextWriter stderr)
    {
        stderr.WriteLine($"t2 replay: unknown command '{verb}'.");
        Usage(stderr);
        return ExitCodes.UsageError;
    }

    private static void Usage(TextWriter output)
    {
        output.WriteLine("Usage: t2 replay verify <file.t2rep>... [--package <base.t2pkg> | --synthetic] [--report <file.json>]");
        output.WriteLine("       t2 replay record --out <file.t2rep> [--package <base.t2pkg> | --synthetic] [--players <n>] [--ticks <n>] [--seed <n>] [--map test|continental|lakes|dry|island|archipelagos] [--size <tiles>]");
        output.WriteLine("       t2 replay info <file.t2rep>");
    }

    private static int Verify(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var files = new List<string>();
        string? package = MatchContent.EnvironmentPackage();
        string? report = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--package" when i + 1 < args.Length:
                    package = args[++i];
                    break;
                case "--synthetic":
                    package = null;
                    break;
                case "--report" when i + 1 < args.Length:
                    report = args[++i];
                    break;
                default:
                    files.Add(args[i]);
                    break;
            }
        }

        if (files.Count == 0)
        {
            stderr.WriteLine("t2 replay verify: no replay given.");
            return ExitCodes.UsageError;
        }

        MatchContent content = MatchContent.Load(package);
        stdout.WriteLine($"content: {content.Description}; engine {Game.GameInfo.Version}, overflow policy {Core.Numerics.OverflowPolicy.Mode}");
        var results = new List<(string File, ReplayVerification? Result, string? Error)>();
        foreach (string file in files.SelectMany(Expand))
        {
            (ReplayVerification? result, string? error) = VerifyOne(file, content);
            results.Add((file, result, error));
            stdout.WriteLine(error is not null
                ? $"{file}: ERROR {error}"
                : result!.Value.Succeeded
                    ? string.Create(CultureInfo.InvariantCulture, $"{file}: OK {result.Value.TicksRun} ticks, {result.Value.HashesChecked} hashes, final {result.Value.FinalStateHash:X16}")
                    : string.Create(CultureInfo.InvariantCulture, $"{file}: MISMATCH at tick {result.Value.FirstMismatch!.Value.Value} after {result.Value.HashesChecked} matching hashes"));
        }

        if (report is not null)
        {
            WriteReport(report, results);
        }

        int ok = results.Count(r => r.Result is { Succeeded: true });
        stdout.WriteLine($"{ok} of {results.Count} replays verified");
        return results.Any(r => r.Error is not null) ? ExitCodes.UsageError : ok == results.Count ? ExitCodes.Success : ExitCodes.Mismatch;
    }

    private static IEnumerable<string> Expand(string path) =>
        Directory.Exists(path) ? Directory.GetFiles(path, "*.t2rep").Order(StringComparer.Ordinal) : [path];

    /// <summary>Re-simulates one replay; the error says why it cannot be verified with this content.</summary>
    internal static (ReplayVerification? Result, string? Error) VerifyOne(string file, MatchContent content)
    {
        ReplayFile replay;
        try
        {
            replay = ReplayFile.Parse(MatchContent.ReadBounded(file));
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return (null, e.Message);
        }

        ReplayHeader h = replay.Header;
        if (h.Setup.IsDefaultOrEmpty)
        {
            return (null, "the replay has no setup description (a scenario or campaign map): it cannot be rebuilt headlessly");
        }

        if (h.RulesetHash != content.Ruleset.ContentHash)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"recorded with ruleset {h.RulesetHash:X16}, this content has {content.Ruleset.ContentHash:X16} (another package or version; --package)"));
        }

        foreach (ReplayAiProgram p in h.AiPrograms)
        {
            if (p.ProgramHash != content.Ai.ContentHash)
            {
                return (null, string.Create(CultureInfo.InvariantCulture, $"kingdom {p.Slot + 1} ran AI script {p.ProgramHash:X16}, this content has {content.Ai.ContentHash:X16}"));
            }
        }

        MatchSetup setup = ReplayPlayback.SetupOf(h, content.Ai);
        using GameSession session = GameSession.ForReplay(setup, content.Ruleset);
        if (session.Simulation.MapHash != h.MapHash)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"the rebuilt map differs ({session.Simulation.MapHash:X16} vs {h.MapHash:X16})"));
        }

        return (ReplayVerifier.Verify(replay, session.Simulation), null);
    }

    private static void WriteReport(string path, List<(string File, ReplayVerification? Result, string? Error)> results)
    {
        using FileStream stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("engine", Game.GameInfo.Version);
        json.WriteString("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        json.WriteString("arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
        json.WriteStartArray("replays");
        foreach ((string file, ReplayVerification? result, string? error) in results)
        {
            json.WriteStartObject();
            json.WriteString("file", Path.GetFileName(file));
            json.WriteBoolean("verified", result is { Succeeded: true });
            if (result is { } r)
            {
                json.WriteNumber("ticks", r.TicksRun);
                json.WriteNumber("hashes", r.HashesChecked);
                json.WriteString("finalHash", r.FinalStateHash.ToString("X16", CultureInfo.InvariantCulture));
                if (r.FirstMismatch is { } m)
                {
                    json.WriteNumber("firstMismatch", m.Value);
                }
            }

            if (error is not null)
            {
                json.WriteString("error", error);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static int Record(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? output = null;
        string? package = MatchContent.EnvironmentPackage();
        int players = 3;
        int ticks = 2_400;
        ulong seed = 1;
        string map = "test";
        int size = 96;
        for (int i = 0; i < args.Length; i++)
        {
            string value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            switch (args[i])
            {
                case "--out": output = value; i++; break;
                case "--package": package = value; i++; break;
                case "--synthetic": package = null; break;
                case "--players": players = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--ticks": ticks = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--seed": seed = ulong.Parse(value, CultureInfo.InvariantCulture); i++; break;
                case "--map": map = value; i++; break;
                case "--size": size = int.Parse(value, CultureInfo.InvariantCulture); i++; break;
                default:
                    stderr.WriteLine($"t2 replay record: unknown option {args[i]}.");
                    return ExitCodes.UsageError;
            }
        }

        if (output is null || players is < 1 or > 8 || ticks < 1)
        {
            stderr.WriteLine("t2 replay record: --out is required; 1-8 players; at least one tick.");
            return ExitCodes.UsageError;
        }

        MatchContent content = MatchContent.Load(package);
        IMapSource source = map == "test"
            ? new SkirmishTestMap(size)
            : Content.MapGen.RandomMapNames.TryParse(map, out Content.MapGen.RandomMapType type) ? new RandomMapSource(type) : throw new ArgumentException($"Unknown map '{map}'.");
        var setup = new MatchSetup
        {
            Map = source,
            Players = [.. Enumerable.Range(0, players).Select(p => new PlayerSetup((byte)p, "euro", ControllerKind.Computer, (Difficulty)(p % 3)))],
            Seed = seed,
            Ai = content.Ai,
        };
        using var session = new GameSession(setup, content.Ruleset, fog: false);
        session.Step(ticks);
        byte[] replay = session.FinishReplay()!;
        if (Path.GetDirectoryName(Path.GetFullPath(output)) is { } folder)
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllBytes(output, replay);
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"recorded {ticks} ticks of {players} computer kingdoms on {source.Name} to {output} ({replay.Length} bytes), final {session.Simulation.ComputeStateHash():X16}"));
        return ExitCodes.Success;
    }

    private static int Info(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 1)
        {
            stderr.WriteLine("t2 replay info: one replay.");
            return ExitCodes.UsageError;
        }

        ReplayFile replay = ReplayFile.Parse(MatchContent.ReadBounded(args[0]));
        ReplayHeader h = replay.Header;
        stdout.WriteLine($"engine {h.EngineVersion}, ruleset {h.RulesetHash:X16}, map {h.MapHash:X16}, seed {h.Config.MatchSeed}, overflow {h.OverflowMode}");
        stdout.WriteLine($"{h.Config.PlayerCount} kingdoms ({string.Join(", ", h.PlayerNames.Select((n, i) => n.Length > 0 ? n : $"computer {i + 1}"))}), victory {h.Config.Victory}, cheats {h.Config.CheatsAllowed}, ranked {h.Config.Ranked}");
        stdout.WriteLine($"{replay.Ticks.Count} ticks ({replay.Ticks.Count * h.Config.TickLengthMilliseconds / 1000} s of game time), {replay.Keyframes.Count} keyframes, {replay.Chat.Count} chat lines, setup {(h.Setup.IsDefaultOrEmpty ? "none" : "described")}");
        foreach (ReplayChat c in replay.Chat.Take(20))
        {
            stdout.WriteLine($"  tick {c.Tick} kingdom {c.From + 1}: {c.Text}");
        }

        return ExitCodes.Success;
    }
}
