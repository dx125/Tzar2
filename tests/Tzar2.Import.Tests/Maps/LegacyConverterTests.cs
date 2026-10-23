using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Content.Maps;
using Tzar2.Content.Packages;
using Tzar2.Content.Rules;
using Tzar2.Formats.Maps;
using Tzar2.Formats.Text;
using Tzar2.Import.Maps;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Import.Tests.Maps;

/// <summary>The legacy map/campaign converter (TDD §18.5, ADR 0121) on synthetic maps built from docs/formats/wmp.md.</summary>
public class LegacyConverterTests
{
    private static int Cls(string name) => LegacyClasses.IndexOf(name);

    /// <summary>A map using every action and event type.</summary>
    internal static SyntheticMap FullMap(string name = "Full")
    {
        var m = new SyntheticMap { Name = name, Side = 8, Title = name + " title" };
        ushort[] tiles = new ushort[64];
        Array.Fill(tiles, SyntheticMap.Solid(1));
        tiles[0] = SyntheticMap.Solid(0);
        tiles[1] = SyntheticMap.Transition(0, 1, 1 | 8);
        tiles[9] = SyntheticMap.Solid(1, tree: 12);
        tiles[10] = SyntheticMap.Solid(1, tree: 2);
        tiles[11] = tiles[12] = tiles[19] = tiles[20] = 0x0400;   // a 2×2 cliff piece: bits 0–9 are not terrain (read as terrain they would be water)
        m.Tiles = tiles;
        m.Kingdoms[0].StartX = 3;
        m.Kingdoms[0].StartY = 4;
        m.Kingdoms[1].PlayerType = 1;
        m.Diplomacy[1] = 0x47;
        m.Objects.Add(new SyntheticObject { ClassName = "knight", X = 2, Y = 2, Owner = 1, Race = 1, Id = 1, Flags = 0x1000, Health = 300 });
        m.Objects.Add(new SyntheticObject { ClassName = "castle", X = 4, Y = 4, Owner = 2, Race = 2, Id = 2, Flags = 0x04C0 });
        m.Objects.Add(new SyntheticObject { ClassName = "peon", X = 5, Y = 1, Owner = 9, Race = 3, Id = 3 });
        m.Objects.Add(new SyntheticObject { ClassName = "cow", X = 6, Y = 1, Owner = 10, Race = 0, Id = 4 });
        m.Objects.Add(new SyntheticObject { ClassName = "ground_patch", X = 7, Y = 7, Owner = 0, Race = 0, Image = "flowers" });
        m.Objects.Add(new SyntheticObject { ClassName = "ground_object", X = 1, Y = 6, Owner = 0, Race = 0, Id = 6, Image = "sign" });
        m.Objects.Add(new SyntheticObject { ClassName = "res_gold", X = 6, Y = 6, Owner = 0, Race = 0, Id = 7, Amount = 5000 });
        m.Extras.Add((1, "Sir Test", "USER\\Knight.bmp"));
        m.Files.Add(("USER/Knight.bmp", SyntheticFormats.Bmp(2, 2, 16, [0x1F, 0x7C, 0xE0, 0x03, 0x00, 0x00, 0xFF, 0x7F])));
        m.Files.Add(("USER/Knight small.bmp", SyntheticFormats.Bmp(1, 1, 16, [0x1F, 0x00])));
        m.Files.Add(("USER/1.mid", SyntheticFormats.Mid()));
        m.Music.Add("USER\\1.mid");

        void E(string n, LegacyTriggerType t, SyntheticPayload p) => m.Events.Add((n, (int)t, p.ToArray()));
        void A(string n, LegacyTriggerType t, SyntheticPayload p) => m.Actions.Add((n, (int)t, p.ToArray()));
        SyntheticPayload P() => new();
        E("exists", LegacyTriggerType.ObjectExist, P().Named(true, 1, 2));
        E("dead", LegacyTriggerType.ObjectDestroyed, P().Typed(0xFFFC, 2, true, "0"));
        E("boarded", LegacyTriggerType.ObjectsEnterHolder, P().Named(false, 1).Holder(2));
        E("left", LegacyTriggerType.ObjectLeaveHolder, P().Named(false, 1).Holder(2));
        E("near", LegacyTriggerType.ObjectInArea, P().Typed(Cls("knight"), 1, false, "2", race: 1).AtObject(2, "3"));
        E("lost", LegacyTriggerType.PlayerDefeated, P().U16(2));
        E("won", LegacyTriggerType.PlayerWon, P().U16(1));
        E("weak", LegacyTriggerType.ObjectExpression, P().Named(false, 1).Str("$health < 10"));

        A("quest", LegacyTriggerType.AddQuest, P().Str("Do it").Str("USER\\Knight.bmp"));
        A("unquest", LegacyTriggerType.RemoveQuest, P().Str("quest"));
        A("dipl", LegacyTriggerType.ChangeDiplomacy, P().U8(1).U8(2).U32(0x41));
        A("owner", LegacyTriggerType.ChangeObjectOwner, P().Named(false, 3).U8(1));
        A("props", LegacyTriggerType.SetObjectProperties, P().Named(false, 1).Str("$attack = 20"));
        A("expr", LegacyTriggerType.Expression, P().Str("$score = $score + 1, #Saved = 1"));
        A("clock", LegacyTriggerType.SetTimer, P().Str("1000").U8(1));
        A("stop", LegacyTriggerType.StopTimer, P().Str("clock"));
        A("lose", LegacyTriggerType.PlayerLose, P().U16(1).Str("|"));
        A("win", LegacyTriggerType.PlayerWin, P().U16(1).Str("MAPS\\Next.wmp|"));
        A("keep", LegacyTriggerType.KeepRule, P());
        A("load", LegacyTriggerType.LoadMap, P().Str("MAPS\\Second.wmp"));
        A("lock", LegacyTriggerType.LockView, P().Str("3000").Rect(0, 0, 8, 8));
        A("chat", LegacyTriggerType.SendChat, P().Str("Hi all").U16(3));
        A("story", LegacyTriggerType.ShowStoryScreen, P().Str("Once upon a time").Str(string.Empty).Str(string.Empty).Str("USER\\1.mid").Str("20000").U8(1));
        A("message", LegacyTriggerType.ShowMessage, P().Str("Title").Str("Body").Str(string.Empty));
        A("spawn", LegacyTriggerType.SpawnObject, P().U16(Cls("archer")).Str("4").U8(2).Rect(1, 1, 3, 3).U8(2));
        A("unlock", LegacyTriggerType.UnlockView, P());
        A("order", LegacyTriggerType.ObjectOrder, P().Named(false, 1).Rect(5, 5, 6, 6).U8(7));
        A("scroll", LegacyTriggerType.ScrollView, P().AtObject(1, "0").AtObject(2, "0"));
        A("talk", LegacyTriggerType.Conversation, P().Str("Chat"));
        A("destroy", LegacyTriggerType.DestroyObject, P().Named(false, 2));
        A("explore", LegacyTriggerType.ExploreArea, P().Rect(0, 0, 4, 4).Str("7000").U8(1));
        A("appear", LegacyTriggerType.AppearObject, P().Named(false, 6));
        A("status", LegacyTriggerType.ChangePlayerStatus, P().U8(1).U8(1).U8(0));
        A("freeze", LegacyTriggerType.LockUnits, P().Named(false, 1).U8(1));
        A("erase", LegacyTriggerType.EraseObject, P().Named(false, 4));
        A("wait", LegacyTriggerType.Delay, P().Str("500"));
        m.Rules.Add(("main", "exists && clock > 500\r\n|| won", ["quest", "keep", "talk"]));
        m.Rules.Add(("broken", "1 +", ["missing", "wait"]));
        m.Conversations.Add(("Chat",
        [
            new SyntheticPhrase { Id = 1, Source = 1, Initial = true, Name = "a", Text = "Hello", FollowUps = [2], Icon = "USER\\Knight.bmp" },
            new SyntheticPhrase { Id = 2, Source = 0, Name = "b", Condition = "$score > 0", Result = "$score = 0", Text = "Bye", FollowUps = [1] },
        ]));
        return m;
    }

    [Fact]
    public void Terrain_objects_and_kingdoms_convert()
    {
        MapConversion c = LegacyMapConverter.Convert(FullMap().Build(), "full.wmp");
        Assert.True(c.Succeeded, c.Report.ToText());
        MapData map = c.Map!;
        Assert.Equal(("Full title", 8, 8, "grass"), (map.Name, map.Width, map.Height, map.TerrainSet));
        Assert.Equal(0, map.SetTerrainCorners[0]);              // tile 0 is water
        Assert.Equal(1, map.TerrainAt(0, 0));                    // classic id of water
        Assert.Equal(1, map.CliffTiles[11]);
        Assert.Equal(1, map.SetTerrainCorners[(2 * 9) + 4]);   // the corner inside the cliff piece is land, not water
        Assert.Equal(0, map.CliffTiles[10]);

        Assert.Equal(["euro.knight", "arab.castle", "asia.peon", "comm.cow", "comm.ground_object", "comm.res_gold", "comm.tree"], map.Objects.Select(o => o.TypeKey).ToArray());
        Assert.Equal((byte)0, map.Objects[0].Owner);
        Assert.Equal((byte)1, map.Objects[1].Owner);
        Assert.Equal(MapObject.Nature, map.Objects[2].Owner);
        Assert.Equal(MapObject.Nature, map.Objects[3].Owner);
        Assert.Equal(((2 * 2048) + 1024, (2 * 2048) + 1024), (map.Objects[0].X, map.Objects[0].Y));
        Assert.Equal(MapObject.AtTile("x", 0, 4, 4).X / 2048, map.Objects[1].X / 2048);
        Assert.Single(c.Decorations, d => d.Image == "flowers");
        Assert.Single(c.Decorations, d => d.ClassName == "tree");   // a stump

        ScenarioData s = c.Scenario!;
        ObjectInfo knight = s.ObjectAt(0);
        Assert.Equal("Sir Test", knight.Name);
        Assert.Equal(ObjectFlags.Persistent, knight.Flags);
        Assert.Equal(300, knight.Health);
        Assert.Equal(new ResourceRef("user/knight.png"), knight.Icon);
        Assert.Equal(new ResourceRef("user/knight small.png"), knight.BigIcon);
        Assert.Null(s.ObjectAt(1).Health);                      // default bits set
        Assert.True((s.ObjectAt(2).Flags & ObjectFlags.Rescue) != 0);

        KingdomDef k1 = s.Kingdoms[0];
        Assert.Equal((3, 4, "euro", true), (k1.StartTileX, k1.StartTileY, k1.Civilization, k1.SharedVictory));
        Assert.Equal((300, 400, 100, 200), (k1.Food, k1.Wood, k1.Gold, k1.Stone));
        Assert.Equal(PlayerType.AiOnly, s.Kingdoms[1].PlayerType);
        DiplomacyDef d12 = s.Diplomacy.Single(d => d.From == 1 && d.To == 2);
        Assert.Equal((Stance.Ally, true, true, true), (d12.Stance, d12.SharedVision, d12.SharedResources, d12.CommandUnits));
        Assert.Equal(new ResourceRef("user/1.mid"), s.Music.Single());
        Assert.Contains(c.Resources, r => r.Path == "user/knight.png" && r.Data[1] == (byte)'P');
    }

    [Fact]
    public void Gates_bridges_and_facings_keep_their_placement()
    {
        SyntheticMap m = FullMap();
        m.Objects.Add(new SyntheticObject { ClassName = "bridge", X = 1, Y = 1, Owner = 10, Race = 0, Extension = [1] });
        m.Objects.Add(new SyntheticObject { ClassName = "bridge", X = 1, Y = 4, Owner = 10, Race = 0, Extension = [0] });
        m.Objects.Add(new SyntheticObject { ClassName = "gate_stone", X = 5, Y = 5, Owner = 1, Race = 1, Extension = [3, 1] });
        m.Objects.Add(new SyntheticObject { ClassName = "gate_wood", X = 6, Y = 5, Owner = 1, Race = 1, Extension = [0, 0] });
        m.Objects.Add(new SyntheticObject { ClassName = "archer", X = 3, Y = 3, Owner = 1, Race = 1, Direction = 4 });
        MapData map = LegacyMapConverter.Convert(m.Build(), "full.wmp").Map!;
        MapObject[] o = map.Objects.Where(x => x.TypeKey is "comm.bridge" or "euro.gate_stone" or "euro.gate_wood" or "euro.archer").ToArray();
        Assert.Equal(MapObjectFlags.Rotated, o[0].Flags);
        Assert.Equal(MapObjectFlags.None, o[1].Flags);
        Assert.Equal(MapObjectFlags.Rotated | MapObjectFlags.Mirrored | MapObjectFlags.Open, o[2].Flags);
        Assert.Equal(MapObjectFlags.None, o[3].Flags);
        Assert.Equal((byte)6, o[4].Facing);   // legacy 4 = north → ours 6
        Assert.Equal(map.ContentHash, MapBinary.Read(MapBinary.Write(map)).ContentHash);
    }

    [Fact]
    public void Every_trigger_type_maps_to_its_ir_record()
    {
        MapConversion c = LegacyMapConverter.Convert(FullMap().Build(), "full.wmp");
        ScenarioData s = c.Scenario!;
        Assert.Equal(
            [typeof(ObjectExistEvent), typeof(ObjectDestroyedEvent), typeof(ObjectsEnterHolderEvent), typeof(ObjectLeaveHolderEvent), typeof(ObjectInAreaEvent), typeof(PlayerDefeatedEvent), typeof(PlayerWonEvent), typeof(ObjectExpressionEvent)],
            s.Events.Select(e => e.GetType()).ToArray());
        Assert.Equal(28, s.Actions.Length);
        Assert.DoesNotContain(s.Actions, a => a is UnknownAction);
        Assert.Equal(new NamedObjects(new[] { 0, 1 }, Quantifier.All), ((ObjectExistEvent)s.Events[0]).Objects);
        Assert.Equal(new CategoryObjects(ObjectCategory.Buildings, 2, CountComparison.AtMost, 0, Quantifier.Any), ((ObjectDestroyedEvent)s.Events[1]).Objects);
        Assert.Equal(1, ((ObjectsEnterHolderEvent)s.Events[2]).HolderIndex);
        var near = (ObjectInAreaEvent)s.Events[4];
        Assert.Equal(new TypedObjects("euro.knight", 1, CountComparison.AtLeast, 2, Quantifier.Any), near.Objects);
        Assert.Equal(new ObjectArea(1, 3), near.Area);

        T A<T>(string name) where T : ActionDef => (T)s.Actions.Single(a => a.Name == name);
        Assert.Equal(0, A<RemoveQuestAction>("unquest").AddQuestActionIndex);
        Assert.Equal(new ChangeDiplomacyAction("dipl", 1, 2, Stance.Ally, true, false, false), A<ChangeDiplomacyAction>("dipl"));
        Assert.IsType<SequenceExpr>(A<ExpressionAction>("expr").Expression.Root);
        Assert.Contains("#Saved", s.Variables);
        Assert.True(A<SetTimerAction>("clock").CountDown);
        Assert.Equal(6, A<StopTimerAction>("stop").SetTimerActionIndex);
        Assert.Null(A<PlayerLoseAction>("lose").Map);
        Assert.Equal("Next", A<PlayerWinAction>("win").Map);
        Assert.Equal("Second", A<LoadMapAction>("load").Map);
        Assert.Equal(new RectArea(0, 0, 7, 7), A<LockViewAction>("lock").Area);
        Assert.Equal(3000, A<LockViewAction>("lock").TimeMs);
        Assert.Equal((ushort)3, A<SendChatAction>("chat").Players);
        ShowStoryScreenAction story = A<ShowStoryScreenAction>("story");
        Assert.Equal((20000, true, "Once upon a time", new ResourceRef("user/1.mid")), (story.ScrollTimeMs, story.AutoClose, story.Text, story.Music));
        Assert.Equal(("Title", "Body"), (A<ShowMessageAction>("message").Title, A<ShowMessageAction>("message").Text));
        Assert.Equal(new SpawnObjectAction("spawn", "arab.archer", 2, 4, new RectArea(1, 1, 3, 3)), A<SpawnObjectAction>("spawn"));
        Assert.Equal("patrol", A<ObjectOrderAction>("order").OrderKey);
        Assert.Equal(0, A<ConversationAction>("talk").ConversationIndex);
        Assert.Equal(new NamedObjects(new[] { 4 }, Quantifier.Any), A<AppearObjectAction>("appear").Objects);   // the referenced sign
        Assert.Equal(new ChangePlayerStatusAction("status", 1, true, false), A<ChangePlayerStatusAction>("status"));
        Assert.Equal(500, A<DelayAction>("wait").TimeMs);
        Assert.False(A<LockUnitsAction>("freeze").Locked);   // legacy byte 1 = unlock

        RuleDef main = s.Rules[0];
        Assert.Equal(new[] { 0, 10, 20 }, main.ActionIndices.ToArray());
        Assert.IsType<BinaryExpr>(main.Condition.Root);
        Assert.NotNull(s.Rules[1].Condition.Error);
        Assert.Contains(c.Report.Diagnostics, d => d.Code == "trigger.action" && d.Location == "rule 'broken'");
        Assert.Contains(c.Report.Diagnostics, d => d.Code == "expression.parse");

        ConversationDef chat = s.Conversations.Single();
        Assert.Equal(0, chat.Phrases[0].SourceObject);
        Assert.Null(chat.Phrases[1].SourceObject);
        Assert.Equal(new[] { 1 }, chat.Phrases[0].FollowUps.ToArray());
        Assert.Contains(c.Report.Diagnostics, d => d.Code == "conversation.cycle");
        Assert.Equal(28, c.Report.Counts["actions"]);
        Assert.Equal(1, c.Report.Counts["quests"]);
    }

    [Fact]
    public void Cyrillic_text_is_detected_and_decoded()
    {
        SyntheticMap m = FullMap();
        m.CodePage = CodePage.Windows1251;
        m.Title = "Огнём и мечом";
        m.Description = "Описание карты на русском языке.";
        MapConversion c = LegacyMapConverter.Convert(m.Build(), "ru.wmp");
        Assert.Equal(1251, c.Report.CodePage);
        Assert.Equal("ru", c.Report.Language);
        Assert.Equal("Огнём и мечом", c.Scenario!.Properties.Title);

        MapConversion forced = LegacyMapConverter.Convert(m.Build(), "ru.wmp", new LegacyConversionOptions { CodePage = CodePage.Windows1252 });
        Assert.Equal(1252, forced.Report.CodePage);
        Assert.Equal("override", forced.Report.CodePageSource);
    }

    [Fact]
    public void Spanish_text_stays_in_1252()
    {
        CodePageGuess g = CodePageDetector.Detect([LegacyText.Encode("La campaña del Señor, ¿dónde está?", CodePage.Windows1252)]);
        Assert.Equal((CodePage.Windows1252, "es"), (g.CodePage, g.Language));
        Assert.Equal(CodePage.Windows1252, CodePageDetector.Detect([Encoding.ASCII.GetBytes("plain")]).CodePage);
    }

    [Fact]
    public void Conversion_and_packages_are_deterministic_and_round_trip()
    {
        byte[] source = FullMap().Build();
        MapConversion a = LegacyMapConverter.Convert(source, "full.wmp");
        MapConversion b = LegacyMapConverter.Convert(source, "full.wmp");
        byte[] pa = LegacyPackageWriter.WriteMap(a, "full")!;
        Assert.Equal(pa, LegacyPackageWriter.WriteMap(b, "full"));
        Assert.Equal(a.Scenario!.ContentHash, b.Scenario!.ContentHash);
        Assert.Equal(a.Map!.ContentHash, b.Map!.ContentHash);

        using var reader = PackageReader.Open(new MemoryStream(pa), "full.t2map");
        PackageManifest manifest = PackageManifest.Parse(Encoding.UTF8.GetString(reader.ReadAllBytes("package.yaml")));
        Assert.Equal(PackageKind.Map, manifest.Kind);
        MapData map = MapBinary.Read(reader.ReadAllBytes("map.bin"));
        Assert.Equal(a.Map.ContentHash, map.ContentHash);
        ScenarioData scenario = ScenarioJson.Read(reader.ReadAllBytes("scenario.json"));
        Assert.Equal(a.Scenario.ContentHash, scenario.ContentHash);
        Assert.Equal(a.Scenario.Actions, scenario.Actions);
        Assert.True(reader.Contains("assets/user/knight.png"));
        Assert.True(reader.Contains("strings/en.po"));
        Assert.True(reader.Contains("conversion-report.json"));
        Assert.Contains("msgctxt \"action/story/text\"", Encoding.UTF8.GetString(reader.ReadAllBytes("strings/en.po")), StringComparison.Ordinal);
        Assert.Contains(a.Report.SourceMd5, Encoding.UTF8.GetString(reader.ReadAllBytes("provenance.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Campaign_graph_entry_maps_dedup_and_diagnostics()
    {
        SyntheticMap first = FullMap("First");
        SyntheticMap second = FullMap("Second");
        SyntheticMap orphan = FullMap("Orphan");
        byte[] wcm = SyntheticMap.Campaign("Saga", "The story.", [("First", true, first.Build()), ("Second", false, second.Build()), ("Orphan", false, orphan.Build())]);
        CampaignConversion c = LegacyCampaignConverter.Convert(wcm, "saga.wcm");
        Assert.True(c.Succeeded, c.Report.ToText());
        CampaignData campaign = c.Campaign!;
        Assert.Equal(("saga", "Saga", "The story."), (campaign.Id, campaign.Name, campaign.Description));
        Assert.Equal(["first", "second", "orphan"], campaign.Maps.Select(m => m.Id).ToArray());
        Assert.True(campaign.Maps[0].IsEntry);
        Assert.Contains(campaign.Edges, e => e.FromMapId == "first" && e.ToMapId == "second" && e.Kind == CampaignEdgeKind.LoadMap);
        Assert.Contains(campaign.Edges, e => e.ToMapName == "Next" && e.ToMapId is null);
        Assert.Contains(c.Report.Diagnostics, d => d.Code == "campaign.edge");
        Assert.Contains(c.Report.Diagnostics, d => d.Code == "campaign.unreachable" && d.Message.Contains("Orphan", StringComparison.Ordinal));
        Assert.Contains(c.Report.Diagnostics, d => d.Code == "campaign.persistent" && d.Message.Contains("Sir Test", StringComparison.Ordinal));
        Assert.Same(campaign.Maps[1], campaign.FindMap("SECOND"));

        byte[] package = LegacyPackageWriter.WriteCampaign(c)!;
        Assert.Equal(package, LegacyPackageWriter.WriteCampaign(LegacyCampaignConverter.Convert(wcm, "saga.wcm")));
        using var reader = PackageReader.Open(new MemoryStream(package), "saga.t2camp");
        Assert.Equal(campaign.ContentHash, CampaignJson.Read(reader.ReadAllBytes("campaign.json")).ContentHash);
        Assert.True(reader.Contains("assets/user/knight.png"));
        Assert.True(reader.Contains("maps/first.t2map"));
        using var inner = PackageReader.Open(new MemoryStream(reader.ReadAllBytes("maps/first.t2map")), "first.t2map");
        Assert.False(inner.Contains("assets/user/knight.png"));
        Assert.True(c.Report.Counts["sharedAssets"] >= 3);
    }

    [Fact]
    public void Unresolved_classes_become_placeholders_and_mappings_win()
    {
        Ruleset rules = SyntheticLegacyContent.CompileRuleset();
        SyntheticMap m = FullMap();
        m.Objects.Add(new SyntheticObject { ClassName = "evilcastle", X = 3, Y = 6, Owner = 2, Race = 0, Id = 40 });
        MapConversion c = LegacyMapConverter.Convert(m.Build(), "full.wmp", new LegacyConversionOptions { Ruleset = rules });
        Assert.NotNull(c.Map);
        c.Map!.Validate(rules);
        Assert.Contains(c.Placeholders, p => p.ClassName == "evilcastle");
        Assert.Contains(c.Report.Diagnostics, d => d.Code == "object.type");

        var mapping = LegacyTypeResolver.ParseMapping("# test\nevilcastle: euro.castle\n");
        MapConversion mapped = LegacyMapConverter.Convert(m.Build(), "full.wmp", new LegacyConversionOptions { Ruleset = rules, Mapping = mapping });
        Assert.DoesNotContain(mapped.Placeholders, p => p.ClassName == "evilcastle");
        Assert.Throws<FormatException>(() => LegacyTypeResolver.ParseMapping("no colon here"));
    }

    [Fact]
    public void Malformed_input_is_reported_not_thrown()
    {
        MapConversion c = LegacyMapConverter.Convert([1, 2, 3], "junk.wmp");
        Assert.False(c.Succeeded);
        Assert.Contains(c.Report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Code == "format");
        Assert.Null(LegacyPackageWriter.WriteMap(c, "junk"));
        CampaignConversion cc = LegacyCampaignConverter.Convert(FullMap().Build(), "map-not-campaign.wcm");
        Assert.False(cc.Succeeded);
        Assert.Contains("\"errors\": 1", Encoding.UTF8.GetString(c.Report.ToJson()), StringComparison.Ordinal);
    }

    [Fact]
    public void Corpus_runner_reports_the_pass_rate()
    {
        string dir = Path.Combine(Path.GetTempPath(), "t2-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.wmp"), FullMap("A").Build());
            File.WriteAllBytes(Path.Combine(dir, "sub", "c.wcm"), SyntheticMap.Campaign("C", "D", [("A", true, FullMap("A").Build())]));
            File.WriteAllBytes(Path.Combine(dir, "broken.wmp"), [0, 1, 2]);
            File.WriteAllBytes(Path.Combine(dir, "readme.txt"), [0]);
            IReadOnlyList<CorpusEntry> entries = LegacyCorpus.Run(dir);
            Assert.Equal(["a.wmp", "broken.wmp", "sub/c.wcm"], entries.Select(e => e.Path).ToArray());
            Assert.Equal(666, LegacyCorpus.PassPermille(entries));
            Assert.Contains("2/3 files convert (66.6 %)", LegacyCorpus.Summary(entries), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
