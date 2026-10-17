using System;
using System.Collections.Generic;

namespace Tzar2.Tools.Cli.Fidelity.Analysis;

/// <summary>The analyses a scenario can request (<c>analysis:</c> list); each turns captures into facts.</summary>
internal static class AnalysisCatalog
{
    public static readonly IReadOnlySet<string> Names = new SortedSet<string>(StringComparer.Ordinal)
    {
        "capture-quality",
        "colour-depth",
        "terrain-tiles",
        "shadow-blend",
        "player-colours",
        "unit-speed",
        "attack-timing",
    };
}
