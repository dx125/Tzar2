using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tzar2.AI.Legacy.Loading;
using Tzar2.AI.Legacy.Program;
using Tzar2.AI.Legacy.Syntax;
using Tzar2.AI.Runtime;
using Tzar2.Content.Packages;
using Tzar2.Content.Rules;

namespace Tzar2.Tools.Cli.Commands;

/// <summary>
/// <c>t2 ai check</c>: parses and compiles legacy AI scripts (REQ-AI-01, ADR 0050) from a folder (an install's
/// <c>DATA/AI</c>, a mod) or from a package (<c>text/data/ai/*</c>), and prints every diagnostic with its location.
/// The strategy scripts (<c>*.ais</c>, <c>def.ai</c> and those named by <c>AI.INI</c>) are compiled with their includes
/// and, with <c>--bind</c>, bound against the classic ruleset of the synthetic tables to list unknown names.
/// </summary>
internal static class AiCommand
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" || args[0] != "check")
        {
            PrintUsage(args.Length > 0 && args[0] is "-h" or "--help" ? stdout : stderr);
            return args.Length > 0 && args[0] is "-h" or "--help" ? ExitCodes.Success : ExitCodes.UsageError;
        }

        string? input = null;
        bool bind = false;
        string civ = "euro";
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bind":
                    bind = true;
                    break;
                case "--civ" when i + 1 < args.Length:
                    civ = args[++i];
                    break;
                case { } option when option.StartsWith('-'):
                    stderr.WriteLine($"t2 ai check: unknown or incomplete option '{option}'.");
                    return ExitCodes.UsageError;
                default:
                    if (input is not null)
                    {
                        stderr.WriteLine("t2 ai check: give one folder or package.");
                        return ExitCodes.UsageError;
                    }

                    input = args[i];
                    break;
            }
        }

        if (input is null)
        {
            stderr.WriteLine("t2 ai check: give the folder or package to check.");
            return ExitCodes.UsageError;
        }

        MemoryAiScriptSource source;
        string? aiIni;
        try
        {
            (source, aiIni) = Load(input);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            stderr.WriteLine($"t2 ai check: cannot read '{input}': {e.Message}");
            return ExitCodes.UsageError;
        }

        string[] names = [.. source.Names];
        if (names.Length == 0)
        {
            stderr.WriteLine($"t2 ai check: no .ai/.ais scripts in '{input}'.");
            return ExitCodes.UsageError;
        }

        int errors = 0;
        int warnings = 0;
        int rules = 0;
        foreach (string name in names)
        {
            source.TryRead(name, out string? text);
            AiScriptSyntax syntax = AiParser.Parse(name, text!);
            rules += CountRules(syntax.Items);
            Report(syntax.Diagnostics, stdout, ref errors, ref warnings);
        }

        stdout.WriteLine($"parsed     {names.Length} files, {rules} rules, {errors} errors, {warnings} warnings");
        var roots = new SortedSet<string>(names.Where(n => n.EndsWith(".ais", StringComparison.Ordinal) || n == "def.ai"), StringComparer.Ordinal);
        if (aiIni is not null)
        {
            foreach (AiScriptChoice choice in AiIni.Parse(aiIni))
            {
                roots.Add(choice.Script);
            }
        }

        AiRulesetModel? model = bind ? new AiRulesetModel(SyntheticLegacyContent.CompileRuleset()) : null;
        int compileErrors = 0;
        int compileWarnings = 0;
        foreach (string root in roots)
        {
            AiProgram program = AiCompiler.Compile(root, source);
            // Parse messages were reported per file above; report what compiling adds (includes, limits).
            IEnumerable<AiDiagnostic> added = program.Diagnostics.Where(d => d.Code >= AiDiagnosticCode.SourceTooLarge);
            if (model is not null)
            {
                added = added.Concat(AiBinding.Bind(program, model, civ).Diagnostics);
            }

            Report(added, stdout, ref compileErrors, ref compileWarnings);
            stdout.WriteLine($"compiled   {root}: {program.Rules.Length} rules from {program.Files.Length} files, {program.Symbols.Length} names, {program.Flags.Length} flags");
        }

        stdout.WriteLine($"result     {errors + compileErrors} errors, {warnings + compileWarnings} warnings");
        return errors + compileErrors > 0 ? ExitCodes.Mismatch : ExitCodes.Success;
    }

    private static (MemoryAiScriptSource Source, string? AiIni) Load(string input)
    {
        var source = new MemoryAiScriptSource();
        string? aiIni = null;
        if (File.Exists(input))
        {
            using PackageReader package = PackageReader.Open(input);
            const string Prefix = "text/data/ai/";
            foreach (PackageEntry entry in package.Entries)
            {
                if (entry.Path.StartsWith(Prefix, StringComparison.Ordinal) && IsScript(entry.Path))
                {
                    source.Add(entry.Path[Prefix.Length..], package.ReadText(entry.Path));
                }
            }

            return (source, null);
        }

        if (!Directory.Exists(input))
        {
            throw new IOException("no such file or folder");
        }

        foreach (string path in Directory.EnumerateFiles(input).Order(StringComparer.Ordinal))
        {
            string file = Path.GetFileName(path);
            if (IsScript(file))
            {
                source.Add(file, File.ReadAllText(path, Encoding.Latin1));
            }
            else if (string.Equals(file, "ai.ini", StringComparison.OrdinalIgnoreCase))
            {
                aiIni = File.ReadAllText(path, Encoding.Latin1);
            }
        }

        return (source, aiIni);
    }

    private static bool IsScript(string name)
        => name.EndsWith(".ai", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ais", StringComparison.OrdinalIgnoreCase);

    private static void Report(IEnumerable<AiDiagnostic> diagnostics, TextWriter stdout, ref int errors, ref int warnings)
    {
        foreach (AiDiagnostic d in diagnostics)
        {
            stdout.WriteLine(d.ToString());
            if (d.Severity == AiDiagnosticSeverity.Error)
            {
                errors++;
            }
            else
            {
                warnings++;
            }
        }
    }

    private static int CountRules(IEnumerable<AiItem> items)
        => items.Sum(i => i switch { AiRuleItem => 1, AiBlockItem b => CountRules(b.Items), _ => 0 });

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("Usage: t2 ai check <folder | package.t2pkg> [--bind] [--civ <key>]");
        output.WriteLine();
        output.WriteLine("Parses every legacy AI script (.ai, .ais) of a folder (e.g. an install's DATA/AI or a mod) or of a");
        output.WriteLine("package (text/data/ai/), compiles the strategy scripts with their includes and prints every message");
        output.WriteLine("with file, line and column. Exit code 1 if there is an error.");
        output.WriteLine();
        output.WriteLine("  --bind        also bind the names against the classic ruleset (synthetic tables) and list unknown ones");
        output.WriteLine("  --civ <key>   civilization to bind for: euro (default), asia, arab");
    }
}
