using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Game.Campaigns;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// <c>t2 campaign run</c>: plays campaigns headless to their end with the scripted solver (ADR 0133; TDD §22 item 5
/// campaign completion bots) and reports which missions complete. Sources: the built-in demo campaign
/// (<c>--demo</c>); legacy <c>.wcm</c> campaigns converted on the fly join with the converter (M4-A).
/// </summary>
internal static class CampaignCommand
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" || args[0] != "run")
        {
            PrintUsage(args.Length > 0 && args[0] is "-h" or "--help" ? stdout : stderr);
            return args.Length > 0 && args[0] is "-h" or "--help" ? ExitCodes.Success : ExitCodes.UsageError;
        }

        var options = new AutoplayOptions();
        string? input = null;
        string? report = null;
        bool demo = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--demo":
                    demo = true;
                    break;
                case "--max-ticks" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int ticks) && ticks > 0:
                    options = options with { MaxTicksPerMission = ticks };
                    i++;
                    break;
                case "--no-ai":
                    options = options with { ComputerAi = false };
                    break;
                case "--no-cheats":
                    options = options with { CheatAssist = false };
                    break;
                case "--report" when i + 1 < args.Length:
                    report = args[++i];
                    break;
                case { } option when option.StartsWith('-'):
                    stderr.WriteLine($"t2 campaign run: unknown or incomplete option '{option}'.");
                    return ExitCodes.UsageError;
                default:
                    input = args[i];
                    break;
            }
        }

        Ruleset ruleset = SyntheticLegacyContent.CompileRuleset();
        var sources = new List<ICampaignSource>();
        if (demo)
        {
            sources.Add(DemoCampaign.Create());
        }

        if (input is not null)
        {
            stderr.WriteLine($"t2 campaign run: cannot read campaigns from '{input}' yet (legacy .wcm conversion comes with the converter, M4-A).");
            return ExitCodes.NotImplemented;
        }

        if (sources.Count == 0)
        {
            PrintUsage(stderr);
            return ExitCodes.UsageError;
        }

        return Play(sources, ruleset, options, report, stdout);
    }

    /// <summary>Plays every campaign and prints the per-mission table; 0 when all completed.</summary>
    public static int Play(IReadOnlyList<ICampaignSource> sources, Ruleset ruleset, AutoplayOptions options, string? reportPath, TextWriter stdout)
    {
        var reports = new List<CampaignReport>();
        bool all = true;
        foreach (ICampaignSource source in sources)
        {
            stdout.WriteLine($"Campaign {source.Campaign.Id}: {source.Campaign.Name} ({source.Campaign.Maps.Length} maps)");
            CampaignReport r = CampaignAutoplay.Run(source, ruleset, options, line => stdout.WriteLine("  " + line));
            reports.Add(r);
            all &= r.Completed;
            stdout.WriteLine($"  => {(r.Completed ? "COMPLETED" : "NOT COMPLETED")}{(r.Error is { } e ? " (" + e + ")" : string.Empty)}");
        }

        if (reportPath is not null)
        {
            File.WriteAllBytes(reportPath, ToJson(reports));
        }

        return all ? ExitCodes.Success : ExitCodes.Mismatch;
    }

    private static byte[] ToJson(IReadOnlyList<CampaignReport> reports)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartArray();
            foreach (CampaignReport r in reports)
            {
                w.WriteStartObject();
                w.WriteString("campaign", r.CampaignId);
                w.WriteBoolean("completed", r.Completed);
                w.WriteString("error", r.Error);
                w.WriteStartArray("missions");
                foreach (MissionReport m in r.Missions)
                {
                    w.WriteStartObject();
                    w.WriteString("map", m.MapId);
                    w.WriteBoolean("completed", m.Completed);
                    w.WriteString("result", m.Result.ToString());
                    w.WriteNumber("ticks", m.Ticks);
                    w.WriteString("assist", m.Assist);
                    w.WriteString("next", m.NextMapId);
                    w.WriteNumber("faults", m.Faults);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return stream.ToArray();
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("Usage: t2 campaign run (--demo | <campaign.wcm|folder>) [--max-ticks <n>] [--no-ai] [--no-cheats] [--report <file.json>]");
        output.WriteLine();
        output.WriteLine("Plays campaigns headless with the scripted solver and reports which missions complete.");
        output.WriteLine("Exit codes: 0 = every campaign completed, 1 = some did not, 2 = usage error.");
    }
}
