using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Content.Editing;
using Tzar2.Game.Editing;
using Tzar2.Game.Session;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>The rule editor (TDD §18.2 "Rules", "Conversations"), named areas and groups, localization, Lua, playtest and the trigger debugger (§18.3).</summary>
public class TriggerEditorTests
{
    private const int Tile = MapData.UnitsPerTile;

    private static IEnumerable<Type> Concrete<TBase>() => typeof(TBase).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(TBase)) && !t.IsAbstract);

    [Fact]
    public void Every_event_and_action_of_the_IR_has_a_property_page()
    {
        Assert.Equal(Concrete<EventDef>().OrderBy(t => t.Name), TriggerSchema.Events.Select(k => k.RecordType).OrderBy(t => t.Name));
        Assert.Equal(Concrete<ActionDef>().OrderBy(t => t.Name), TriggerSchema.Actions.Select(k => k.RecordType).OrderBy(t => t.Name));

        // Appendix C: 8 events and 27 actions, plus the undocumented Delay and the kept unknown kinds.
        Assert.Equal(8, TriggerSchema.Events.Count(k => k.RecordType != typeof(UnknownEvent)));
        Assert.Equal(27, TriggerSchema.Actions.Count(k => k.RecordType != typeof(UnknownAction) && k.RecordType != typeof(DelayAction)));
    }

    [Fact]
    public void Every_field_reads_and_writes_back()
    {
        foreach (TriggerKind kind in TriggerSchema.Events.Concat(TriggerSchema.Actions))
        {
            object record = kind.Create("probe");
            Assert.IsType(kind.RecordType, record);
            Assert.Equal("probe", record is EventDef e ? e.Name : ((ActionDef)record).Name);
            Assert.Same(kind, record is EventDef ev ? TriggerSchema.Of(ev) : TriggerSchema.Of((ActionDef)record));
            foreach (TriggerField f in kind.Fields)
            {
                object? value = f.Get(record);
                object written = f.Set(record, value);
                Assert.Equal(record, written);
                object? changed = Sample(f.Kind, value);
                if (changed is not null && f.Kind != FieldKind.Raw)
                {
                    object next = f.Set(record, changed);
                    Assert.Equal(changed, f.Get(next));
                }
            }
        }
    }

    private static object? Sample(FieldKind kind, object? current) => kind switch
    {
        FieldKind.Text or FieldKind.LongText or FieldKind.Order or FieldKind.TypeKey => "changed",
        FieldKind.MapName => "Next",
        FieldKind.Integer or FieldKind.Milliseconds or FieldKind.Holder or FieldKind.Conversation or FieldKind.QuestAction or FieldKind.TimerAction => 7,
        FieldKind.Kingdom or FieldKind.Owner => 3,
        FieldKind.Boolean => !(bool)current!,
        FieldKind.Players => (ushort)5,
        FieldKind.Objects or FieldKind.OptionalObjects => new TypedObjects("euro.knight", 1, CountComparison.AtLeast, 2, Quantifier.Any),
        FieldKind.Area => new RectArea(1, 2, 3, 4),
        FieldKind.Expression => ScenarioModel.Source("$x > 1"),
        FieldKind.Image or FieldKind.Sound or FieldKind.Music => new ResourceRef("user/a.png"),
        FieldKind.Stance => Stance.Ally,
        _ => null,
    };

    [Fact]
    public void Rules_conditions_and_action_ordering()
    {
        MapDocument doc = MapDocument.CreateEmpty("Rules", 16, 16, Fixtures.RulesFor("grass"));
        doc.Apply("T", s => s.WithScenario(m => m
            .AddAction(new KeepRuleAction("keep"), out _)
            .AddAction(new SendChatAction("hello", PlayerMasks.AllPlayers, "hi"), out _)
            .AddRule(new RuleDef("r", ScenarioModel.Source("1"), new[] { 0, 1 }), out _)
            .AddRule(new RuleDef("s", ScenarioModel.Source("0"), new[] { 1 }), out _)
            .MoveRuleAction(0, 0, 1)
            .MoveRule(1, 0)));
        ScenarioModel m = doc.State.Scenario;
        Assert.Equal(["s", "r"], m.Rules.Items.Select(r => r.Name).ToArray());
        Assert.Equal([1, 0], m.Rules[1].ActionIndices.Items.ToArray());
        Assert.Null(ExpressionHelp.Check(m, "Clock == 0 && $gold > @gold(2)"));
        Assert.Contains("position", ExpressionHelp.Check(m, "1 +"), StringComparison.Ordinal);
    }

    [Fact]
    public void Expression_vocabulary_and_completion()
    {
        ScenarioModel m = ScenarioModel.Empty("V").AddVariable("Score").AddEvent(new PlayerWonEvent("Won", 1), out _).AddAction(new SetTimerAction("Clock", ExpressionDef.Empty, true), out _);
        ImmutableCompletions c = new(ExpressionHelp.Vocabulary(m));
        Assert.Contains(("$health", "reserved"), c.Pairs);
        Assert.Contains(("@rnd(", "function"), c.Pairs);
        Assert.Contains(("$Score", "variable"), c.Pairs);
        Assert.Contains(("Won", "event"), c.Pairs);
        Assert.Contains(("Clock", "timer"), c.Pairs);
        Assert.All(ExpressionHelp.Complete(m, "@g"), x => Assert.StartsWith("@g", x.Text, StringComparison.Ordinal));
    }

    private sealed record ImmutableCompletions(System.Collections.Immutable.ImmutableArray<Completion> All)
    {
        public IEnumerable<(string, string)> Pairs => All.Select(c => (c.Text, c.Kind));
    }

    [Fact]
    public void Renaming_an_event_timer_or_variable_rewrites_every_expression()
    {
        ScenarioModel m = ScenarioModel.Empty("Ren")
            .AddVariable("count")
            .AddEvent(new PlayerWonEvent("won", 1), out _)
            .AddAction(new SetTimerAction("clock", ScenarioModel.Source("$count * 1000"), true), out _)
            .AddAction(new ExpressionAction("calc", ScenarioModel.Source("$count = $count + 1; $counter = 2")), out _)
            .AddRule(new RuleDef("r", ScenarioModel.Source("won && clock == 0 && wonder"), new[] { 1 }), out _);
        m = ExpressionHelp.Rename(m, NameKind.Event, "won", "victory", out int a);
        m = ExpressionHelp.Rename(m, NameKind.Timer, "clock", "dawn", out int b);
        m = ExpressionHelp.Rename(m, NameKind.Variable, "count", "total", out int c);
        Assert.Equal((1, 1, 2), (a, b, c));
        Assert.Equal("victory && dawn == 0 && wonder", m.Rules[0].Condition.Source);
        Assert.Equal("$total = $total + 1; $counter = 2", ((ExpressionAction)m.Actions[1]).Expression.Source);
        Assert.Equal(["total"], m.Variables.Items.ToArray());
        Assert.Equal("victory", m.Events[0].Name);
    }

    [Fact]
    public void Conversations_phrases_initial_flags_and_follow_ups()
    {
        MapDocument doc = MapDocument.CreateEmpty("Talk", 16, 16, Fixtures.RulesFor("grass"));
        int speaker = 0;
        doc.Apply("Speaker", s => s.Add(new MapObject("euro.peon", 0, 1024, 1024), new ObjectInfo("Miller", "The Miller", ObjectFlags.None), out speaker));
        doc.Apply("Talk", s => s.WithScenario(m => m
            .AddConversation(new ConversationDef("talk", EquatableArray<PhraseDef>.Empty), out int c)
            .AddPhrase(0, new PhraseDef("ask", true, speaker, ExpressionDef.Empty, ExpressionDef.Empty, "Help?", new ResourceRef("user/face.png"), new ResourceRef("user/ask.wav"), new[] { 1, 2 }), out _)
            .AddPhrase(0, new PhraseDef("yes", false, null, ScenarioModel.Source("$gold > 0"), ScenarioModel.Source("$Agreed = 1"), "Yes.", null, null, Array.Empty<int>()), out _)
            .AddPhrase(0, new PhraseDef("no", false, null, ExpressionDef.Empty, ScenarioModel.Source("$Agreed = 2"), "No.", null, null, Array.Empty<int>()), out _)
            .AddAction(new ConversationAction("Ask", 0), out _)));
        ScenarioData built = doc.BuildScenario();
        PhraseDef ask = built.Conversations[0].Phrases[0];
        Assert.Equal((true, 0, 2), (ask.Initial, ask.SourceObject!.Value, ask.FollowUps.Length));
        Assert.Contains("Agreed", built.Variables.Items);
        doc.Apply("Remove no", s => s.WithScenario(m => m.RemovePhrase(0, 2)));
        Assert.Equal([1], doc.State.Scenario.Conversations[0].Phrases[0].FollowUps.Items.ToArray());
    }

    [Fact]
    public void Quests_are_add_quest_actions_and_remove_quest_finds_them()
    {
        ScenarioModel m = ScenarioModel.Empty("Q")
            .AddAction(new AddQuestAction("Find the well", "Look north.", null), out int add)
            .AddAction(new RemoveQuestAction("Done", add), out _);
        Assert.Equal(add, ((RemoveQuestAction)m.Actions[1]).AddQuestActionIndex);
        Assert.Single(m.Actions.Items.OfType<AddQuestAction>());
    }

    [Fact]
    public void Named_areas_and_object_groups_follow_their_edits()
    {
        MapDocument doc = MapDocument.CreateEmpty("Areas", 32, 32, Fixtures.RulesFor("grass"));
        int a = 0, b = 0, c = 0;
        doc.Apply("Place", s => s.Add(new MapObject("euro.peon", 0, 1024, 1024), null, out a).Add(new MapObject("euro.peon", 0, 3000, 1024), null, out b).Add(new MapObject("euro.peon", 0, 5000, 1024), null, out c));
        var gate = new RectArea(10, 10, 12, 12);
        doc.Apply("Area", s => s.SetArea("Gate", gate, out _).SetGroup("Guards", [a, b], out _));
        doc.Apply("Use", s => s.WithScenario(m => m
            .AddEvent(new ObjectInAreaEvent("AtGate", new NamedObjects(new[] { b, a }, Quantifier.Any), gate), out _)
            .AddAction(new LockViewAction("Look", 1000, gate), out _)));
        int moved = 0, regrouped = 0;
        doc.Apply("Move area", s => s.SetArea("Gate", new RectArea(20, 20, 22, 22), out moved));
        doc.Apply("Regroup", s => s.SetGroup("Guards", [a, b, c], out regrouped));
        Assert.Equal((2, 1), (moved, regrouped));
        var e = (ObjectInAreaEvent)doc.State.Scenario.Events[0];
        Assert.Equal(new RectArea(20, 20, 22, 22), e.Area);
        Assert.Equal(3, ((NamedObjects)e.Objects).ObjectIndices.Length);
        doc.Apply("Remove", s => s.RemoveArea("Gate").RemoveGroup("Guards"));
        Assert.Empty(doc.State.Extras.Areas);
        Assert.Equal(new RectArea(20, 20, 22, 22), ((LockViewAction)doc.State.Scenario.Actions[0]).Area);
    }

    [Fact]
    public void Localization_exports_and_imports_po()
    {
        MapDocument doc = PackageTests.Sample();
        doc.Apply("Quest", s => s.WithScenario(m => m.AddAction(new AddQuestAction("Q", "Cross the \"river\".\nQuickly.", null), out _)));
        string po = MapStrings.ExportPo(doc.State, "en");
        Assert.Contains("msgctxt \"action/Q/text\"\nmsgid \"Cross the \\\"river\\\".\\nQuickly.\"", po, StringComparison.Ordinal);
        Assert.Contains("msgctxt \"map/title\"", po, StringComparison.Ordinal);
        string translated = po.Replace("msgid \"The Ford\"\nmsgstr \"\"", "msgid \"The Ford\"\nmsgstr \"Брод\"", StringComparison.Ordinal)
            .Replace("msgid \"Cross the \\\"river\\\".\\nQuickly.\"\nmsgstr \"\"", "msgid \"x\"\nmsgstr \"Перейди \\\"реку\\\".\\nБыстро.\"", StringComparison.Ordinal);
        int applied = 0;
        doc.Apply("Import", s => MapStrings.ImportPo(s, translated, out applied));
        Assert.Equal(2, applied);
        Assert.Equal("Брод", doc.State.Scenario.Properties.Title);
        Assert.Equal("Перейди \"реку\".\nБыстро.", ((AddQuestAction)doc.State.Scenario.Actions[^1]).Text);
        Assert.Throws<FormatException>(() => MapStrings.ParsePo("msgctxt nope"));
    }

    [Fact]
    public void Lua_scripts_are_stored_and_checked()
    {
        MapDocument doc = MapDocument.CreateEmpty("Lua", 8, 8, Fixtures.RulesFor("grass"));
        doc.Apply("Script", s => s.SetScript("Main", "function on_start()\n  if x then\n    print(\"hi\")\n  end\nend\n"));
        Assert.Equal(["main.lua"], doc.State.Scripts.Keys.ToArray());
        Assert.Empty(LuaScripts.Check(doc.State.Scripts["main.lua"]));
        Assert.Equal(1, Assert.Single(LuaScripts.Check("function f()\n  return 1\n")).Line);
        Assert.Equal(2, LuaScripts.Check("x = 1\ny = \"open\n").Single().Line);
        Assert.Contains(LuaScripts.Check("repeat x = x + 1 end"), i => i.Message.Contains("until", StringComparison.Ordinal));
        Assert.Empty(LuaScripts.Check("--[[ long\ncomment ]] local t = { [[a]], 'b' } while t do break end repeat until true"));
        MapDocument back = MapPackage.Read(MapPackage.Write(doc), Fixtures.RulesFor);
        Assert.Equal(doc.State.Scripts, back.State.Scripts);
    }

    private static MapDocument Mission()
    {
        MapDocument doc = MapDocument.CreateEmpty("Test", 40, 24, Fixtures.RulesFor(null));
        int hero = 0;
        doc.Apply("Objects", s => s
            .Add(new MapObject("euro.footman", 0, (4 * Tile) + 1024, (10 * Tile) + 1024), new ObjectInfo("Hero", "", ObjectFlags.None), out hero)
            .Add(new MapObject("euro.peon", 1, (30 * Tile) + 1024, (10 * Tile) + 1024), null, out _));
        doc.Apply("Scenario", s => s.WithScenario(m => m
            .WithKingdom(ScenarioModel.DefaultKingdom(1) with { StartTileX = 4, StartTileY = 10 })
            .WithKingdom(ScenarioModel.DefaultKingdom(2) with { StartTileX = 30, StartTileY = 10 })
            .AddAction(new SetTimerAction("Clock", ScenarioModel.Source("500"), true), out _)
            .AddAction(new ExpressionAction("Mark", ScenarioModel.Source("$Done = 1")), out _)
            .AddAction(new AddQuestAction("Wait", "Wait for the clock.", null), out _)
            .AddRule(new RuleDef("start", ScenarioModel.Source("1"), new[] { 0, 2 }), out _)
            .AddRule(new RuleDef("ring", ScenarioModel.Source("Clock == 0 && $Done == 0"), new[] { 1 }), out _)));
        _ = hero;
        return doc;
    }

    [Fact]
    public void Playtest_as_any_kingdom_with_the_map_revealed()
    {
        MapDocument doc = Mission();
        Assert.False(Playtest.Check(doc, Fixtures.Classic).BlocksPlay);
        MatchSetup own = Playtest.Setup(Playtest.Mission(doc), Fixtures.Classic, new PlaytestOptions());
        Assert.Equal(0, own.LocalPlayer);
        MatchSetup second = Playtest.Setup(Playtest.Mission(doc), Fixtures.Classic, new PlaytestOptions(Kingdom: 2, RevealMap: true));
        Assert.Equal(1, second.LocalPlayer);
        Assert.True(second.NoFog && second.Explored && second.CheatsAllowed);
        Assert.Equal(ControllerKind.Computer, second.Players[0].Controller);
        using GameSession session = Playtest.Start(doc, Fixtures.Classic, new PlaytestOptions(Kingdom: 2));
        session.Step(60);
        Assert.Null(session.Fault);
        Assert.Equal(1, session.LocalPlayer);
    }

    [Fact]
    public void The_debugger_watches_variables_tracks_results_and_breaks_on_rules_and_actions()
    {
        MapDocument doc = Mission();
        using GameSession session = Playtest.Start(doc, Fixtures.Classic, new PlaytestOptions());
        using var debugger = new TriggerDebugger(session);
        debugger.BreakOnRule(1, true);
        Assert.True(debugger.HasRuleBreak(1));
        session.Step(5);
        DebugWatch w = debugger.Watch;
        Assert.Equal(1, w.Rules[0].LastValue);
        Assert.True(w.Rules[0].Disabled);
        Assert.Equal(0, w.Rules[1].LastValue);
        Assert.Contains(w.Timers, t => t.Name == "Clock" && t.Running);
        Assert.False(session.Paused);
        Assert.Contains(debugger.Log, l => l.Text.Contains("action 'Clock'", StringComparison.Ordinal));

        // The clock runs out after 500 ms; the next pass fires "ring" and the debugger pauses the session.
        for (int i = 0; i < 100 && !session.Paused; i++)
        {
            session.Step(1);
        }

        Assert.True(session.Paused);
        Assert.Contains("rule 'ring' fired", debugger.Hit, StringComparison.Ordinal);
        Assert.Contains(debugger.Watch.Variables, v => v.Name == "Done" && v.Value == 1);
        debugger.Continue();
        Assert.False(session.Paused);
        Assert.Null(debugger.Hit);
        debugger.BreakOnAction(0, true);
        Assert.True(debugger.HasActionBreak(0));
    }

    [Fact]
    public void A_debugged_playtest_plays_exactly_like_an_undebugged_one()
    {
        MapDocument doc = Mission();
        using GameSession plain = Playtest.Start(doc, Fixtures.Classic, new PlaytestOptions());
        using GameSession traced = Playtest.Start(doc, Fixtures.Classic, new PlaytestOptions());
        using var debugger = new TriggerDebugger(traced) { LogEvaluations = true };
        plain.Step(120);
        traced.Step(120);
        Assert.Equal(plain.Simulation.ComputeFullHash(), traced.Simulation.ComputeFullHash());
        Assert.NotEmpty(debugger.Log);
    }
}
