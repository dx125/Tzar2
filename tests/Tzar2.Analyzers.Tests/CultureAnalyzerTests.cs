using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit;
using static Tzar2.Analyzers.Tests.AnalyzerHarness;

namespace Tzar2.Analyzers.Tests;

public class CultureAnalyzerTests
{
    [Theory]
    [InlineData("public static string M(double d) => $\"{d:0.##} s\";")]
    [InlineData("public static string M(float f) => $\"{f}\";")]
    [InlineData("public static string M(decimal m) => $\"x {m}\";")]
    [InlineData("public static string M(double? d) => $\"{d}\";")]
    [InlineData("public static string M(DateTime t) => $\"{t:yyyy-MM-dd}\";")]
    [InlineData("public static string M(int n) => $\"{n:N0} bytes\";")]
    [InlineData("public static void M(StringBuilder b, double d) => b.Append($\"{d}\");")]
    [InlineData("public static string M(double d) => \"x = \" + d;")]
    public void Culture_dependent_formatting_is_reported(string body)
        => Assert.Equal(["T2C001"], Ids(Run(body)));

    [Theory]
    [InlineData("public static string M(double d) => string.Create(CultureInfo.InvariantCulture, $\"{d:0.##} s\");")]
    [InlineData("public static string M(double d) => FormattableString.Invariant($\"{d}\");")]
    [InlineData("public static void M(StringBuilder b, double d) => b.Append(CultureInfo.InvariantCulture, $\"{d}\");")]
    [InlineData("public static string M(int n, string s) => $\"{n} {s} {n:X4} {n:D3} {n:000} {n,5}\";")]
    [InlineData("public static string M(int n) => \"n = \" + n;")]
    public void Invariant_or_culture_free_formatting_is_not_reported(string body)
        => Assert.Empty(Run(body));

    private static ImmutableArray<Diagnostic> Run(string body) => Analyze(
        "using System; using System.Globalization; using System.Text; public static class C { " + body + " }",
        deterministic: false,
        analyzer: new CultureAnalyzer());
}
