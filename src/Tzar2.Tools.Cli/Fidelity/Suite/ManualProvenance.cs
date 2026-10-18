using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Rules;

namespace Tzar2.Tools.Cli.Fidelity.Suite;

/// <summary>
/// Provenance of hand-read values (<c>tests/fidelity/facts/manual.yaml</c>, ADR 0113): every value of a block needs an
/// entry under <c>sources</c> naming the capture session, the trial, the frame file, who read it and when (UTC date). A
/// list value names its frames in order (e.g. <c>xp_after_kills</c>: <c>window/panel-0.bmp … window/panel-12.bmp</c>). A
/// block with a value lacking any of these is ignored by the suite (its checks stay pending) and fails
/// <c>ManualFactsProvenanceTests</c>.
/// </summary>
internal static class ManualProvenance
{
    public static readonly string[] Fields = ["session", "trial", "frame", "read_by", "read_utc"];

    /// <summary>Problems of <paramref name="facts"/>: value keys without complete provenance, with the missing field.</summary>
    public static IReadOnlyList<string> Missing(YamlMapping facts)
    {
        var sources = facts.Get("sources") as YamlMapping;
        var missing = new List<string>();
        foreach ((string key, YamlNode _) in facts.Entries)
        {
            if (key is "sources" or "source" or "note")
            {
                continue;
            }

            if (sources?.Get(key) is not YamlMapping source)
            {
                missing.Add($"{key}: no sources entry");
                continue;
            }

            missing.AddRange(Fields.Where(field => string.IsNullOrWhiteSpace(FactsIndex.Text(source.Get(field)))).Select(field => $"{key}: no {field}"));
        }

        return missing;
    }
}
