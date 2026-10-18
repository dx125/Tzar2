using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Tzar2.Content.Rules;
using Tzar2.Tools.Cli.Soak;

namespace Tzar2.Tools.Cli.Commands;

/// <summary><c>t2 soak</c>: the multiplayer soak harness (ADR 0167; TDD §22.7; M6 exit criterion).</summary>
internal static class SoakCommand
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? package = MatchContent.EnvironmentPackage();
        string? report = null;
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        bool verify = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    Usage(stdout);
                    return ExitCodes.Success;
                case "--no-verify":
                    verify = false;
                    break;
                case "--synthetic":
                    package = null;
                    break;
                case "--package" when i + 1 < args.Length:
                    package = args[++i];
                    break;
                case "--report" when i + 1 < args.Length:
                    report = args[++i];
                    break;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                    {
                        stderr.WriteLine($"t2 soak: unknown or incomplete option {args[i]}.");
                        Usage(stderr);
                        return ExitCodes.UsageError;
                    }

                    o[args[i][2..]] = args[++i];
                    break;
            }
        }

        SoakOptions options;
        try
        {
            MatchContent content = MatchContent.Load(package);
            options = new SoakOptions
            {
                Ruleset = content.Ruleset,
                Ai = content.Ai,
                Matches = Int(o, "matches", 10, 1, 100_000),
                Parallel = Int(o, "parallel", Math.Max(1, Environment.ProcessorCount / 4), 1, 64),
                Humans = Int(o, "humans", 2, 1, 6),
                Computers = Int(o, "computers", 2, 0, 6),
                Ticks = Int(o, "ticks", 6_000, 100, 1_000_000),
                Speed = Int(o, "speed", 400, 25, 400),
                RttMs = Int(o, "rtt", 200, 0, 2_000),
                JitterMs = o.ContainsKey("jitter") ? Int(o, "jitter", 0, 0, 1_000) : null,
                LossPercent = Double(o, "loss", 5, 0, 50),
                Disconnects = Double(o, "disconnects", 0.5, 0, 1),
                DropTimeoutSeconds = Int(o, "drop-timeout", 3, 1, 600),
                Seed = (ulong)Int(o, "seed", 1, 0, int.MaxValue),
                TestMapSize = o.TryGetValue("map", out string? map) && map == "random" ? 0 : Int(o, "map", 96, 48, 512),
                ReplayDirectory = o.GetValueOrDefault("replays"),
                MatchTimeoutSeconds = Int(o, "match-timeout", 900, 10, 86_400),
                VerifyReplays = verify,
            };
            if (options.Humans + options.Computers > 8)
            {
                throw new ArgumentException("At most 8 kingdoms per match.");
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IOException or RulesException)
        {
            stderr.WriteLine("t2 soak: " + e.Message);
            return ExitCodes.UsageError;
        }

        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"soak: {options.Matches} matches ({options.Parallel} at a time), {options.Humans} players + {options.Computers} Hard AIs, {options.Ticks} ticks at {options.Speed}% speed, RTT {options.RttMs} ms, jitter {options.JitterMs ?? options.RttMs / 20} ms, loss {options.LossPercent}%, disconnects {options.Disconnects:P0}, drop timeout {options.DropTimeoutSeconds} s"));
        TimerResolution.Begin();
        var started = DateTime.UtcNow;
        IReadOnlyList<SoakMatchResult> results = new SoakRunner(options, stdout).Run(CancellationToken.None);
        int ok = results.Count(r => r.Ok);
        int desyncs = results.Count(r => r.Desync || (r.Completed && !r.HashesAgree));
        int failures = results.Count(r => r.Failure is not null);
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"soak result: {ok}/{results.Count} matches ok, {desyncs} desyncs, {failures} failures, {results.Sum(r => r.Disconnects)} disconnects, {results.Sum(r => r.Rejoins)} rejoins, {results.Count(r => r.TakenOver)} AI takeovers, {results.Sum(r => r.ReplaysVerified)} replays verified, {results.Sum(r => r.Ticks)} ticks, {(DateTime.UtcNow - started).TotalMinutes:F1} min"));
        if (report is not null)
        {
            WriteReport(report, options, results);
        }

        return ok == results.Count && results.Count == options.Matches ? ExitCodes.Success : ExitCodes.Mismatch;
    }

    private static void Usage(TextWriter output)
    {
        output.WriteLine("Usage: t2 soak [--matches <n>] [--parallel <n>] [--humans <n>] [--computers <n>] [--ticks <n>] [--speed <25-400>]");
        output.WriteLine("              [--rtt <ms>] [--jitter <ms>] [--loss <%>] [--disconnects <0-1>] [--drop-timeout <s>] [--seed <n>]");
        output.WriteLine("              [--map <tiles>|random] [--replays <dir>] [--no-verify] [--report <file.json>] [--package <base.t2pkg> | --synthetic]");
        output.WriteLine("Headless matches through a real relay on loopback with impaired UDP, bots, Hard AIs and random disconnects (ADR 0167).");
        output.WriteLine("Exit code 0: every match completed with identical states, no desync and verified replays.");
    }

    private static int Int(Dictionary<string, string> o, string name, int fallback, int min, int max)
    {
        if (!o.TryGetValue(name, out string? text))
        {
            return fallback;
        }

        int value = int.Parse(text, CultureInfo.InvariantCulture);
        return value >= min && value <= max ? value : throw new ArgumentException($"--{name} must be in [{min}, {max}].");
    }

    private static double Double(Dictionary<string, string> o, string name, double fallback, double min, double max)
    {
        if (!o.TryGetValue(name, out string? text))
        {
            return fallback;
        }

        double value = double.Parse(text, CultureInfo.InvariantCulture);
        return value >= min && value <= max ? value : throw new ArgumentException($"--{name} must be in [{min}, {max}].");
    }

    private static void WriteReport(string path, SoakOptions options, IReadOnlyList<SoakMatchResult> results)
    {
        using FileStream stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("engine", Game.GameInfo.Version);
        json.WriteString("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        json.WriteNumber("matches", results.Count);
        json.WriteNumber("ok", results.Count(r => r.Ok));
        json.WriteNumber("desyncs", results.Count(r => r.Desync || (r.Completed && !r.HashesAgree)));
        json.WriteNumber("failures", results.Count(r => r.Failure is not null));
        json.WriteNumber("ticks", results.Sum(r => (long)r.Ticks));
        json.WriteNumber("disconnects", results.Sum(r => r.Disconnects));
        json.WriteNumber("rejoins", results.Sum(r => r.Rejoins));
        json.WriteNumber("takeovers", results.Count(r => r.TakenOver));
        json.WriteNumber("replaysVerified", results.Sum(r => r.ReplaysVerified));
        json.WriteNumber("replayFailures", results.Sum(r => r.ReplayFailures));
        json.WriteStartObject("settings");
        json.WriteNumber("humans", options.Humans);
        json.WriteNumber("computers", options.Computers);
        json.WriteNumber("ticksPerMatch", options.Ticks);
        json.WriteNumber("speed", options.Speed);
        json.WriteNumber("rttMs", options.RttMs);
        json.WriteNumber("lossPercent", options.LossPercent);
        json.WriteNumber("disconnects", options.Disconnects);
        json.WriteEndObject();
        json.WriteStartArray("results");
        foreach (SoakMatchResult r in results)
        {
            json.WriteStartObject();
            json.WriteNumber("index", r.Index);
            json.WriteNumber("seed", r.Seed);
            json.WriteString("map", r.Map);
            json.WriteBoolean("ok", r.Ok);
            json.WriteNumber("ticks", r.Ticks);
            json.WriteBoolean("hashesAgree", r.HashesAgree);
            json.WriteBoolean("desync", r.Desync);
            json.WriteNumber("disconnects", r.Disconnects);
            json.WriteNumber("rejoins", r.Rejoins);
            json.WriteBoolean("takenOver", r.TakenOver);
            json.WriteNumber("replaysVerified", r.ReplaysVerified);
            json.WriteNumber("orders", r.Orders);
            json.WriteNumber("lateBatches", r.LateBatches);
            json.WriteNumber("meanInputDelay", Math.Round(r.MeanInputDelay, 2));
            json.WriteNumber("upKBps", Math.Round(r.UpKBps, 3));
            json.WriteNumber("downKBps", Math.Round(r.DownKBps, 3));
            json.WriteNumber("seconds", Math.Round(r.Seconds, 1));
            if (r.Failure is not null)
            {
                json.WriteString("failure", r.Failure);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }
}
