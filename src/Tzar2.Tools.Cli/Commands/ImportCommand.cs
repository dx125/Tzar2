using System;
using System.Globalization;
using System.IO;
using Tzar2.Formats.Text;
using Tzar2.Import.Pipeline;

namespace Tzar2.Tools.Cli.Commands;

/// <summary><c>t2 import &lt;originPath&gt; &lt;outDir&gt;</c>: converts an original install into a base package (TDD §12.1).</summary>
internal static class ImportCommand
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? origin = null, output = null, packageId = null;
        CodePage? codePage = null;
        int? jobs = null;
        bool quiet = false, skipDetection = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    PrintUsage(stdout);
                    return ExitCodes.Success;
                case "--id" when i + 1 < args.Length:
                    packageId = args[++i];
                    break;
                case "--codepage" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out int cp) || !LegacyText.IsSupported(cp))
                    {
                        stderr.WriteLine($"t2 import: unsupported code page '{args[i]}' (1250, 1251 or 1252).");
                        return ExitCodes.UsageError;
                    }

                    codePage = (CodePage)cp;
                    break;
                case "--jobs" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out int j) || j < 1)
                    {
                        stderr.WriteLine($"t2 import: invalid job count '{args[i]}'.");
                        return ExitCodes.UsageError;
                    }

                    jobs = j;
                    break;
                case "--skip-edition-check":
                    skipDetection = true;
                    break;
                case "-q" or "--quiet":
                    quiet = true;
                    break;
                case { } option when option.StartsWith('-'):
                    stderr.WriteLine($"t2 import: unknown or incomplete option '{option}'.");
                    return ExitCodes.UsageError;
                case { } path when origin is null:
                    origin = path;
                    break;
                case { } path when output is null:
                    output = path;
                    break;
                default:
                    stderr.WriteLine($"t2 import: unexpected argument '{args[i]}'.");
                    return ExitCodes.UsageError;
            }
        }

        if (origin is null || output is null)
        {
            PrintUsage(stderr);
            return ExitCodes.UsageError;
        }

        if (!Directory.Exists(origin))
        {
            stderr.WriteLine($"t2 import: '{origin}' is not a folder.");
            return ExitCodes.UsageError;
        }

        if (packageId is not null && !IsValidPackageId(packageId))
        {
            stderr.WriteLine($"t2 import: invalid package id '{packageId}' (letters, digits, '.', '-', '_'; must not start with '.').");
            return ExitCodes.UsageError;
        }

        // REQ-IMP-04: never write into the install. Case-insensitive, because the install usually lives on a
        // case-insensitive file system (Windows, macOS); symbolic links are resolved where they exist.
        string originFull = Resolve(origin).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((Resolve(output) + Path.DirectorySeparatorChar).StartsWith(originFull, StringComparison.OrdinalIgnoreCase))
        {
            stderr.WriteLine("t2 import: the output folder must not be inside the original install (REQ-IMP-04).");
            return ExitCodes.UsageError;
        }

        var options = new ImportOptions
        {
            OriginPath = origin,
            OutputDirectory = output,
            PackageId = packageId ?? "base.original",
            CodePageOverride = codePage,
            MaxParallelism = jobs ?? Environment.ProcessorCount,
            SkipEditionDetection = skipDetection,
        };

        ImportResult result;
        try
        {
            result = ImportPipeline.Run(options, quiet ? null : stdout.WriteLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"t2 import: {ex.Message}");
            return ExitCodes.UsageError;
        }

        stdout.Write(result.Report.ToText(quiet ? 10 : 40));
        stdout.WriteLine($"Report: {Path.Combine(Path.GetFullPath(output), ImportPipeline.ReportJson)}");
        return result.Report.Succeeded ? ExitCodes.Success : ExitCodes.Mismatch;
    }

    internal static bool IsValidPackageId(string id)
    {
        if (id.Length is 0 or > 64 || id[0] == '.' || id.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        try
        {
            // Resolve links on the longest existing prefix of the path.
            string existing = full;
            string rest = string.Empty;
            while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
            {
                rest = Path.Combine(Path.GetFileName(existing), rest);
                existing = parent;
            }

            string? target = new DirectoryInfo(existing).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            return Path.TrimEndingDirectorySeparator(Path.Combine(target ?? existing, rest));
        }
        catch (IOException)
        {
            return full;
        }
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("Usage: t2 import <originPath> <outDir> [--id <packageId>] [--codepage 1250|1251|1252] [--jobs <n>] [--skip-edition-check] [--quiet]");
        output.WriteLine();
        output.WriteLine("Converts an original install (read only) into <outDir>/<packageId>.t2pkg and writes import-report.json/.txt.");
        output.WriteLine("The package is deterministic: the same install gives a byte-identical file.");
        output.WriteLine("Exit codes: 0 = imported without errors, 1 = some files failed (see the report), 2 = usage or I/O error.");
    }
}
