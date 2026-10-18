using System.IO;
using System.Linq;
using Tzar2.Content.Rules;
using Tzar2.Tools.Cli.Fidelity.Suite;
using Tzar2.Tools.Cli.Fidelity.Yaml;
using Xunit;

namespace Tzar2.Fidelity.Tests;

/// <summary>
/// Every hand-read value in <c>manual.yaml</c> names its capture session, trial, frame file, reader and date (ADR 0113);
/// an entry without them is rejected here (and ignored by the suite).
/// </summary>
public sealed class ManualFactsProvenanceTests
{
    [FidelityFact]
    public void Every_hand_read_value_has_full_provenance()
    {
        string path = Path.Combine(FactsLocation.Directory, "manual.yaml");
        if (!File.Exists(path))
        {
            return;
        }

        var doc = (YamlMapping)MiniYaml.Parse(File.ReadAllText(path));
        var analyses = (YamlMapping)doc.Get("analyses")!;
        Assert.NotEmpty(analyses.Entries);
        var problems = analyses.Entries
            .Where(a => a.Value is YamlMapping m && m.Get("facts") is YamlMapping)
            .SelectMany(a => ManualProvenance.Missing((YamlMapping)((YamlMapping)a.Value).Get("facts")!).Select(p => $"{a.Key}.{p}"))
            .ToList();
        Assert.True(problems.Count == 0, "manual.yaml values without provenance: " + string.Join("; ", problems));
    }
}
