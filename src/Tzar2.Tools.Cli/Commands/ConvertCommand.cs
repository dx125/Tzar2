using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
using Tzar2.Formats.Text;
using Tzar2.Import.Maps;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// <c>t2 convert &lt;file|folder&gt; [--out &lt;dir&gt;] [--codepage &lt;cp&gt;] [--mapping &lt;yaml&gt;] [--package &lt;base.t2pkg&gt;]
/// [--json &lt;report.json&gt;] [--quiet]</c>: converts legacy maps and campaigns (TDD §18.5, REQ-EDT-04) into
/// <c>.t2map</c>/<c>.t2camp</c> packages with a conversion report. A folder (or <c>--corpus</c>, defaulting to
/// <c>TZAR2_CORPUS</c>) converts every legacy file under it and reports the pass rate.
/// </summary>
internal static class ConvertCommand
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? input = null, output = null, mappingPath = null, packagePath = null, jsonPath = null;
        CodePage? codePage = null;
        bool quiet = false, corpus = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    PrintUsage(stdout);
                    return ExitCodes.Success;
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--mapping" when i + 1 < args.Length:
                    mappingPath = args[++i];
                    break;
                case "--package" when i + 1 < args.Length:
                    packagePath = args[++i];
                    break;
                case "--json" when i + 1 < args.Length:
                    jsonPath = args[++i];
                    break;
                case "--codepage" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out int cp) || !LegacyText.IsSupported(cp))
                    {
                        stderr.WriteLine($"t2 convert: unsupported code page '{args[i]}' (1250, 1251 or 1252).");
                        return ExitCodes.UsageError;
                    }

                    codePage = (CodePage)cp;
                    break;
                case "--corpus":
                    corpus = true;
                    break;
                case "-q" or "--quiet":
                    quiet = true;
                    break;
                case { } option when option.StartsWith('-'):
                    stderr.WriteLine($"t2 convert: unknown or incomplete option '{option}'.");
                    return ExitCodes.UsageError;
                case { } path when input is null:
                    input = path;
                    break;
                default:
                    stderr.WriteLine($"t2 convert: unexpected argument '{args[i]}'.");
                    return ExitCodes.UsageError;
            }
        }

        if (input is null && corpus)
        {
            input = Environment.GetEnvironmentVariable(LegacyCorpus.EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(input))
            {
                stderr.WriteLine($"t2 convert --corpus: give a folder or set {LegacyCorpus.EnvironmentVariable}.");
                return ExitCodes.UsageError;
            }
        }

        if (input is null)
        {
            PrintUsage(stderr);
            return ExitCodes.UsageError;
        }

        LegacyConversionOptions options;
        try
        {
            options = new LegacyConversionOptions
            {
                CodePage = codePage,
                Mapping = mappingPath is null ? null : LegacyTypeResolver.ParseMapping(File.ReadAllText(mappingPath, Encoding.UTF8)),
                Ruleset = packagePath is null ? null : LoadRuleset(packagePath),
            };
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException or RulesException or InvalidDataException)
        {
            stderr.WriteLine($"t2 convert: {e.Message}");
            return ExitCodes.UsageError;
        }

        if (Directory.Exists(input))
        {
            return RunFolder(input, output, jsonPath, options, quiet, stdout);
        }

        if (!File.Exists(input))
        {
            stderr.WriteLine($"t2 convert: '{input}' does not exist.");
            return ExitCodes.UsageError;
        }

        (MapConversion? map, CampaignConversion? campaign) = LegacyCorpus.ConvertFile(input, options);
        ConversionReport report = map?.Report ?? campaign!.Report;
        if (output is not null)
        {
            Write(input, output, map, campaign, stdout, quiet);
        }

        if (jsonPath is not null)
        {
            File.WriteAllBytes(jsonPath, report.ToJson());
        }

        if (!quiet)
        {
            stdout.Write(report.ToText());
        }

        return report.Succeeded ? ExitCodes.Success : ExitCodes.Mismatch;
    }

    private static int RunFolder(string root, string? output, string? jsonPath, LegacyConversionOptions options, bool quiet, TextWriter stdout)
    {
        var reports = new List<ConversionReport>();
        IReadOnlyList<CorpusEntry> entries = LegacyCorpus.Run(root, options, (file, map, campaign) =>
        {
            reports.Add(map?.Report ?? campaign!.Report);
            if (output is not null)
            {
                string rel = Path.GetDirectoryName(Path.GetRelativePath(root, file)) ?? string.Empty;
                Write(file, Path.Combine(output, rel), map, campaign, stdout, quiet: true);
            }
        });
        if (jsonPath is not null)
        {
            using var stream = new MemoryStream();
            stream.Write("{\"files\":["u8);
            for (int i = 0; i < reports.Count; i++)
            {
                if (i > 0)
                {
                    stream.Write(","u8);
                }

                stream.Write(reports[i].ToJson());
            }

            int permille = LegacyCorpus.PassPermille(entries);
            stream.Write(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"],\"passed\":{entries.Count(e => e.Succeeded)},\"total\":{entries.Count},\"passPermille\":{permille}}}\n")));
            File.WriteAllBytes(jsonPath, stream.ToArray());
        }

        stdout.Write(quiet ? LegacyCorpus.Summary(entries).Split('\n').Reverse().Skip(1).First() + "\n" : LegacyCorpus.Summary(entries));
        return entries.All(e => e.Succeeded) ? ExitCodes.Success : ExitCodes.Mismatch;
    }

    private static void Write(string input, string outDir, MapConversion? map, CampaignConversion? campaign, TextWriter stdout, bool quiet)
    {
        Directory.CreateDirectory(outDir);
        string stem = Path.GetFileNameWithoutExtension(input);
        ConversionReport report = map?.Report ?? campaign!.Report;
        byte[]? package = map is not null ? LegacyPackageWriter.WriteMap(map, stem) : LegacyPackageWriter.WriteCampaign(campaign!);
        string ext = map is not null ? ".t2map" : ".t2camp";
        if (package is not null)
        {
            string target = Path.Combine(outDir, stem + ext);
            File.WriteAllBytes(target, package);
            if (!quiet)
            {
                stdout.WriteLine($"wrote {target}");
            }
        }

        File.WriteAllBytes(Path.Combine(outDir, stem + ext + ".report.json"), report.ToJson());
        File.WriteAllText(Path.Combine(outDir, stem + ext + ".report.txt"), report.ToText(), new UTF8Encoding(false));
    }

    private static Ruleset LoadRuleset(string packagePath)
    {
        var vfs = new VirtualFileSystem();
        vfs.Mount(new EngineDefaultsSource());
        vfs.Mount(PackageSource.Open(packagePath));
        return RulesetCompiler.Compile(vfs, "classic").Ruleset;
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("Usage: t2 convert <file|folder> [options]");
        output.WriteLine("       t2 convert --corpus [folder]          (default folder: $" + LegacyCorpus.EnvironmentVariable + ")");
        output.WriteLine();
        output.WriteLine("Converts legacy .wmp maps and .wcm campaigns into .t2map/.t2camp packages (TDD §18.5).");
        output.WriteLine("A folder converts every legacy file under it and prints the pass rate.");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --out <dir>          write packages and <name>.<ext>.report.json/.txt there");
        output.WriteLine("  --codepage <cp>      force 1250, 1251 or 1252 instead of detecting it");
        output.WriteLine("  --mapping <yaml>     type mapping table: 'legacy_class: type.key' per line");
        output.WriteLine("  --package <t2pkg>    resolve types and terrain against the classic ruleset of this base package");
        output.WriteLine("  --json <file>        write the JSON report (folder: all reports and the pass rate)");
        output.WriteLine("  -q, --quiet          print only the summary");
        output.WriteLine();
        output.WriteLine("Exit code: 0 all converted without errors, 1 some had errors, 2 usage error.");
    }
}
