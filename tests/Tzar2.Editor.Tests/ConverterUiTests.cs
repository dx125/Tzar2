using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Formats.Text;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Editing;
using Tzar2.Import.Editing;
using Tzar2.Import.Maps;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>The legacy converter inside the editor (TDD §18.5, REQ-EDT-04…12) on invented legacy files.</summary>
public sealed class ConverterUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tzar2-import-" + Guid.NewGuid().ToString("N"));

    public ConverterUiTests() => Directory.CreateDirectory(Path.Combine(_dir, "in", "nested"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string relative, byte[] data)
    {
        string path = Path.Combine(_dir, "in", relative);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static ImportSettings Settings => new() { Ruleset = Fixtures.Classic };

    [Fact]
    public void Files_and_folders_queue_once_and_convert_with_reports()
    {
        string map = Write("hill.wmp", Fixtures.LegacyMap("Hill").Build());
        Write("nested/vale.WMP", Fixtures.LegacyMap("Vale").Build());
        Write("readme.txt", [1, 2, 3]);
        var session = new LegacyImportSession { Settings = Settings };
        Assert.Equal(1, session.Add(map));
        Assert.Equal(1, session.Add(Path.Combine(_dir, "in")));
        Assert.Equal(2, session.Files.Count);
        Assert.All(session.Convert(), i => Assert.True(i.Succeeded, i.Report?.ToText()));
        ImportItem hill = session.Items[0];
        Assert.Equal("hill.t2map", hill.OutputName);
        Assert.True(hill.Counts()["objects"] > 0);
        Assert.NotEmpty(hill.Rows());
        MapDocument doc = hill.OpenDocument(Fixtures.RulesFor);
        Assert.Equal(hill.Package, MapPackage.Write(doc));
    }

    [Fact]
    public void Output_goes_to_the_chosen_folder_and_sources_stay_untouched()
    {
        string source = Write("keep.wmp", Fixtures.LegacyMap("Keep").Build());
        byte[] md5 = MD5.HashData(File.ReadAllBytes(source));
        var session = new LegacyImportSession { Settings = Settings };
        session.Add(source);
        session.Convert();
        string[] written = session.Save(Path.Combine(_dir, "out")).ToArray();
        Assert.Equal([Path.Combine(_dir, "out", "keep.t2map")], written);
        Assert.Equal(md5, MD5.HashData(File.ReadAllBytes(source)));
        Assert.Contains(Convert.ToHexString(md5).ToLowerInvariant(), System.Text.Encoding.UTF8.GetString(session.Items[0].Package!), StringComparison.Ordinal);
    }

    [Fact]
    public void Report_rows_locate_objects_rules_events_and_actions()
    {
        SyntheticMap m = Fixtures.LegacyMap("Locate");
        m.Rules.Add(("broken", "1", ["missing"]));
        m.Objects.Add(new SyntheticObject { ClassName = "knight", X = 40, Y = 40, Owner = 1, Race = 1, Id = 9 });
        ImportItem item = LegacyImportSession.ConvertOne(Write("locate.wmp", m.Build()), Settings);
        ReportRow rule = item.Rows().Single(r => r.Code == "trigger.action");
        Assert.Equal(LocationKind.Rule, rule.Target.Kind);
        Assert.Equal("broken", item.Map!.Scenario!.Rules[rule.Target.Index].Name);
        Assert.Contains(item.Rows(), r => r.Code == "object.position");

        // A real object location resolves to the converted object's id and tile.
        MapConversion conversion = item.Map!;
        int legacy = conversion.LegacyObjectIndices.Keys.First();
        EditorLocation at = ReportLocations.Parse($"object #{legacy} (knight, id 1)", conversion);
        Assert.Equal(LocationKind.Object, at.Kind);
        Assert.Equal(conversion.LegacyObjectIndices[legacy] + 1, at.Index);
        Assert.Equal(new EditorLocation(LocationKind.Event, 0, Name: "castle_down"), ReportLocations.Parse("event 'castle_down'", conversion));
        Assert.Equal(LocationKind.Action, ReportLocations.Parse("action 'clock'", conversion).Kind);
        Assert.Equal(EditorLocation.Map, ReportLocations.Parse("something else", conversion));
    }

    [Fact]
    public void Code_pages_are_detected_and_can_be_overridden_cyrillic_and_spanish()
    {
        string ru = Write("ru.wmp", Fixtures.LegacyMap("Ru", codePage: CodePage.Windows1251, title: "Северный брод").Build());
        string es = Write("es.wmp", Fixtures.LegacyMap("Es", codePage: CodePage.Windows1252, title: "Castillo del Peñón").Build());
        ImportItem a = LegacyImportSession.ConvertOne(ru, Settings);
        ImportItem b = LegacyImportSession.ConvertOne(es, Settings);
        Assert.Equal(("Северный брод", 1251), (a.Map!.Scenario!.Properties.Title, a.Report!.CodePage));
        Assert.Equal("ru", a.Report.Language);
        Assert.Equal("Castillo del Peñón", b.Map!.Scenario!.Properties.Title);

        // Forcing the wrong code page gives other text; the user override is what the converter uses.
        ImportItem forced = LegacyImportSession.ConvertOne(ru, Settings with { CodePage = 1252 });
        Assert.NotEqual("Северный брод", forced.Map!.Scenario!.Properties.Title);
        Assert.Equal(1252, forced.Report!.CodePage);
        Assert.Equal([1250, 1251, 1252], ImportSettings.CodePages.ToArray());

        // Two accented letters in a row ("Peñón") are a Latin word, not a Cyrillic one.
        Assert.Equal(CodePage.Windows1252, CodePageDetector.Detect([LegacyText.Encode("Castillo del Peñón y la Señora", CodePage.Windows1252)]).CodePage);
        Assert.Equal(CodePage.Windows1251, CodePageDetector.Detect([LegacyText.Encode("Северный брод и мельница", CodePage.Windows1251)]).CodePage);
        Assert.Equal("es", CodePageDetector.Detect([LegacyText.Encode("Peñón del año", CodePage.Windows1252)]).Language);
    }

    [Fact]
    public void Unknown_types_become_placeholders_until_the_mapping_table_resolves_them()
    {
        SyntheticMap m = Fixtures.LegacyMap("Mod");
        m.Objects.Add(new SyntheticObject { ClassName = "evilcastle", X = 8, Y = 8, Owner = 2, Race = 0, Id = 30 });
        string file = Write("mod.wmp", m.Build());
        var session = new LegacyImportSession { Settings = Settings };
        session.Add(file);
        session.Convert();
        Assert.Equal(["evilcastle"], session.Unresolved().ToArray());
        Assert.NotEmpty(session.Items[0].Map!.Placeholders);

        TypeMappingTable table = TypeMappingTable.Empty.With("evilcastle", "euro.castle");
        string yaml = table.ToYaml();
        Assert.Equal(table.Entries, TypeMappingTable.Parse(yaml).Entries);
        session.Settings = session.Settings with { Mapping = TypeMappingTable.Parse(yaml) };
        session.Convert();
        Assert.Empty(session.Unresolved());
        Assert.Empty(session.Items[0].Map!.Placeholders);
        Assert.Throws<FormatException>(() => TypeMappingTable.Parse("not a mapping line"));
    }

    [Fact]
    public void Campaign_graph_reconstruction_flags_unreachable_and_dangling_maps_and_previews_persistence()
    {
        SyntheticMap first = Fixtures.LegacyMap("First", next: "Second");
        SyntheticMap second = Fixtures.LegacyMap("Second", next: "Nowhere");
        SyntheticMap lost = Fixtures.LegacyMap("Lost");
        byte[] wcm = SyntheticMap.Campaign("Saga", "Invented.", [("First", true, first.Build()), ("Second", false, second.Build()), ("Lost", false, lost.Build())]);
        ImportItem item = LegacyImportSession.ConvertOne(Write("saga.wcm", wcm), Settings);
        Assert.True(item.IsCampaign);
        Assert.Equal("saga.t2camp", item.OutputName);
        CampaignData campaign = item.Campaign!.Campaign!;
        CampaignGraph graph = CampaignGraph.Build(campaign, id => item.Campaign.Maps[campaign.Maps.Items.ToList().FindIndex(m => m.Id == id)].Scenario);
        Assert.Equal(["lost"], graph.Unreachable.Select(n => n.Id).ToArray());
        Assert.Equal(["Nowhere"], graph.Dangling.ToArray());
        Assert.Equal(["first", "second", "lost"], graph.Persistent["Sir Pell"].ToArray());
        Assert.Contains(item.Rows(), r => r.Code == "campaign.unreachable" && r.Target.Kind == LocationKind.CampaignMap && r.Target.Name == "lost");
        Assert.Contains(item.Rows(), r => r.MapId == "second");
        Assert.True(item.Counts()["sharedAssets"] >= 1, "identical resources are stored once (REQ-EDT-09)");
    }

    [Fact]
    public void Every_converted_map_passes_the_600_tick_smoke_test_and_the_campaign_plays()
    {
        SyntheticMap first = Fixtures.LegacyMap("Alpha", next: "Beta");
        SyntheticMap second = Fixtures.LegacyMap("Beta");
        byte[] wcm = SyntheticMap.Campaign("Short", "Invented.", [("Alpha", true, first.Build()), ("Beta", false, second.Build())]);
        ImportItem item = LegacyImportSession.ConvertOne(Write("short.wcm", wcm), Settings);
        foreach (MapConversion m in item.Campaign!.Maps)
        {
            SmokeResult r = MapSmoke.Run(new MissionContent(m.Map!, m.Scenario!), Fixtures.Classic);
            Assert.True(r.Passed, r.Error);
            Assert.Equal(MapSmoke.DefaultTicks, r.Ticks);
        }

        // "Play converted campaign": the converted maps start as campaign missions.
        var source = new InMemoryCampaignSource(item.Campaign.Campaign!, item.Campaign.Maps.Select((m, i) => (item.Campaign.Campaign!.Maps[i].Id, new MissionContent(m.Map!, m.Scenario!))).ToDictionary(p => p.Id, p => p.Item2));
        Assert.Equal("alpha", CampaignFlow.Begin(source.Campaign).Current);
    }

    [Fact]
    public void The_whole_report_exports_as_json()
    {
        var session = new LegacyImportSession { Settings = Settings };
        session.Add(Write("a.wmp", Fixtures.LegacyMap("A").Build()));
        session.Add(Write("b.wmp", [0x42, 0x41, 0x44]));
        session.Convert();
        Assert.False(session.Items[1].Succeeded);
        string json = System.Text.Encoding.UTF8.GetString(session.ExportReports());
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
    }
}
