using System;
using System.IO;
using System.Reflection;
using Tzar2.Import.Editions;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// <c>t2 verify-original &lt;path&gt;</c>: compares a local install with the GOG v1.10 English reference manifest
/// (TDD REQ-RE-03, docs/original-build/README.md) by path, size and MD5.
/// </summary>
internal static class VerifyOriginalCommand
{
    public const string EmbeddedManifestName = "gog-1.10-en.reference.tsv";
    private const string EmbeddedManifestResource = "Tzar2.Tools.Cli.Manifests." + EmbeddedManifestName;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? installPath = null;
        string? manifestPath = null;
        bool strict = false;
        bool quiet = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    PrintUsage(stdout);
                    return ExitCodes.Success;
                case "--manifest" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "-q" or "--quiet":
                    quiet = true;
                    break;
                case { } option when option.StartsWith('-'):
                    stderr.WriteLine($"t2 verify-original: unknown or incomplete option '{option}'.");
                    return ExitCodes.UsageError;
                case { } path when installPath is null:
                    installPath = path;
                    break;
                default:
                    stderr.WriteLine($"t2 verify-original: unexpected argument '{args[i]}'.");
                    return ExitCodes.UsageError;
            }
        }

        if (installPath is null)
        {
            PrintUsage(stderr);
            return ExitCodes.UsageError;
        }

        ReferenceManifest manifest;
        InstallVerificationReport report;
        try
        {
            manifest = manifestPath is null ? LoadEmbeddedManifest() : ReferenceManifest.Load(manifestPath);
            report = InstallVerifier.Verify(installPath, manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            stderr.WriteLine($"t2 verify-original: {ex.Message}");
            return ExitCodes.UsageError;
        }

        stdout.WriteLine($"Reference: {manifest.Name} ({manifest.Entries.Count} files, {manifest.TotalSize} bytes)");
        stdout.WriteLine($"Install:   {Path.GetFullPath(installPath)}");
        if (!quiet)
        {
            foreach (ModifiedFile file in report.Modified)
            {
                string detail = file.SizeDiffers
                    ? $"size {file.ExpectedSize} expected, {file.ActualSize} found"
                    : $"md5 {file.ExpectedMd5} expected, {file.ActualMd5} found";
                stdout.WriteLine($"MODIFIED  {file.Path}  ({detail})");
            }

            foreach (string path in report.Missing)
            {
                stdout.WriteLine($"MISSING   {path}");
            }

            foreach (string path in report.Extra)
            {
                stdout.WriteLine($"EXTRA     {path}");
            }
        }

        stdout.WriteLine(
            $"Identical: {report.Identical.Count}, modified: {report.Modified.Count}, missing: {report.Missing.Count}, " +
            $"extra: {report.Extra.Count}, ignored: {report.Ignored.Count}");

        bool ok = report.MatchesReference && (!strict || report.Extra.Count == 0);
        stdout.WriteLine(ok ? "Result: the install matches the reference build." : "Result: MISMATCH.");
        return ok ? ExitCodes.Success : ExitCodes.Mismatch;
    }

    internal static ReferenceManifest LoadEmbeddedManifest()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedManifestResource)
            ?? throw new InvalidOperationException($"Embedded manifest '{EmbeddedManifestResource}' is missing.");
        using var reader = new StreamReader(stream);
        return ReferenceManifest.Parse(reader, EmbeddedManifestName);
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("Usage: t2 verify-original <path> [--manifest <file.tsv>] [--strict] [--quiet]");
        output.WriteLine();
        output.WriteLine("Compares an original install with a reference manifest by path, size and MD5.");
        output.WriteLine($"  --manifest <file>  manifest to compare against (default: the embedded {EmbeddedManifestName})");
        output.WriteLine("  --strict           also fail when the install contains files that are not in the manifest");
        output.WriteLine("  --quiet            print only the summary");
        output.WriteLine();
        output.WriteLine("Exit codes: 0 = matches, 1 = modified or missing files (or extra files with --strict), 2 = usage or I/O error.");
    }
}
