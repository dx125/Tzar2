using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Tzar2.Analyzers.Tests;

/// <summary>Compiles a snippet against the test host's framework assemblies and runs <see cref="DeterminismAnalyzer"/> (or another analyzer) on it.</summary>
internal static class AnalyzerHarness
{
    private const string Attributes = """
        namespace Tzar2.Core
        {
            [System.AttributeUsage(System.AttributeTargets.Assembly)] public sealed class Tzar2DeterministicAttribute : System.Attribute { }
            [System.AttributeUsage(System.AttributeTargets.Assembly)] public sealed class Tzar2NoIOAttribute : System.Attribute { }
        }
        """;

    private static readonly ImmutableArray<MetadataReference> References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(path) is "mscorlib.dll" or "netstandard.dll")
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToImmutableArray();

    public static ImmutableArray<Diagnostic> Analyze(string source, bool deterministic = true, bool noIO = false, DiagnosticAnalyzer? analyzer = null)
    {
        string markers = (deterministic ? "[assembly: Tzar2.Core.Tzar2Deterministic]\n" : string.Empty)
            + (noIO ? "[assembly: Tzar2.Core.Tzar2NoIO]\n" : string.Empty);
        CSharpParseOptions options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Snippet",
            [
                CSharpSyntaxTree.ParseText(markers + Attributes, options),
                CSharpSyntaxTree.ParseText(source, options),
            ],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        Diagnostic[] compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (compileErrors.Length > 0)
        {
            throw new InvalidOperationException("Snippet does not compile:\n" + string.Join("\n", compileErrors.Select(d => d.ToString())));
        }

        return compilation
            .WithAnalyzers(ImmutableArray.Create(analyzer ?? new DeterminismAnalyzer()))
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>The distinct rule IDs reported, sorted.</summary>
    public static string[] Ids(ImmutableArray<Diagnostic> diagnostics)
        => diagnostics.Select(d => d.Id).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();

    /// <summary>The source text each diagnostic of <paramref name="id"/> points at.</summary>
    public static string[] Spans(ImmutableArray<Diagnostic> diagnostics, string id)
        => diagnostics.Where(d => d.Id == id)
            .Select(d => d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
}
