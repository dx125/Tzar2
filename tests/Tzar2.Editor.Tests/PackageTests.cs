using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Content.Packages;
using Tzar2.Import.Maps;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>Saving and loading <c>.t2map</c> through the package writers: deterministic and byte-identical (ADR 0151).</summary>
public class PackageTests
{
    private const int Tile = MapData.UnitsPerTile;

    internal static MapDocument Sample()
    {
        MapDocument doc = MapDocument.CreateEmpty("Ford", 24, 20, Fixtures.RulesFor("grass"));
        using (EditTransaction tx = doc.Begin("River"))
        {
            for (int y = 0; y <= 20; y++)
            {
                tx.Paint(11, y, 0);
                tx.Paint(12, y, 0);
            }

            tx.SetCliff(3, 3, true);
            tx.Commit();
        }

        int hero = 0, house = 0;
        doc.Apply("Objects", s => s
            .Add(new MapObject("euro.footman", 0, (4 * Tile) + 1024, (10 * Tile) + 1024), new ObjectInfo("Oren", "Captain Oren", ObjectFlags.Persistent, Level: 2), out hero)
            .Add(MapObject.AtTile("euro.house", 1, 18, 8), new ObjectInfo("Bandits", string.Empty, ObjectFlags.None, MaxHealth: 150), out house)
            .Add(MapObject.AtTile("comm.tree", MapObject.Nature, 2, 2), null, out _));
        doc.Apply("Scenario", s => s.WithScenario(m => (m with { Properties = m.Properties with { Title = "The Ford", Author = "Tzar2 tests", Description = "Cross the river." } })
            .WithKingdom(ScenarioModel.DefaultKingdom(1) with { StartTileX = 4, StartTileY = 10, Gold = 200 })
            .WithKingdom(ScenarioModel.DefaultKingdom(2) with { Civilization = "asia" })
            .WithDiplomacy(new DiplomacyDef(1, 2, Stance.Enemy, false, false, false))
            .AddEvent(new ObjectInAreaEvent("FarBank", new NamedObjects(new[] { hero }, Quantifier.All), new RectArea(14, 6, 20, 14)), out _)
            .AddEvent(new ObjectDestroyedEvent("HouseDown", new NamedObjects(new[] { house }, Quantifier.All)), out _)
            .AddAction(new ShowStoryScreenAction("Winter", 8000, true, "The river froze early — «зима».", new ResourceRef("user/winter.png"), null, null), out _)
            .AddAction(new PlayerWinAction("Won", PlayerMasks.Of(1), "Mill"), out _)
            .AddRule(new RuleDef("start", ScenarioModel.Source("1"), new[] { 0 }), out _)
            .AddRule(new RuleDef("end", ScenarioModel.Source("FarBank && HouseDown"), new[] { 1 }), out _))
            with
        {
            Assets = s.Assets.Add("user/winter.png", ImmutableArray.Create<byte>(1, 2, 3)),
            Decorations = s.Decorations.Add(new MapDecoration("ground_patch", "daisies", 5, 5, -1, -1)),
        });
        return doc;
    }

    [Fact]
    public void Saving_is_deterministic_and_a_load_save_round_trip_is_byte_identical()
    {
        byte[] first = MapPackage.Write(Sample());
        Assert.Equal(first, MapPackage.Write(Sample()));
        MapDocument loaded = MapPackage.Read(first, Fixtures.RulesFor);
        Assert.Equal(first, MapPackage.Write(loaded));
        Assert.False(loaded.IsDirty);

        // The loaded document is the same map.
        MapDocument original = Sample();
        Assert.Equal(original.BuildMap().ContentHash, loaded.BuildMap().ContentHash);
        Assert.Equal(original.BuildScenario().ContentHash, loaded.BuildScenario().ContentHash);
        Assert.Equal(original.State.Decorations, loaded.State.Decorations);
        Assert.Equal(3, loaded.State.Assets["user/winter.png"].Length);
    }

    [Fact]
    public void The_package_has_the_section_18_4_parts_and_no_editor_json_when_nothing_needs_it()
    {
        byte[] bytes = MapPackage.Write(Sample());
        using var reader = PackageReader.Open(new MemoryStream(bytes), "ford.t2map");
        Assert.True(reader.Contains("package.yaml"));
        Assert.True(reader.Contains("map.bin"));
        Assert.True(reader.Contains("scenario.json"));
        Assert.True(reader.Contains("decorations.json"));
        Assert.True(reader.Contains("assets/user/winter.png"));
        Assert.False(reader.Contains("editor.json"));
        PackageManifest manifest = PackageManifest.Parse(reader.ReadText("package.yaml"));
        Assert.Equal((PackageKind.Map, "map.ford", "The Ford"), (manifest.Kind, manifest.Id, manifest.Name));
        Assert.Contains("«зима»", reader.ReadText("scenario.json"), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Ids_areas_and_groups_survive_in_editor_json()
    {
        MapDocument doc = Sample();
        int first = doc.State.Objects[0].Id;
        doc.Apply("Delete", s => s.Remove([first]));
        doc.Apply("Extras", s => s with
        {
            Extras = s.Extras with
            {
                Areas = new[] { new NamedArea("Bank", new RectArea(14, 6, 20, 14)) },
                Groups = new[] { new ObjectGroup("Bandits", new[] { doc.State.Objects[0].Id }) },
            },
        });
        byte[] bytes = MapPackage.Write(doc);
        MapDocument loaded = MapPackage.Read(bytes, Fixtures.RulesFor);
        Assert.Equal(doc.State.Objects.Select(o => o.Id), loaded.State.Objects.Select(o => o.Id));
        Assert.Equal(doc.State.Extras, loaded.State.Extras);
        Assert.Equal(bytes, MapPackage.Write(loaded));
    }

    [Fact]
    public void A_converted_legacy_map_loads_and_saves_back_to_the_converters_exact_bytes()
    {
        byte[] legacy = Fixtures.LegacyMap("Hill", "Valley").Build();
        MapConversion conversion = LegacyMapConverter.Convert(legacy, "hill.wmp", new LegacyConversionOptions { Ruleset = Fixtures.Classic });
        Assert.True(conversion.Succeeded, conversion.Report.ToText());
        byte[] package = LegacyPackageWriter.WriteMap(conversion, "hill")!;
        MapDocument doc = MapPackage.Read(package, Fixtures.RulesFor);
        Assert.Equal(package, MapPackage.Write(doc));
        Assert.True(doc.Preserved.ContainsKey("provenance.json"));
        Assert.True(doc.Preserved.ContainsKey("conversion-report.json"));
        Assert.NotEmpty(doc.State.Decorations);

        // Editing and saving keeps the preserved parts verbatim.
        doc.Apply("Retitle", s => s.WithScenario(m => m with { Properties = m.Properties with { Title = "Hill (edited)" } }));
        using var reader = PackageReader.Open(new MemoryStream(MapPackage.Write(doc)), "hill.t2map");
        using var source = PackageReader.Open(new MemoryStream(package), "hill.t2map");
        Assert.Equal(source.ReadAllBytes("provenance.json"), reader.ReadAllBytes("provenance.json"));
        Assert.Contains("Hill (edited)", reader.ReadText("package.yaml"), System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_that_is_not_a_map_is_rejected()
    {
        var writer = new PackageWriter();
        writer.AddText("package.yaml", new PackageManifest { Id = "x", Name = "x", Version = "1.0.0", Kind = PackageKind.Campaign }.ToYaml());
        using var ms = new MemoryStream();
        writer.WriteTo(ms);
        Assert.Throws<InvalidDataException>(() => MapPackage.Read(ms.ToArray(), Fixtures.RulesFor));
        Assert.Throws<InvalidDataException>(() => MapPackage.Read(Encoding.UTF8.GetBytes("not a zip"), Fixtures.RulesFor));
    }
}
