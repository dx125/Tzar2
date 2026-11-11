using System;
using System.Linq;
using Tzar2.Client.Logic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
using Xunit;

namespace Tzar2.Client.Tests;

public class RuinVisualTests
{
    [Fact]
    [Trait("Rule", "CAP-007")]
    public void A_ruin_is_drawn_by_its_own_picture()
    {
        // ADR 0283: the ruin class draws nothing of its own (OBJECTS\RUIN.INI: anim_none); each ruin type is its picture.
        MemorySource tables = SyntheticLegacyContent.CreateTables();
        tables.Add("tables/data/objects/ruin.ini.json", PresentationCatalogTests.Ini(("general", [("class", "ruin")]), ("comm", [("image", "anim_none")])));
        var vfs = new VirtualFileSystem();
        vfs.Mount(new EngineDefaultsSource());
        vfs.Mount(tables);
        Ruleset rules = RulesetCompiler.Compile(vfs, "classic").Ruleset;
        var catalog = new PresentationCatalog(rules, p => tables.Contains(p) ? tables.ReadAllBytes(p) : null, tables.Paths.Where(p => p.StartsWith(PresentationCatalog.ObjectsFolder, StringComparison.Ordinal)));
        Assert.Equal("gr_statue_1", catalog.Type((ushort)rules.EntityTypeId("comm.gr_statue_1")).Image(UnitAnim.Base));
    }
}
