using System;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Content.Packages;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Editing;
using Tzar2.Import.Editing;
using Tzar2.Import.Maps;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>The campaign editor (TDD §18.2 "Campaign editor", §18.3 visual campaign graph, ADR 0155).</summary>
public class CampaignEditorTests
{
    private const int Tile = MapData.UnitsPerTile;

    internal static MapDocument Map(string name, string? next, bool win = false, bool persistentHero = true)
    {
        MapDocument doc = MapDocument.CreateEmpty(name, 32, 20, Fixtures.RulesFor(null));
        doc.Apply("Objects", s => s
            .Add(new MapObject("euro.footman", 0, (4 * Tile) + 1024, (10 * Tile) + 1024), new ObjectInfo("Oren", "Captain Oren", persistentHero ? ObjectFlags.Persistent : ObjectFlags.None), out _)
            .Add(new MapObject("euro.peon", 1, (28 * Tile) + 1024, (10 * Tile) + 1024), null, out _)
            .Import("user/banner.png", [9, 9, 9]));
        doc.Apply("Scenario", s => s.WithScenario(m =>
        {
            m = (m with { Properties = m.Properties with { Title = name, Description = name + " details", Victory = VictoryCondition.NotSpecified } })
                .WithKingdom(ScenarioModel.DefaultKingdom(1) with { StartTileX = 4, StartTileY = 10 })
                .WithKingdom(ScenarioModel.DefaultKingdom(2) with { StartTileX = 28, StartTileY = 10 })
                .AddAction(new ExpressionAction("remember", ScenarioModel.Source("#Visits = #Visits + 1")), out int remember);
            m = next is null
                ? m.AddAction(new PlayerWinAction("won", PlayerMasks.Of(1), null), out int end).AddRule(new RuleDef("finish", ScenarioModel.Source("1"), new[] { remember, end }), out _)
                : win
                    ? m.AddAction(new PlayerWinAction("won", PlayerMasks.Of(1), next), out int w).AddRule(new RuleDef("next", ScenarioModel.Source("1"), new[] { remember, w }), out _)
                    : m.AddAction(new LoadMapAction("onward", next), out int l).AddRule(new RuleDef("next", ScenarioModel.Source("1"), new[] { remember, l }), out _);
            return m;
        }));
        return doc;
    }

    internal static CampaignDocument Saga()
    {
        CampaignDocument c = CampaignDocument.New("The Saga");
        c.Apply(s => s with { Description = "An invented campaign." });
        c.AddMap(Map("Ford", "Mill"), entry: true);
        c.AddMap(Map("Mill", "watch", win: true));
        c.AddMap(Map("Watch", null));
        return c;
    }

    [Fact]
    public void Maps_entries_descriptions_and_the_graph_from_the_maps_actions()
    {
        CampaignDocument c = Saga();
        Assert.Equal(["ford", "mill", "watch"], c.State.Maps.Select(m => m.Id).ToArray());
        Assert.True(c.State.Maps[0].IsEntry);
        CampaignData data = c.Build();
        Assert.Equal(("the-saga", "The Saga", "An invented campaign."), (data.Id, data.Name, data.Description));
        Assert.Contains(data.Edges, e => e.FromMapId == "ford" && e.ToMapId == "mill" && e.Kind == CampaignEdgeKind.LoadMap);
        Assert.Contains(data.Edges, e => e.FromMapId == "mill" && e.ToMapId == "watch" && e.Kind == CampaignEdgeKind.Win);
        Assert.Empty(c.Validate(new ValidationOptions { Ruleset = Fixtures.Classic }).Issues.Where(i => i.Severity >= ValidationSeverity.Warning));
        Assert.Equal("Ford details", c.State.Maps[0].Document.State.Scenario.Properties.Description);
        c.UpdateMap("mill", m => m with { Description = "Free the mill." });
        Assert.Equal("Free the mill.", c.Build().Maps[1].Description);
    }

    [Fact]
    public void The_graph_flags_unreachable_maps_dangling_names_and_shows_persistence_and_carried_variables()
    {
        CampaignDocument c = Saga();
        c.AddMap(Map("Island", "Atlantis"));
        CampaignGraph g = c.Graph();
        Assert.Equal(["island"], g.Unreachable.Select(n => n.Id).ToArray());
        Assert.Equal(["Atlantis"], g.Dangling.ToArray());
        Assert.Equal(["ford", "mill", "watch", "island"], g.Persistent["Oren"].ToArray());
        CarriedVariable visits = Assert.Single(c.CarriedVariables());
        Assert.Equal("#Visits", visits.Name);
        Assert.Equal(4, visits.Maps.Length);
        ValidationResult r = c.Validate();
        Assert.Contains(r.Issues, i => i.Code == "campaign.unreachable");
        Assert.Contains(r.Issues, i => i.Code == "campaign.edge");
    }

    [Fact]
    public void A_campaign_needs_an_entry_map_and_unique_map_names()
    {
        CampaignDocument c = Saga();
        c.UpdateMap("ford", m => m with { IsEntry = false });
        Assert.True(c.Validate().BlocksPlay);
        c.Undo();
        Assert.False(c.Validate().BlocksPlay);
        c.Redo();
        c.Undo();
        c.UpdateMap("watch", m => m with { MapName = "MILL" });
        Assert.Contains(c.Validate().Issues, i => i.Code == "campaign.name");
        Assert.True(CampaignDocument.New("Empty").Validate().BlocksPlay);
    }

    [Fact]
    public void Map_import_export_delete_and_order()
    {
        CampaignDocument c = Saga();
        byte[] standalone = CampaignPackage.WriteMap(c, "mill");
        MapDocument copy = MapPackage.Read(standalone, Fixtures.RulesFor);
        Assert.Equal(c.Find("mill")!.Document.BuildScenario().ContentHash, copy.BuildScenario().ContentHash);
        string id = c.AddMap(copy, "Mill");
        Assert.Equal("mill-2", id);
        c.MoveMap(id, 0);
        Assert.Equal(id, c.State.Maps[0].Id);
        c.RemoveMap(id);
        Assert.Equal(3, c.State.Maps.Count);
    }

    [Fact]
    public void Save_open_new_round_trip_byte_identical_with_shared_resources_stored_once()
    {
        CampaignDocument c = Saga();
        c.Apply(s => s with { Positions = s.Positions.Add("ford", (10, 20)) });
        byte[] first = CampaignPackage.Write(c);
        Assert.Equal(first, CampaignPackage.Write(Saga() is var again ? Positioned(again) : again));
        CampaignDocument loaded = CampaignPackage.Read(first, Fixtures.RulesFor);
        Assert.Equal(first, CampaignPackage.Write(loaded));
        Assert.False(loaded.IsDirty);
        Assert.Equal((10, 20), loaded.State.Positions["ford"]);

        using var reader = PackageReader.Open(new MemoryStream(first), "saga.t2camp");
        Assert.True(reader.Contains("assets/user/banner.png"));
        using var inner = PackageReader.Open(new MemoryStream(reader.ReadAllBytes("maps/ford.t2map")), "ford.t2map");
        Assert.False(inner.Contains("assets/user/banner.png"));
        Assert.True(loaded.SharedAssets.ContainsKey("user/banner.png"));
        Assert.DoesNotContain(loaded.Validate().Issues, i => i.Code == "resource.missing");
        Assert.Equal(PackageKind.Campaign, PackageManifest.Parse(reader.ReadText("package.yaml")).Kind);

        static CampaignDocument Positioned(CampaignDocument d)
        {
            d.Apply(s => s with { Positions = s.Positions.Add("ford", (10, 20)) });
            return d;
        }
    }

    [Fact]
    public void A_converted_campaign_opens_and_saves_back_to_the_converters_exact_bytes()
    {
        byte[] wcm = SyntheticMap.Campaign("Saga", "Invented.", [("First", true, Fixtures.LegacyMap("First", next: "Second").Build()), ("Second", false, Fixtures.LegacyMap("Second").Build())]);
        CampaignConversion conversion = LegacyCampaignConverter.Convert(wcm, "saga.wcm", new LegacyConversionOptions { Ruleset = Fixtures.Classic });
        byte[] package = LegacyPackageWriter.WriteCampaign(conversion)!;
        CampaignDocument c = CampaignPackage.Read(package, Fixtures.RulesFor);
        Assert.Equal(package, CampaignPackage.Write(c));
        Assert.Equal(conversion.Campaign!.ContentHash, c.Build().ContentHash);
        Assert.True(c.Preserved.ContainsKey("conversion-report.json"));
    }

    [Fact]
    public void A_legacy_map_can_be_added_from_a_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tzar2-camp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "extra.wmp");
            File.WriteAllBytes(file, Fixtures.LegacyMap("Extra").Build());
            ImportItem item = LegacyImportSession.ConvertOne(file, new ImportSettings { Ruleset = Fixtures.Classic });
            CampaignDocument c = Saga();
            string id = c.AddMap(item.OpenDocument(Fixtures.RulesFor), item.Map!.MapName);
            Assert.Equal("extra", id);
            Assert.Equal(4, c.Build().Maps.Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_edited_campaign_plays_through_the_campaign_flow_with_carry_over()
    {
        CampaignDocument c = Saga();
        var source = new CampaignPlaytest(c);
        CampaignReport report = CampaignAutoplay.Run(source, Fixtures.Classic, new AutoplayOptions { MaxTicksPerMission = 2_000, CheatAssist = false });
        Assert.True(report.Completed, string.Join("\n", report.Missions.Select(m => $"{m.MapId}: {m.Result} {m.Assist} {m.Trace}")));
        Assert.Equal(["ford", "mill", "watch"], report.Missions.Select(m => m.MapId).ToArray());
        Assert.Equal(3, report.Progress.Variables.Single(v => v.Key.Contains("Visits", StringComparison.Ordinal)).Value);
    }
}
