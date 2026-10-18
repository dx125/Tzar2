using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Formats.Maps;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Formats.Tests;

/// <summary>
/// RE-03/RE-04 checks of the map reader against the reference build: every map (7 top-level, 26 in the 2 campaigns)
/// decodes with zero warnings, every trigger record decodes, and the counts in docs/formats/wmp.md hold. Skipped
/// without <c>TZAR2_ORIGIN</c>.
/// </summary>
public class OriginMapTests
{
    private static IEnumerable<(string Label, LegacyMap Map)> AllMaps()
    {
        string root = OriginPaths.RequireOrigin();
        foreach (string file in Directory.EnumerateFiles(root, "*.wmp", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            yield return (Path.GetFileName(file), LegacyMapReader.ReadMap(File.ReadAllBytes(file)));
        }

        foreach (string file in Directory.EnumerateFiles(root, "*.wcm", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            LegacyCampaign campaign = LegacyMapReader.ReadCampaign(File.ReadAllBytes(file));
            foreach (LegacyFile map in campaign.MapFiles)
            {
                yield return (Path.GetFileName(file) + "/" + map.Path, LegacyMapReader.ReadMap(map.Data));
            }
        }
    }

    [OriginFact]
    public void All_33_maps_decode_without_warnings()
    {
        var maps = AllMaps().ToList();
        Assert.Equal(33, maps.Count);
        foreach ((string label, LegacyMap map) in maps)
        {
            Assert.True(map.Warnings.IsEmpty, label + ": " + string.Join("; ", map.Warnings.Select(w => w.Message)));
            Assert.All(map.Events.Concat(map.Actions), t => Assert.True(t.IsKnown && !t.Fields.IsDefaultOrEmpty || t.Type is 10 or 17, $"{label}: {t.Name}"));
            Assert.All(map.Objects, o => Assert.True(o.ClassIndex > 0, $"{label}: object {o.Index}"));
            Assert.Equal(map.Side * map.Side, map.Tiles.Length);
        }

        Assert.Equal(26_339, maps.Sum(m => m.Map.Objects.Length));
        Assert.Equal(276, maps.Sum(m => m.Map.Events.Length));
        Assert.Equal(1_195, maps.Sum(m => m.Map.Actions.Length));
        Assert.Equal(399, maps.Sum(m => m.Map.Rules.Length));
    }

    [OriginFact]
    public void Campaigns_list_their_maps_and_entry_maps()
    {
        string root = OriginPaths.RequireOrigin();
        foreach (string file in Directory.EnumerateFiles(root, "*.wcm", SearchOption.AllDirectories))
        {
            LegacyCampaign c = LegacyMapReader.ReadCampaign(File.ReadAllBytes(file));
            Assert.NotEmpty(c.Title);
            Assert.Equal(c.Maps.Length, c.MapFiles.Length);
            Assert.Single(c.Maps, m => m.Entry);
        }
    }
}
