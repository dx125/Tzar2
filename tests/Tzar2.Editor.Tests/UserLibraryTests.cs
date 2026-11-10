using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Editing;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>The player's own maps and campaigns in the game's menus, the unsaved-work safeguards and the Lua warning (ADR 0286).</summary>
public sealed class UserLibraryTests : IDisposable
{
    private const int Tile = MapData.UnitsPerTile;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tzar2-userlib-" + Guid.NewGuid().ToString("N"));

    public UserLibraryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static MapDocument Map(string title, string type = "euro.footman")
    {
        MapDocument doc = MapDocument.CreateEmpty(title, 32, 24, Fixtures.RulesFor(null));
        doc.Apply("Objects", s => s
            .Add(new MapObject(type, 0, (4 * Tile) + 1024, (10 * Tile) + 1024), null, out _)
            .Add(new MapObject("euro.peon", 1, (26 * Tile) + 1024, (10 * Tile) + 1024), null, out _));
        doc.Apply("Scenario", s => s.WithScenario(m =>
            (m with { Properties = m.Properties with { Title = title, Description = title + " details" } })
            .WithKingdom(ScenarioModel.DefaultKingdom(1) with { StartTileX = 4, StartTileY = 10 })
            .WithKingdom(ScenarioModel.DefaultKingdom(2) with { StartTileX = 26, StartTileY = 10 })));
        return doc;
    }

    private void Write(string folder, string file, byte[] bytes)
    {
        Directory.CreateDirectory(Path.Combine(_root, folder));
        File.WriteAllBytes(Path.Combine(_root, folder, file), bytes);
    }

    [Fact]
    public void The_players_maps_are_offered_by_title_with_ids_under_their_folder()
    {
        Write(UserLibrary.MapsFolder, "ford.t2map", MapPackage.Write(Map("The Ford")));
        Write(UserLibrary.ImportedFolder, "choro.t2map", MapPackage.Write(Map("Choro Griso")));
        Write(UserLibrary.MapsFolder, "broken.t2map", [1, 2, 3]);
        Write(UserLibrary.MapsFolder, "unknown.t2map", MapPackage.Write(Map("Unknown", type: "euro.no_such_unit")));
        Write(UserLibrary.MapsFolder, "notes.txt", [1]);

        var problems = new List<string>();
        List<CustomMapEntry> maps = UserLibrary.Maps(_root, Fixtures.RulesFor, Fixtures.Classic, problems);

        Assert.Equal(["imported/choro", "maps/ford"], maps.Select(m => m.Id).ToArray());
        Assert.Equal(["Choro Griso", "The Ford"], maps.Select(m => m.Title).ToArray());
        Assert.Equal("The Ford details", maps[1].Description);
        Assert.Equal(2, maps[1].Kingdoms);
        Assert.Equal(Playtest.Mission(Map("The Ford")).ContentHash, maps[1].Hash); // the lobby finds it by this hash
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("maps/broken: ", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("maps/unknown: ", StringComparison.Ordinal) && p.Contains("euro.no_such_unit", StringComparison.Ordinal));
        Assert.Empty(UserLibrary.Maps(Path.Combine(_root, "nowhere"), Fixtures.RulesFor, Fixtures.Classic, problems));
    }

    [Fact]
    public void A_players_map_starts_as_a_custom_map_match()
    {
        Write(UserLibrary.MapsFolder, "ford.t2map", MapPackage.Write(Map("The Ford")));
        CustomMapEntry map = Assert.Single(UserLibrary.Maps(_root, Fixtures.RulesFor, Fixtures.Classic, new List<string>()));
        using var session = new Tzar2.Game.Session.GameSession(ScenarioMatch.Setup(map.Mission, Fixtures.Classic, custom: new CustomMapSettings()), Fixtures.Classic);
        session.Step(20);
        Assert.Null(session.Fault);
    }

    [Fact]
    public void The_players_campaigns_are_offered_and_build_their_maps_when_played()
    {
        CampaignDocument saga = CampaignDocument.New("The Saga");
        saga.AddMap(Map("Ford"), entry: true);
        saga.AddMap(Map("Mill"));
        Write(UserLibrary.CampaignsFolder, "saga.t2camp", CampaignPackage.Write(saga));
        Write(UserLibrary.ImportedFolder, "bad.t2camp", [9, 9]);

        var problems = new List<string>();
        ICampaignSource source = Assert.Single(UserLibrary.Campaigns(_root, Fixtures.RulesFor, problems));
        Assert.Equal("The Saga", source.Campaign.Name);
        Assert.Equal("Ford", source.Load(source.Campaign.Maps.Single(m => m.IsEntry).Id).Scenario.Properties.Title);
        Assert.StartsWith("imported/bad: ", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public void The_autosave_comes_every_three_minutes_of_editing_while_there_are_new_changes()
    {
        var autosave = new EditorAutosave();
        Assert.False(autosave.Due(60_000, dirty: true, version: 1)); // not yet
        Assert.True(autosave.Due(180_000, dirty: true, version: 1));
        Assert.False(autosave.Due(360_000, dirty: true, version: 1)); // nothing new since
        Assert.False(autosave.Due(400_000, dirty: true, version: 2)); // within the interval
        Assert.True(autosave.Due(540_000, dirty: true, version: 2));
        Assert.False(autosave.Due(720_000, dirty: false, version: 3)); // saved by hand meanwhile

        MapDocument doc = Map("Ford Crossing");
        Assert.Equal("ford-crossing.t2map", EditorAutosave.MapFile(doc));
        Assert.Equal("the-saga.t2camp", EditorAutosave.CampaignFile(CampaignDocument.New("The Saga")));
    }

    [Fact]
    public void A_campaigns_version_changes_with_the_edits_of_its_maps()
    {
        CampaignDocument saga = CampaignDocument.New("The Saga");
        string ford = saga.AddMap(Map("Ford"), entry: true);
        long before = saga.Version;
        saga.State.Maps.Single(m => m.Id == ford).Document.Apply("Rename", s => s.WithScenario(m => m with { Properties = m.Properties with { Title = "The Ford" } }));
        Assert.NotEqual(before, saga.Version);
        Assert.True(saga.IsDirty);
    }

    [Fact]
    public void The_validator_warns_that_lua_scripts_do_not_run()
    {
        MapDocument doc = Map("Lua");
        Assert.Empty(MapValidator.Validate(doc, new ValidationOptions { Ruleset = Fixtures.Classic }).WithCode("script."));
        doc.Apply("Script", s => s.SetScript("main", "-- nothing\n"));
        ValidationIssue issue = Assert.Single(MapValidator.Validate(doc, new ValidationOptions { Ruleset = Fixtures.Classic }).WithCode("script."));
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Contains("does not run Lua", issue.Message, StringComparison.Ordinal);
    }
}
