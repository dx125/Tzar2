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

    // Floating point reached only through locals, parameters, patterns, out-declarations or default values.
    [Theory]
    [InlineData("public static class C { public static long M(object o) { if (o is double d) return (long)d; return 0; } }")]
    [InlineData("public static class C { public static long M(string s) { double.TryParse(s, out var d); return (long)d; } }")]
    [InlineData("public static class C { public static long M(object o) => o switch { double d => (long)d, _ => 0 }; }")]
    [InlineData("public static class C { public static long M() => (long)default(double); }")]
    [InlineData("public static class C { public static bool M(object o) => o is double; }")]
    [InlineData("public static class C { public static bool M(object o) => o is double and not double.NaN; }")]
    [InlineData("public static class C { public static bool M(object o) => o switch { float => true, _ => false }; }")]
    [InlineData("public static class C { public static bool M(object o) => o is double { }; }")]
    [InlineData("public static class C { public static bool M(string s) => double.TryParse(s, out double _); }")]
    public void Floating_point_through_locals_and_patterns_is_reported(string source)
        => Assert.Contains(Descriptors.FloatingPoint.Id, Ids(Analyze(source)));

    [Fact]
    public void Floating_point_local_bypass_is_reported_at_the_local()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("public static class C { public static long M(object o) { if (o is double d) return (long)d; return 0; } }");
        Assert.Contains("double d", Spans(diagnostics, "T2D001"));
        Assert.Contains("d", Spans(diagnostics, "T2D001"));
    }

    [Fact]
    public void Integer_patterns_and_locals_are_clean()
        => Assert.Empty(Analyze("""
            public static class C
            {
                public static long M(object o, string s, int p)
                {
                    long r = p;
                    if (o is int i) { r += i; }
                    r += o switch { long l => l, _ => default(int) };
                    int.TryParse(s, out var parsed);
                    return r + parsed + (o is string ? 1 : 0);
                }
            }
            """));

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
    [InlineData("System.Collections.Generic.IReadOnlyDictionary<int, int>", "d.Keys")]
    [InlineData("System.Collections.Generic.IReadOnlyDictionary<int, int>", "d.Values")]
    [InlineData("System.Collections.Generic.IDictionary<int, int>", "d.Keys")]
    [InlineData("System.Collections.Generic.IDictionary<int, int>", "d.Values")]
    [InlineData("System.Collections.Concurrent.ConcurrentDictionary<int, int>", "d.Keys")]
    [InlineData("System.Collections.Concurrent.ConcurrentDictionary<int, int>", "d.Values")]
    [InlineData("System.Collections.Immutable.ImmutableDictionary<int, int>", "d.Keys")]
    [InlineData("System.Collections.Immutable.ImmutableDictionary<int, int>", "d.Values")]
    [InlineData("System.Collections.Frozen.FrozenDictionary<int, int>", "d.Keys")]
    [InlineData("System.Collections.Frozen.FrozenDictionary<int, int>", "d.Values")]
    [InlineData("Derived", "d.Keys")]
    [InlineData("System.Collections.Generic.Dictionary<int, int>", "(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<int, int>>)d")]
    [InlineData("System.Collections.Generic.HashSet<int>", "(System.Collections.Generic.IEnumerable<int>)d")]
    [InlineData("System.Collections.Generic.IDictionary<int, int>", "(System.Collections.Generic.IEnumerable<int>)d.Keys")]
    public void Foreach_over_keys_values_and_casts_of_unordered_collections_is_reported(string type, string collection)
        => Foreach_over_unordered_collections_is_reported(type, collection);

    [Theory]
    [InlineData("System.Collections.Generic.SortedDictionary<int, int>", "d.Keys")]
    [InlineData("System.Collections.Generic.SortedDictionary<int, int>", "d.Values")]
    [InlineData("System.Collections.Generic.SortedList<int, int>", "d.Keys")]
    [InlineData("System.Collections.Generic.List<int>", "(System.Collections.Generic.IEnumerable<int>)d")]
    public void Foreach_over_keys_of_ordered_collections_is_clean(string type, string collection)
        => Assert.Empty(Analyze($$"""
            public static class C
            {
                public static int M({{type}} d)
                {
                    int n = 0;
                    foreach (var item in {{collection}}) { n++; }
                    return n;
                }
            }
            """));

    [Theory]
    [InlineData("public static class C { public static int M(int a, int b) => System.HashCode.Combine(a, b); }")]
    [InlineData("public static class C { public static int M(int a) { var h = new System.HashCode(); h.Add(a); return h.ToHashCode(); } }")]
    [InlineData("public static class C { public static int M(string s) => s.GetHashCode(); }")]
    [InlineData("public static class C { public static int M(string s) => s.GetHashCode(System.StringComparison.Ordinal); }")]
    [InlineData("public static class C { public static int M(string s) => string.GetHashCode(s.AsSpan()); }")]
    [InlineData("public static class C { public static int M(string s) => ((object)s).GetHashCode(); }")]
    [InlineData("public static class C { public static int M(string s) => System.StringComparer.Ordinal.GetHashCode(s); }")]
    [InlineData("public static class C { public static int M(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }")]
    [InlineData("public static class C { public static string M() => System.IO.Path.GetRandomFileName(); }")]
    public void Nondeterministic_hashing_is_reported(string source)
        => Assert.Equal([Descriptors.RandomSource.Id], Ids(Analyze("using System;\n" + source)));

    [Fact]
    public void Hash_codes_of_integers_and_ids_are_allowed()
        => Assert.Empty(Analyze("public static class C { public static int M(int i, long l) => i.GetHashCode() ^ l.GetHashCode(); }"));

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
    [InlineData("public static class C { public static object M() => new System.IO.StreamReader(\"a\"); }")]
    [InlineData("public static class C { public static object M() => new System.Net.Http.HttpClient(); }")]
    [InlineData("public static class C { public static object M() => System.Net.Dns.GetHostName(); }")]
    [InlineData("public static class C { public static object M() => System.IO.Compression.ZipFile.OpenRead(\"a\"); }")]
    [InlineData("public static class C { public static object M() => System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(\"a\"); }")]
    [InlineData("public static class C { public static string? M() => System.Console.ReadLine(); }")]
    [InlineData("public static class C { public static int M() => System.Console.Read(); }")]
    [InlineData("public static class C { public static object? M() => System.Diagnostics.Process.Start(\"a\"); }")]
    [InlineData("public static class C { public static object M() => new System.IO.StreamWriter(\"a\"); }")]
    [InlineData("public static class C { public static object M() => new System.IO.Pipes.AnonymousPipeServerStream(); }")]
    public void IO_is_reported_in_no_io_assemblies(string source)
    {
        Assert.Equal([Descriptors.InputOutput.Id], Ids(Analyze(source, deterministic: true, noIO: true)));
        Assert.Empty(Analyze(source, deterministic: true, noIO: false));
    }

    [Theory]
    [InlineData("public static class C { public static object M() => new System.IO.MemoryStream(); }")]
    [InlineData("public static class C { public static object M(byte[] b) => new System.IO.MemoryStream(b, writable: false); }")]
    [InlineData("public static class C { public static int M(byte[] b) { using var r = new System.IO.BinaryReader(new System.IO.MemoryStream(b)); return r.ReadInt32(); } }")]
    [InlineData("public static class C { public static object M(System.IO.Stream s) => new System.IO.StreamReader(s); }")]
    [InlineData("public static class C { public static object M(System.IO.Stream s) => new System.IO.Compression.GZipStream(s, System.IO.Compression.CompressionMode.Decompress); }")]
    [InlineData("public static class C { public static object M() => System.Net.IPAddress.Loopback; }")]
    [InlineData("public static class C { public static object M() => new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0); }")]
    [InlineData("public sealed class S : System.IO.MemoryStream { } public static class C { public static object M() => new S(); }")]
    public void In_memory_streams_and_address_values_are_allowed_in_no_io_assemblies(string source)
        => Assert.Empty(Analyze(source, deterministic: true, noIO: true));

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
