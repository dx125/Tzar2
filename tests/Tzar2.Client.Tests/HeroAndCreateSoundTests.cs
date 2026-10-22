using System;
using System.Linq;
using Tzar2.Client.Logic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Content.Vfs;
using Xunit;

namespace Tzar2.Client.Tests;

// PRES-006 (icon_hero) and PRES-007 (sound_create) of the object tables, on invented values.
public class HeroAndCreateSoundTests
{
    private static PresentationCatalog Catalog()
    {
        MemorySource tables = SyntheticLegacyContent.CreateTables();
        tables.Add("tables/data/objects/knight.ini.json", PresentationCatalogTests.Ini(
            ("general", [("class", "knight"), ("type", "unit")]),
            ("euro", [("image", "e_knight"), ("icon", "KNIGHT"), ("icon_hero", "HERO"), ("sound_create", "fanfare")])));
        tables.Add("tables/data/icons.ini.json", PresentationCatalogTests.Ini(("KNIGHT", [("index", "12")]), ("HERO", [("index", "90")])));
        var vfs = new VirtualFileSystem();
        vfs.Mount(new EngineDefaultsSource());
        vfs.Mount(tables);
        Ruleset rules = RulesetCompiler.Compile(vfs, "classic").Ruleset;
        return new PresentationCatalog(rules, p => tables.Contains(p) ? tables.ReadAllBytes(p) : null, tables.Paths.Where(p => p.StartsWith(PresentationCatalog.ObjectsFolder, StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Rule", "PRES-006")]
    public void A_hero_shows_the_hero_portrait_of_its_type()
    {
        PresentationCatalog catalog = Catalog();
        TypeVisual knight = catalog.Type((ushort)catalog.Ruleset.EntityTypeId("euro.knight"));
        Assert.Equal((12, 90), (knight.IconIndex, knight.HeroIconIndex));
        Assert.Equal(90, knight.Portrait(hero: true));
        Assert.Equal(12, knight.Portrait(hero: false));

        // A type without icon_hero keeps its own portrait for heroes too.
        TypeVisual peon = catalog.Type((ushort)catalog.Ruleset.EntityTypeId("euro.peon"));
        Assert.Equal(-1, peon.HeroIconIndex);
        Assert.Equal(peon.IconIndex, peon.Portrait(hero: true));
    }

    [Fact]
    [Trait("Rule", "PRES-007")]
    public void A_type_carries_its_creation_sound()
    {
        PresentationCatalog catalog = Catalog();
        Assert.Equal("fanfare", catalog.Type((ushort)catalog.Ruleset.EntityTypeId("euro.knight")).SoundCreate);
        Assert.Null(catalog.Type((ushort)catalog.Ruleset.EntityTypeId("euro.peon")).SoundCreate); // empty in the data
    }
}
