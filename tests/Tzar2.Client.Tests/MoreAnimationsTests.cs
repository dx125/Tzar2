using System;
using System.Linq;
using Tzar2.Client.Logic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
using Xunit;

namespace Tzar2.Client.Tests;

[Trait("Rule", "PRES-005")]
public class MoreAnimationsTests
{
    [Fact]
    public void The_peasants_capture_and_meat_pictures_come_from_its_table()
    {
        MemorySource tables = SyntheticLegacyContent.CreateTables();
        tables.Add("tables/data/objects/peon.ini.json", PresentationCatalogTests.Ini(
            ("general", [("class", "peon"), ("type", "unit")]),
            ("euro", [("image", "e_peon"), ("image_capture", "e_peon_dig"), ("image_meat", "e_peon_meat"), ("image_attack2", "e_peon_attack2"), ("image_spell", "e_x")])));
        var vfs = new VirtualFileSystem();
        vfs.Mount(new EngineDefaultsSource());
        vfs.Mount(tables);
        Ruleset rules = RulesetCompiler.Compile(vfs, "classic").Ruleset;
        var catalog = new PresentationCatalog(rules, p => tables.Contains(p) ? tables.ReadAllBytes(p) : null, tables.Paths.Where(p => p.StartsWith(PresentationCatalog.ObjectsFolder, StringComparison.Ordinal)));
        TypeVisual peon = catalog.Type((ushort)rules.EntityTypeId("euro.peon"));
        Assert.Equal(("e_peon_dig", "e_peon_meat", "e_peon_attack2", "e_x"), (peon.Image(UnitAnim.Capture), peon.Image(UnitAnim.Meat), peon.Image(UnitAnim.Attack2), peon.Image(UnitAnim.Spell)));
        Assert.Equal("e_peon", peon.Image(UnitAnim.Attack3)); // none: the base picture
    }

    [Fact]
    public void Half_the_blows_of_a_unit_with_a_second_swing_use_it()
    {
        int second = Enumerable.Range(0, 10_000).Count(b => SpriteRules.Swing(true, (uint)b) == UnitAnim.Attack2);
        Assert.InRange(second, 4_500, 5_500);
        Assert.All(Enumerable.Range(0, 100), b => Assert.Equal(UnitAnim.Attack, SpriteRules.Swing(false, (uint)b)));
    }
}
