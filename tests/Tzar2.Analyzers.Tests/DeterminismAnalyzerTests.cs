using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit;
using static Tzar2.Analyzers.Tests.AnalyzerHarness;

namespace Tzar2.Analyzers.Tests;

public class DeterminismAnalyzerTests
{
    [Fact]
    public void Clean_integer_code_has_no_diagnostics()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            using System.Collections.Generic;
            public static class Clean
            {
                public static long Sum(List<int> values, SortedDictionary<int, int> map)
                {
                    long sum = 0;
                    foreach (int v in values) { sum += v; }
                    foreach (var kv in map) { sum += kv.Value; }
                    var lookup = new Dictionary<int, int>();
                    lookup[1] = 2;
                    return sum + lookup[1] + System.Math.Abs(-3) + System.Math.Max(1, 2);
                }
            }
            """, deterministic: true, noIO: true);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Unmarked_assembly_is_not_analyzed()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            public static class Free
            {
                public static double Now() => System.DateTime.Now.Ticks * 1.5 + new System.Random().NextDouble();
                public static string Read() => System.IO.File.ReadAllText("x");
            }
            """, deterministic: false, noIO: false);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("public static class C { public static double F; }")]
    [InlineData("public static class C { public static float P { get; set; } }")]
    [InlineData("public static class C { public static int M(decimal d) => 0; }")]
    [InlineData("public static class C { public static int M() { var x = 1.5; return 0; } }")]
    [InlineData("public static class C { public static int M(int i) => (int)System.Math.Sqrt(i); }")]
    [InlineData("public static class C { public static int M(int i) => (int)System.MathF.Sqrt(i); }")]
    [InlineData("public static class C { public static object M() => new System.Numerics.Vector2(1, 2); }")]
    [InlineData("public static class C { public static System.Collections.Generic.List<double>? L; }")]
    [InlineData("public static class C { public static int M(int i) => (int)(i * 0.5f); }")]
    [InlineData("public static class C { public static System.Half H; }")]
    [InlineData("public static class C { public static System.Type T() => typeof(double); }")]
    public void Floating_point_is_reported(string source)
        => Assert.Contains(Descriptors.FloatingPoint.Id, Ids(Analyze(source)));

    [Fact]
    public void Floating_point_expression_is_reported_once_at_its_outermost_node()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("public static class C { public static int M(int i) => (int)(i * 2.5 + 1.0); }");
        Assert.Equal(["i * 2.5 + 1.0"], Spans(diagnostics, "T2D001"));
    }

    [Theory]
    [InlineData("public static class C { public static int M() => new System.Random(1).Next(); }")]
    [InlineData("public static class C { public static int M() => System.Random.Shared.Next(); }")]
    [InlineData("public static class C { public static System.Guid M() => System.Guid.NewGuid(); }")]
    [InlineData("public static class C { public static int M() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(5); }")]
    public void Random_sources_are_reported(string source)
        => Assert.Equal([Descriptors.RandomSource.Id], Ids(Analyze(source)));

    [Theory]
    [InlineData("public static class C { public static long M() => System.DateTime.Now.Ticks; }")]
    [InlineData("public static class C { public static long M() => System.DateTime.UtcNow.Ticks; }")]
    [InlineData("public static class C { public static long M() => System.DateTime.Today.Ticks; }")]
    [InlineData("public static class C { public static long M() => System.DateTimeOffset.UtcNow.Ticks; }")]
    [InlineData("public static class C { public static long M() => System.Environment.TickCount64; }")]
    [InlineData("public static class C { public static int M() => System.Environment.TickCount; }")]
    [InlineData("public static class C { public static long M() => System.Diagnostics.Stopwatch.GetTimestamp(); }")]
    [InlineData("public static class C { public static long M() { var sw = System.Diagnostics.Stopwatch.StartNew(); return sw.ElapsedTicks; } }")]
    [InlineData("public static class C { public static long M() => System.TimeProvider.System.GetTimestamp(); }")]
    public void Wall_clock_is_reported(string source)
        => Assert.Equal([Descriptors.WallClock.Id], Ids(Analyze(source)));

    [Fact]
    public void DateTime_arithmetic_without_clock_is_allowed()
        => Assert.Empty(Analyze("public static class C { public static long M(System.DateTime d) => d.AddTicks(1).Ticks + System.DateTime.MinValue.Ticks; }"));

    [Theory]
    [InlineData("System.Collections.Generic.Dictionary<int, int>", "d")]
    [InlineData("System.Collections.Generic.Dictionary<int, int>", "d.Keys")]
    [InlineData("System.Collections.Generic.Dictionary<int, int>", "d.Values")]
    [InlineData("System.Collections.Generic.HashSet<int>", "d")]
    [InlineData("System.Collections.Generic.IReadOnlyDictionary<int, int>", "d")]
    [InlineData("System.Collections.Generic.ISet<int>", "d")]
    [InlineData("System.Collections.Concurrent.ConcurrentDictionary<int, int>", "d")]
    [InlineData("System.Collections.Immutable.ImmutableHashSet<int>", "d")]
    [InlineData("Derived", "d")]
    public void Foreach_over_unordered_collections_is_reported(string type, string collection)
    {
        string source = $$"""
            public sealed class Derived : System.Collections.Generic.Dictionary<int, int> { }
            public static class C
            {
                public static int M({{type}} d)
                {
                    int n = 0;
                    foreach (var item in {{collection}}) { n++; }
                    return n;
                }
            }
            """;
        ImmutableArray<Diagnostic> diagnostics = Analyze(source);
        Assert.Equal([Descriptors.UnorderedIteration.Id], Ids(diagnostics));
        Assert.Equal([collection], Spans(diagnostics, "T2D004"));
    }

    [Theory]
    [InlineData("public static class C { public static object M() => new System.Reflection.Emit.DynamicMethod(\"m\", typeof(void), null); }")]
    [InlineData("public static class C { public static System.Func<int> M() { System.Linq.Expressions.Expression<System.Func<int>> e = () => 1; return e.Compile(); } }")]
    [InlineData("public static class C { public static System.Delegate M(System.Linq.Expressions.LambdaExpression e) => e.Compile(); }")]
    public void Runtime_code_generation_is_reported(string source)
        => Assert.Equal([Descriptors.CodeGeneration.Id], Ids(Analyze(source)));

    [Theory]
    [InlineData("public static class C { public static string M() => System.IO.File.ReadAllText(\"a\"); }")]
    [InlineData("public static class C { public static bool M() => System.IO.Directory.Exists(\"a\"); }")]
    [InlineData("public static class C { public static long M() => new System.IO.FileInfo(\"a\").Length; }")]
    [InlineData("public static class C { public static object M() => new System.IO.FileStream(\"a\", System.IO.FileMode.Open); }")]
    [InlineData("public static class C { public static object M() => new System.IO.MemoryStream(); }")]
    [InlineData("public static class C { public static object M() => new System.IO.StreamReader(\"a\"); }")]
    [InlineData("public static class C { public static object M() => new System.Net.Http.HttpClient(); }")]
    [InlineData("public static class C { public static object M() => System.Net.Dns.GetHostName(); }")]
    public void IO_is_reported_in_no_io_assemblies(string source)
    {
        Assert.Equal([Descriptors.InputOutput.Id], Ids(Analyze(source, deterministic: true, noIO: true)));
        Assert.Empty(Analyze(source, deterministic: true, noIO: false));
    }

    [Fact]
    public void Stream_parameters_are_allowed_in_no_io_assemblies()
        => Assert.Empty(Analyze("public static class C { public static int M(System.IO.Stream s) => s.ReadByte(); }", deterministic: true, noIO: true));

    [Fact]
    public void No_io_alone_does_not_enable_determinism_rules()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            "public static class C { public static double M() => System.IO.File.Exists(\"a\") ? 1.0 : System.DateTime.Now.Second; }",
            deterministic: false,
            noIO: true);
        Assert.Equal([Descriptors.InputOutput.Id], Ids(diagnostics));
    }

    [Fact]
    public void All_rules_are_errors()
    {
        foreach (DiagnosticDescriptor descriptor in new DeterminismAnalyzer().SupportedDiagnostics)
        {
            Assert.Equal(DiagnosticSeverity.Error, descriptor.DefaultSeverity);
            Assert.StartsWith("T2D", descriptor.Id, System.StringComparison.Ordinal);
        }
    }
}
