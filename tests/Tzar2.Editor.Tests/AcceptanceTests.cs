using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Content.Editing;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Editing;
using Tzar2.Game.Scenario;
using Tzar2.Game.Session;
using Tzar2.Import.Editing;
using Tzar2.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Editor.Tests;

/// <summary>
/// TDD §18.6 acceptance, headless. (1) A 3-map campaign with conversations, a persistent hero, timers, quests, story screens,
/// custom sounds and music is authored with the editor's own tools only (the interaction model behind the Godot screen:
/// pointer events, property pages, the campaign editor), saved, reopened and played to the end. (2)/(3) The original and
/// the FX Gold Edition / community campaigns convert through the converter UI's session with zero errors, a reviewable
/// report, a correct graph, correct Cyrillic/Spanish text, deduplicated resources, and every map passes the smoke test and
/// completes. Those need the user's local copies (never committed, REQ-LEGAL-01) and are skipped without them.
/// </summary>
public sealed class AcceptanceTests(ITestOutputHelper output)
{
    private static MapPoint P(int x, int y) => new(x, y, x, y);

    // Authors one map through the controller (placement, starting locations) and the trigger edits the rule editor
    // applies; returns the controller so the caller can add its own triggers.
    private static EditorController NewMap(string name)
    {
        MapDocument doc = Content.Editing.NewMap.Empty(new NewMapOptions(name, 64, null), Fixtures.RulesFor(null), Fixtures.Classic);
        var c = new EditorController(doc, Fixtures.Classic);
        c.Tool = EditorTool.Place;
        c.PlaceOwner = 0;
        c.PlaceKey = "euro.footman";
        c.PointerDown(P(4, 10), EditorModifiers.None);
        c.PointerDown(P(4, 12), EditorModifiers.None);
        c.PlaceKey = "euro.peon";
        c.PointerDown(P(3, 8), EditorModifiers.None);
        c.PlaceOwner = 1;
        c.PointerDown(P(56, 4), EditorModifiers.None);
        c.PointerHover(P(4, 10));
        c.Key("1", EditorModifiers.None);
        c.PointerHover(P(56, 4));
        c.Key("2", EditorModifiers.None);
        doc.Apply("Properties", s => s.WithScenario(m => (m with { Properties = m.Properties with { Title = name, Author = "Acceptance", Victory = VictoryCondition.NotSpecified } })
            .UpdateKingdom(1, k => k with { Name = "Oren's men", Gold = 300, Food = 300, Wood = 300, Stone = 300 })
            .UpdateKingdom(2, k => k with { PlayerType = PlayerType.AiOnly, Gold = 300, Food = 300, Wood = 300, Stone = 300 })));
        return c;
    }

    // Makes the first placed footman the persistent hero through the properties dock's edit.
    private static int Hero(EditorController c)
    {
        int id = c.Document.State.Objects.First(o => o.TypeKey == "euro.footman").Id;
        c.Select([id]);
        c.Document.Apply("Hero", s => s.UpdateAll(c.Selection, o => o with { Info = o.Info with { Name = "Oren", Title = "Captain Oren", Flags = o.Info.Flags | ObjectFlags.Persistent } }));
        return id;
    }

    // Adds an action through its generated property page (TriggerSchema), as the rule editor does.
    private static int Action(MapDocument doc, string kind, string name, params (string Field, object? Value)[] fields)
    {
        TriggerKind k = TriggerSchema.Actions.Single(a => a.Id == kind);
        object record = k.Create(name);
        foreach ((string field, object? value) in fields)
        {
            record = k.Fields.Single(f => f.Name == field).Set(record, value);
        }

        int index = 0;
        doc.Apply("New action", s => s.WithScenario(m => m.AddAction((ActionDef)record, out index)));
        return index;
    }

    private static void Event(MapDocument doc, string kind, string name, params (string Field, object? Value)[] fields)
    {
        TriggerKind k = TriggerSchema.Events.Single(a => a.Id == kind);
        object record = k.Create(name);
        foreach ((string field, object? value) in fields)
        {
            record = k.Fields.Single(f => f.Name == field).Set(record, value);
        }

        doc.Apply("New event", s => s.WithScenario(m => m.AddEvent((EventDef)record, out _)));
    }

    private static void Rule(MapDocument doc, string name, string condition, params int[] actions)
        => doc.Apply("New rule", s => s.WithScenario(m => m.AddRule(new RuleDef(name, ScenarioModel.Source(condition), actions), out _)));

    internal static CampaignDocument AuthorCampaign()
    {
        // Map 1: the ford. Story screen with music, a quest, a countdown, a custom sound; reaching the far bank loads the mill.
        EditorController ford = NewMap("Ford");
        int oren = Hero(ford);
        MapDocument f = ford.Document;
        f.Apply("Resources", s => s.Import("user/wind.wav", Synthetic.Wav()).Import("user/theme.mid", Synthetic.Midi()).Import("user/ford.png", [1, 2, 3])
            .WithScenario(m => m.AddSound(new CustomSoundDef("wind", new ResourceRef("user/wind.wav"), SoundRepeat.Random, 4000, 9000, null, 20, 10), out _)
                .WithMusic([new ResourceRef("user/theme.mid")])));
        f.Apply("Area", s => s.SetArea("FarBank", new RectArea(40, 4, 50, 16), out _));
        Event(f, "objectInArea", "FarBank", ("Objects", new NamedObjects(new[] { oren }, Quantifier.All)), ("Area", new RectArea(40, 4, 50, 16)));
        int story = Action(f, "showStoryScreen", "Winter", ("Text", "The river froze early that year."), ("Scroll time", 6000), ("Image", new ResourceRef("user/ford.png")), ("Music", new ResourceRef("user/theme.mid")));
        int quest = Action(f, "addQuest", "Cross the ford", ("Text", "Bring Captain Oren to the far bank."));
        int clock = Action(f, "setTimer", "Frost", ("Initial value (ms)", ScenarioModel.Source("120000")), ("Count down", true));
        int train = Action(f, "setObjectProperties", "Trained", ("Objects", new NamedObjects(new[] { oren }, Quantifier.All)), ("Expression", ScenarioModel.Source("$level = 3")));
        int remember = Action(f, "expression", "Remember", ("Expression", ScenarioModel.Source("#Crossed = 1")));
        int onward = Action(f, "loadMap", "Onward", ("Map", "Mill"));
        Rule(f, "start", "1", story, quest, clock, train);
        Rule(f, "crossed", "FarBank", remember, onward);

        // Map 2: the mill. A conversation with a choice; burning the bandits' house wins and names the watch.
        EditorController mill = NewMap("Mill");
        Hero(mill);
        mill.PlaceOwner = 2;
        mill.PlaceKey = "euro.peon";
        mill.PointerDown(P(10, 4), EditorModifiers.None);
        int miller = mill.Selection.Single();
        mill.Document.Apply("Miller", s => s.Update(miller, o => o with { Info = o.Info with { Name = "Miller", Title = "The Miller" } }));
        mill.PlaceOwner = 1;
        mill.PlaceKey = "euro.house";
        mill.PointerDown(P(30, 8), EditorModifiers.None);
        int house = mill.Selection.Single();
        mill.Document.Apply("House", s => s.Update(house, o => o with { Info = o.Info with { Name = "BanditHouse", MaxHealth = 150 } }));
        MapDocument m2 = mill.Document;
        m2.Apply("Talk", s => s.WithScenario(m => m
            .AddConversation(new ConversationDef("ask", EquatableArray<PhraseDef>.Empty), out int c)
            .AddPhrase(c, new PhraseDef("ask", true, miller, ExpressionDef.Empty, ExpressionDef.Empty, "Bandits took my mill. Will you drive them off?", null, null, new[] { 1, 2 }), out _)
            .AddPhrase(c, new PhraseDef("yes", false, null, ExpressionDef.Empty, ScenarioModel.Source("$Agreed = 1"), "We will.", null, null, EquatableArray<int>.Empty), out _)
            .AddPhrase(c, new PhraseDef("later", false, null, ExpressionDef.Empty, ScenarioModel.Source("$Agreed = 2"), "Perhaps later.", null, null, EquatableArray<int>.Empty), out _)));
        Event(m2, "objectDestroyed", "HouseDown", ("Objects", new NamedObjects(new[] { house }, Quantifier.All)));
        int ask = Action(m2, "conversation", "Ask", ("Conversation", 0));
        int q2 = Action(m2, "addQuest", "Free the mill", ("Text", "Burn the bandits' house east of the mill."));
        int greet = Action(m2, "showMessage", "Welcome", ("Title", "The Miller"), ("Text", "You came by the ford."));
        int done = Action(m2, "removeQuest", "Done", ("Quest", q2));
        int won = Action(m2, "playerWin", "Freed", ("Players", PlayerMasks.Of(1)), ("Next map", "Watch"));
        Rule(m2, "start", "1", ask, q2);
        Rule(m2, "welcome", "#Crossed == 1", greet);
        Rule(m2, "freed", "HouseDown", done, won);

        // Map 3: the watch. Hold until dawn (a countdown); the captain must live.
        EditorController watch = NewMap("Watch");
        int captain = Hero(watch);
        MapDocument m3 = watch.Document;
        Event(m3, "objectDestroyed", "CaptainDown", ("Objects", new NamedObjects(new[] { captain }, Quantifier.All)));
        int dawn = Action(m3, "setTimer", "Dawn", ("Initial value (ms)", ScenarioModel.Source("20000")), ("Count down", true));
        int q3 = Action(m3, "addQuest", "Hold the watch", ("Text", "Keep Captain Oren alive until dawn."));
        int chat = Action(m3, "sendChat", "Courage", ("Message", "Hold fast!"));
        int win = Action(m3, "playerWin", "Dawn breaks", ("Players", PlayerMasks.Of(1)));
        int lose = Action(m3, "playerLose", "Fallen", ("Players", PlayerMasks.Of(1)));
        Rule(m3, "start", "1", dawn, q3, chat);
        Rule(m3, "dawn", "Dawn == 0", win);
        Rule(m3, "fallen", "CaptainDown", lose);

        // The campaign editor.
        CampaignDocument campaign = CampaignDocument.New("The Long Winter");
        campaign.Apply(s => s with { Description = "Lead Captain Oren from the ford to the hill watch (authored in the editor)." });
        campaign.AddMap(f, "Ford", entry: true);
        campaign.AddMap(m2, "Mill");
        campaign.AddMap(m3, "Watch");
        campaign.UpdateMap("ford", e => e with { Description = "Cross the river ford." });
        return campaign;
    }

    [Fact]
    public void A_three_map_campaign_authored_with_the_editor_only_plays_end_to_end()
    {
        CampaignDocument authored = AuthorCampaign();
        ValidationResult check = authored.Validate(new ValidationOptions { Ruleset = Fixtures.Classic });
        Assert.False(check.BlocksPlay, string.Join("\n", check.Issues.Select(i => i.Message)));
        Assert.DoesNotContain(check.Issues, i => i.Severity >= ValidationSeverity.Error);

        // Saved and reopened exactly.
        byte[] package = CampaignPackage.Write(authored);
        CampaignDocument campaign = CampaignPackage.Read(package, Fixtures.RulesFor);
        Assert.Equal(package, CampaignPackage.Write(campaign));
        Assert.Equal(["ford", "mill", "watch"], campaign.Build().Maps.Items.Select(m => m.Id).ToArray());
        Assert.Equal(2, campaign.Build().Edges.Length);
        Assert.Equal(["ford", "mill", "watch"], campaign.Graph().Persistent["Oren"].ToArray());

        // Presentation of map 1: the story screen with its music, the quest, the custom sound and the music playlist.
        using (GameSession session = Playtest.Start(campaign.Find("ford")!.Document, Fixtures.Classic, new PlaytestOptions()))
        {
            session.Step(60);
            var notices = new List<ScenarioNotice>();
            session.DrainNotices(notices);
            Assert.Contains(notices, n => n.Kind == ScenarioNoticeKind.StoryScreen && n.Text.Contains("froze", StringComparison.Ordinal) && n.Music == "user/theme.mid");
            Assert.Contains(session.Scenario!.View.Quests, q => q.Title == "Cross the ford");
            ScenarioData built = campaign.Find("ford")!.Document.BuildScenario();
            Assert.Single(built.Sounds);
            Assert.Single(built.Music);
        }

        // Played to the end through the campaign flow, with conversations, carry-over and timers.
        CampaignReport report = CampaignAutoplay.Run(new CampaignPlaytest(campaign), Fixtures.Classic, new AutoplayOptions { ComputerAi = true }, output.WriteLine);
        Assert.Null(report.Error);
        Assert.True(report.Completed, string.Join("\n", report.Missions.Select(m => $"{m.MapId}: {m.Result} {m.Assist} {m.Trace}")));
        Assert.Equal(["ford", "mill", "watch"], report.Missions.Select(m => m.MapId).Distinct().ToArray());
        Assert.Equal(("Oren", 3), (report.Progress.Heroes.Single().Name, report.Progress.Heroes.Single().Level));
        Assert.Contains(new KeyValuePair<string, int>("#Crossed", 1), report.Progress.Variables);
        Assert.All(report.Missions, m => Assert.Equal(0, m.Faults));
    }

    // ----- local-only: the user's original, FX Gold Edition and community campaigns (never in the repository) -----

    private static IEnumerable<string> Campaigns(string? root) => root is null || !Directory.Exists(root)
        ? []
        : Directory.EnumerateFiles(root, "*.wcm", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    // Converts every campaign under root through the converter UI's session and checks §18.6's expectations.
    private void ConvertAndPlay(string root, bool play)
    {
        // With the imported base package (TZAR2_PACKAGE) the original's own ruleset and AI are used, as in the game.
        string? packagePath = Environment.GetEnvironmentVariable("TZAR2_PACKAGE");
        using Content.Vfs.PackageSource? package = packagePath is { Length: > 0 } && File.Exists(packagePath) ? Content.Vfs.PackageSource.Open(packagePath) : null;
        Ruleset ruleset = package is null ? Fixtures.Classic : RulesetLoader.Compile(package).Ruleset;
        SkirmishAi? ai = package is null ? null : SkirmishAi.Resolve(package);
        var session = new LegacyImportSession { Settings = new ImportSettings { Ruleset = ruleset } };
        Assert.True(session.Add(root) > 0, "no legacy files under " + root);
        foreach (ImportItem item in session.Convert())
        {
            output.WriteLine($"{item.Path}: {item.Report?.ErrorCount} errors, {item.Report?.WarningCount} warnings, code page {item.Report?.CodePage}, language {item.Report?.Language}");
            Assert.True(item.Succeeded, item.Report?.ToText() ?? item.Error);
            Assert.NotEmpty(item.Rows());
            if (item.Campaign?.Campaign is not { } data)
            {
                continue;
            }

            CampaignGraph graph = CampaignGraph.Build(data, id => item.Campaign.Maps[data.Maps.Items.ToList().FindIndex(m => m.Id == id)].Scenario);
            Assert.Empty(graph.Unreachable);
            Assert.True(item.Counts().GetValueOrDefault("sharedAssets") >= 0);
            Assert.DoesNotContain(item.Campaign.Maps, m => (m.Scenario?.Properties.Title ?? string.Empty).Contains('�', StringComparison.Ordinal));
            foreach (Import.Maps.MapConversion m in item.Campaign.Maps)
            {
                SmokeResult smoke = MapSmoke.Run(new MissionContent(m.Map!, m.Scenario!), ruleset);
                Assert.True(smoke.Passed, $"{m.MapName}: {smoke.Error}");
            }

            if (play)
            {
                CampaignDocument doc = item.OpenCampaign(set => set is null ? TerrainRules.WithoutSet(ruleset) : TerrainRules.ForSet(set, null, ruleset));
                CampaignReport report = CampaignAutoplay.Run(new CampaignPlaytest(doc), ruleset, new AutoplayOptions { ContinueAfterFailure = true, Ai = ai }, line => output.WriteLine($"{doc.State.Id} {line}"));
                foreach (MissionReport r in report.Missions)
                {
                    output.WriteLine($"{doc.State.Id}/{r.MapId}: {(r.Completed ? "completed" : "NOT completed")} ({r.Result}, {r.Assist}, {r.Ticks} ticks, {r.Faults} faults)");
                }

                // A map counts by its last attempt: a lost first try (no cheat assistance) is retried, as in `t2 campaign run`.
                // A map that no attempt completes still fails; so does one whose first attempt did not even run.
                IGrouping<string, MissionReport>[] maps = [.. report.Missions.GroupBy(r => r.MapId)];
                Assert.True(maps.All(g => g.First().Ticks > 0), string.Join("\n", maps.Where(g => g.First().Ticks <= 0).Select(g => $"{g.Key}: the first attempt did not run")));
                MissionReport[] last = [.. maps.Select(g => g.Last())];
                Assert.True(last.All(r => r.Completed), string.Join("\n", last.Where(r => !r.Completed).Select(r => $"{r.MapId}: {r.Result} {r.Assist} {r.Trace}")));
            }
        }
    }

    [OriginFact]
    public void The_original_campaigns_convert_through_the_editor_and_every_mission_completes()
        => ConvertAndPlay(Path.Combine(OriginPaths.RequireOrigin(), "Campaign"), play: true);

    [LocalFolderFact("TZAR2_FX_CAMPAIGNS")]
    public void The_FX_Gold_Edition_campaigns_convert_through_the_editor_with_zero_errors()
        => ConvertAndPlay(Environment.GetEnvironmentVariable("TZAR2_FX_CAMPAIGNS")!, play: false);

    [LocalFolderFact(Import.Maps.LegacyCorpus.EnvironmentVariable)]
    public void A_community_campaign_from_an_arbitrary_folder_converts_through_the_editor()
        => ConvertAndPlay(Environment.GetEnvironmentVariable(Import.Maps.LegacyCorpus.EnvironmentVariable)!, play: false);
}

/// <summary>Runs only where a local folder named by an environment variable exists (user data, never in CI).</summary>
public sealed class LocalFolderFactAttribute : FactAttribute
{
    public LocalFolderFactAttribute(string variable)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            Skip = $"{variable} is not set to a local folder (the campaigns are the user's own copies, REQ-LEGAL-01).";
        }
    }
}

/// <summary>Invented audio for the authored campaign (REQ-LEGAL-03).</summary>
internal static class Synthetic
{
    public static byte[] Wav() => SyntheticFormats.Wav();

    public static byte[] Midi() => SyntheticFormats.Mid();
}
