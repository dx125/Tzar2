using System;
using System.IO;
using System.Linq;
using Tzar2.Tests.Shared;
using Tzar2.Tools.Cli.Fidelity.Suite;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Fidelity.Tests;

/// <summary>Where the capture facts are: <c>TZAR2_FIDELITY_FACTS</c>, else the committed <c>tests/fidelity/facts</c>.</summary>
internal static class FactsLocation
{
    public static string Directory { get; } =
        Environment.GetEnvironmentVariable("TZAR2_FIDELITY_FACTS") is { Length: > 0 } p ? p : Path.Combine(OriginPaths.RepositoryRoot, "tests", "fidelity", "facts");

    public static bool Present => System.IO.Directory.Exists(Directory) && System.IO.Directory.EnumerateFiles(Directory, "*.yaml").Any();

    private static readonly Lazy<FactsIndex> LazyFacts = new(() => new FactsIndex(Directory), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<EngineModel> LazyEngine = new(() => EngineModel.Load(OriginPaths.RepositoryRoot), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Loaded once, thread-safely, for every test of the process.</summary>
    public static FactsIndex Facts => LazyFacts.Value;

    public static EngineModel Engine => LazyEngine.Value;
}

/// <summary>Runs only when capture facts exist (like the package-gated skirmish tests: local inputs, no original data).</summary>
public sealed class FidelityFactAttribute : FactAttribute
{
    public FidelityFactAttribute()
    {
        if (!FactsLocation.Present)
        {
            Skip = $"No capture facts in {FactsLocation.Directory}: run t2 fidelity analyze on a session (ADR 0030) or set TZAR2_FIDELITY_FACTS.";
        }
    }
}

/// <summary>The theory form of <see cref="FidelityFactAttribute"/>.</summary>
public sealed class FidelityTheoryAttribute : TheoryAttribute
{
    public FidelityTheoryAttribute()
    {
        if (!FactsLocation.Present)
        {
            Skip = $"No capture facts in {FactsLocation.Directory}.";
        }
    }
}

/// <summary>
/// The fidelity suite (TDD §22.3, ADR 0112): every measured fact of the original must match the engine. A check whose
/// fact is not captured yet is pending and appears only in the report; a measured check that fails turns this red until
/// the engine follows the measurement (or an ADR explains the deviation).
/// </summary>
public sealed class FidelitySuiteTests(ITestOutputHelper output)
{
    public static TheoryData<string> MeasuredChecks()
    {
        var data = new TheoryData<string>();
        if (!FactsLocation.Present)
        {
            data.Add("none");
            return data;
        }

        foreach ((FidelityCheck check, CheckResult result) in FidelitySuite.Evaluate(FactsLocation.Facts, FactsLocation.Engine))
        {
            if (result.Status != CheckStatus.Pending)
            {
                data.Add(check.Id);
            }
        }

        if (data.Count == 0)
        {
            data.Add("none");
        }

        return data;
    }

    [FidelityTheory]
    [MemberData(nameof(MeasuredChecks))]
    [Trait("Rule", "ECO-015")]
    [Trait("Rule", "ECO-016")]
    [Trait("Rule", "WORLD-007")]
    [Trait("Rule", "WORLD-008")]
    [Trait("Rule", "NAT-005")]
    [Trait("Rule", "PROD-009")]
    public void Measured_fact_matches_the_engine(string id)
    {
        if (id == "none")
        {
            return;
        }

        FidelityCheck check = FidelitySuite.Checks.Single(c => c.Id == id);
        CheckResult result = check.Evaluate(FactsLocation.Facts, FactsLocation.Engine);
        output.WriteLine($"{check.Unknown} {check.Title}: {result.Detail}");
        Assert.True(result.Status == CheckStatus.Pass, $"{id} ({check.Unknown}): {result.Detail}");
    }

    [FidelityFact]
    public void The_report_covers_every_area_and_the_engine_reads_its_own_values()
    {
        var writer = new StringWriter();
        var results = FidelitySuite.Evaluate(FactsLocation.Facts, FactsLocation.Engine);
        FidelitySuite.WriteReport(results, writer);
        output.WriteLine(writer.ToString());
        Assert.All(FidelitySuite.Areas.Concat(FidelitySuite.VisualAreas), a => Assert.Contains(a, writer.ToString(), StringComparison.Ordinal));
        Assert.Contains("M3 GATE", writer.ToString(), StringComparison.Ordinal);
        Assert.Equal(FidelitySuite.Checks.Count, FidelitySuite.Checks.Select(c => c.Id).Distinct().Count());
        Assert.NotNull(FactsLocation.Engine.ShadowStrength);
        Assert.NotNull(FactsLocation.Engine.PeasantLoad);
    }
}
