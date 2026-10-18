using System;
using System.Collections.Immutable;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Content.Editing;
using Tzar2.Game.Campaigns;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>The editable map document, its transactions and the unlimited undo history (REQ-EDT-02, ADR 0150).</summary>
public class DocumentTests
{
    private const int Tile = MapData.UnitsPerTile;

    internal static TerrainRules Rules { get; } = TerrainRules.ForSet("grass", null, Fixtures.Classic);

    [Fact]
    public void An_empty_map_builds_into_the_compiled_contract()
    {
        MapDocument doc = MapDocument.CreateEmpty("Valley", 64, 48, Rules);
        MapData map = doc.BuildMap();
        Assert.Equal(64, map.Width);
        Assert.Equal(48, map.Height);
        Assert.Equal("grass", map.TerrainSet);
        Assert.All(map.SetTerrainCorners.ToArray(), c => Assert.Equal(1, c));
        ScenarioData s = doc.BuildScenario();
        Assert.Equal(0, s.ObjectCount);
        Assert.False(doc.IsDirty);
        Assert.False(doc.History.CanUndo);
    }

    [Fact]
    public void Terrain_strokes_are_one_step_and_undo_restores_every_layer()
    {
        MapDocument doc = MapDocument.CreateEmpty("Lake", 16, 16, Rules);
        byte water = doc.BuildMap().TerrainCorners[0];
        DocumentChange? last = null;
        doc.Changed += c => last = c;
        using (EditTransaction tx = doc.Begin("Paint water"))
        {
            for (int x = 3; x <= 6; x++)
            {
                tx.Paint(x, 4, 0);
                tx.Flush();
                Assert.Equal(ChangeKind.Edit, last!.Value.Kind);
            }

            tx.SetCliff(10, 10, true);
            Assert.True(tx.Commit());
        }

        Assert.Equal(0, doc.PaintedAt(4, 4));
        Assert.NotEqual(water, doc.TerrainAt(4, 4));
        Assert.True(doc.IsCliff(10, 10));
        Assert.Equal("Paint water", doc.History.UndoLabel);
        Assert.True(doc.IsDirty);
        Assert.Equal((2, 3, 10, 10), (last!.Value.TileLeft, last.Value.TileTop, last.Value.TileRight, last.Value.TileBottom));

        doc.Undo();
        Assert.Equal(1, doc.PaintedAt(4, 4));
        Assert.False(doc.IsCliff(10, 10));
        Assert.Equal(ChangeKind.Undo, last!.Value.Kind);
        Assert.False(doc.IsDirty);
        doc.Redo();
        Assert.Equal(0, doc.PaintedAt(4, 4));
        Assert.True(doc.IsCliff(10, 10));
    }

    [Fact]
    public void A_transaction_disposed_without_commit_rolls_back()
    {
        MapDocument doc = MapDocument.CreateEmpty("Roll", 8, 8, Rules);
        using (EditTransaction tx = doc.Begin("Abandoned"))
        {
            tx.Paint(1, 1, 0);
            tx.State = tx.State.Add(MapObject.AtTile("comm.tree", MapObject.Nature, 2, 2), null, out _);
        }

        Assert.Equal(1, doc.PaintedAt(1, 1));
        Assert.Empty(doc.State.Objects);
        Assert.False(doc.History.CanUndo);
        Assert.False(doc.InTransaction);
    }

    [Fact]
    public void An_edit_that_changes_nothing_records_no_step()
    {
        MapDocument doc = MapDocument.CreateEmpty("Same", 8, 8, Rules);
        using EditTransaction tx = doc.Begin("Nothing");
        tx.Paint(1, 1, 1);
        Assert.False(tx.Commit());
        Assert.False(doc.History.CanUndo);
    }

    [Fact]
    public void Only_one_edit_may_be_open()
    {
        MapDocument doc = MapDocument.CreateEmpty("One", 8, 8, Rules);
        using EditTransaction tx = doc.Begin("First");
        Assert.Throws<InvalidOperationException>(() => doc.Begin("Second"));
        Assert.Throws<InvalidOperationException>(doc.Undo);
    }

    [Fact]
    public void History_is_unlimited_and_survives_save()
    {
        MapDocument doc = MapDocument.CreateEmpty("Long", 32, 32, Rules);
        for (int i = 0; i < 500; i++)
        {
            doc.Apply("Place " + i, s => s.Add(new MapObject("comm.tree", MapObject.Nature, (i % 32 * Tile) + 1024, (i / 32 * Tile) + 1024), null, out _));
        }

        doc.MarkSaved();
        Assert.False(doc.IsDirty);
        Assert.Equal(500, doc.History.UndoSteps.Count);
        for (int i = 0; i < 500; i++)
        {
            doc.Undo();
        }

        Assert.Empty(doc.State.Objects);
        Assert.True(doc.IsDirty);
        for (int i = 0; i < 500; i++)
        {
            doc.Redo();
        }

        Assert.Equal(500, doc.State.Objects.Count);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void A_new_edit_after_undo_clears_the_redo_stack()
    {
        MapDocument doc = MapDocument.CreateEmpty("Branch", 8, 8, Rules);
        doc.Apply("A", s => s with { Name = "A" });
        doc.Undo();
        Assert.True(doc.History.CanRedo);
        doc.Apply("B", s => s with { Name = "B" });
        Assert.False(doc.History.CanRedo);
        Assert.Equal("B", doc.State.Name);
    }

    [Fact]
    public void Deleting_a_referenced_object_cleans_the_triggers_and_undo_brings_everything_back()
    {
        MapDocument doc = MapDocument.CreateEmpty("Refs", 32, 32, Rules);
        int hero = 0, gate = 0;
        doc.Apply("Place", s => s
            .Add(new MapObject("euro.footman", 0, (5 * Tile) + 1024, (6 * Tile) + 1024), new ObjectInfo("Hero", "The Hero", ObjectFlags.Persistent), out hero)
            .Add(MapObject.AtTile("euro.house", 0, 10, 10), null, out gate));
        doc.Apply("Triggers", s => s.WithScenario(m => m
            .AddEvent(new ObjectInAreaEvent("AtGate", new NamedObjects(new[] { hero, gate }, Quantifier.All), new ObjectArea(hero)), out _)
            .AddEvent(new ObjectsEnterHolderEvent("InHouse", new NamedObjects(new[] { hero }, Quantifier.Any), gate), out _)
            .AddAction(new LockViewAction("Look", 1000, new ObjectArea(hero)), out _)));
        Assert.Equal(4, doc.State.ReferencesTo(hero).Length);

        doc.Apply("Delete hero", s => s.Remove([hero]));
        ScenarioModel m = doc.State.Scenario;
        Assert.Equal(new[] { gate }, ((NamedObjects)((ObjectInAreaEvent)m.Events[0]).Objects).ObjectIndices.Items.ToArray());
        Assert.Equal(new RectArea(5, 6, 5, 6), ((ObjectInAreaEvent)m.Events[0]).Area);
        Assert.Equal(gate, ((ObjectsEnterHolderEvent)m.Events[1]).HolderIndex);
        Assert.Empty(doc.State.ReferencesTo(hero));

        // The built scenario uses indices: the house is now object 0.
        ScenarioData built = doc.BuildScenario();
        Assert.Equal(0, ((ObjectsEnterHolderEvent)built.Events[1]).HolderIndex);

        doc.Undo();
        Assert.Equal(4, doc.State.ReferencesTo(hero).Length);
        Assert.Equal(2, doc.State.Objects.Count);
    }

    [Fact]
    public void Deleting_an_action_removes_it_from_every_rule_and_fixes_the_index_references()
    {
        MapDocument doc = MapDocument.CreateEmpty("Actions", 16, 16, Rules);
        doc.Apply("Triggers", s => s.WithScenario(m => m
            .AddAction(new AddQuestAction("Quest", "Find the well.", null), out _)
            .AddAction(new SetTimerAction("Clock", ScenarioModel.Source("1000"), true), out _)
            .AddAction(new StopTimerAction("Stop", 1), out _)
            .AddAction(new RemoveQuestAction("Done", 0), out _)
            .AddRule(new RuleDef("start", ScenarioModel.Source("1"), new[] { 0, 1 }), out _)
            .AddRule(new RuleDef("end", ScenarioModel.Source("Clock == 0"), new[] { 2, 3, 0 }), out _)));

        doc.Apply("Delete quest", s => s.WithScenario(m => m.RemoveAction(0)));
        ScenarioModel m = doc.State.Scenario;
        Assert.Equal(3, m.Actions.Length);
        Assert.Equal(new[] { 0 }, m.Rules[0].ActionIndices.Items.ToArray());
        Assert.Equal(new[] { 1, 2 }, m.Rules[1].ActionIndices.Items.ToArray());
        Assert.Equal(0, ((StopTimerAction)m.Actions[1]).SetTimerActionIndex);
        Assert.Equal(-1, ((RemoveQuestAction)m.Actions[2]).AddQuestActionIndex);

        // The timer is still found by name: the condition resolves to a TimerRef on the shifted index.
        ScenarioData built = doc.BuildScenario();
        Assert.IsType<Content.Compiled.Scenario.Expressions.BinaryExpr>(built.Rules[1].Condition.Root);
        Assert.Contains("TimerRef { ActionIndex = 0 }", built.Rules[1].Condition.Root!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Moving_an_action_keeps_rules_and_references_pointing_at_the_same_actions()
    {
        ScenarioModel m = ScenarioModel.Empty("Move")
            .AddAction(new SetTimerAction("Clock", ScenarioModel.Source("0"), false), out _)
            .AddAction(new KeepRuleAction("Keep"), out _)
            .AddAction(new StopTimerAction("Stop", 0), out _)
            .AddRule(new RuleDef("r", ScenarioModel.Source("1"), new[] { 2, 0, 1 }), out _);
        ScenarioModel moved = ScenarioRewriter.MoveAction(m, 0, 2);
        Assert.Equal(new[] { "Keep", "Stop", "Clock" }, moved.Actions.Items.Select(a => a.Name).ToArray());
        Assert.Equal(new[] { "Stop", "Clock", "Keep" }, moved.Rules[0].ActionIndices.Items.Select(i => moved.Actions[i].Name).ToArray());
        Assert.Equal(2, ((StopTimerAction)moved.Actions[1]).SetTimerActionIndex);
    }

    [Fact]
    public void Deleting_a_phrase_or_a_conversation_keeps_the_graph_valid()
    {
        var talk = new ConversationDef("talk", new[]
        {
            new PhraseDef("a", true, null, ExpressionDef.Empty, ExpressionDef.Empty, "Hello", null, null, new[] { 1, 2 }),
            new PhraseDef("b", false, null, ExpressionDef.Empty, ExpressionDef.Empty, "Yes", null, null, new[] { 2 }),
            new PhraseDef("c", false, null, ExpressionDef.Empty, ExpressionDef.Empty, "No", null, null, Array.Empty<int>()),
        });
        ScenarioModel m = ScenarioModel.Empty("Talk")
            .AddConversation(new ConversationDef("other", EquatableArray<PhraseDef>.Empty), out _)
            .AddConversation(talk, out _)
            .AddAction(new ConversationAction("Start", 1), out _)
            .AddAction(new ConversationAction("StartOther", 0), out _)
            .RemovePhrase(1, 1);
        Assert.Equal(new[] { 1 }, m.Conversations[1].Phrases[0].FollowUps.Items.ToArray());
        m = m.RemoveConversation(0);
        Assert.Equal(0, ((ConversationAction)m.Actions[0]).ConversationIndex);
        Assert.Equal(-1, ((ConversationAction)m.Actions[1]).ConversationIndex);
    }

    [Fact]
    public void Undeclared_variables_are_appended_in_a_fixed_order_on_build()
    {
        MapDocument doc = MapDocument.CreateEmpty("Vars", 8, 8, Rules);
        doc.Apply("Triggers", s => s.WithScenario(m => m.AddVariable("Declared")
            .AddAction(new ExpressionAction("Count", ScenarioModel.Source("$Later = $Declared + 1")), out _)
            .AddRule(new RuleDef("r", ScenarioModel.Source("$First > 0 || $health > 0"), new[] { 0 }), out _)));
        ScenarioData s = doc.BuildScenario();
        Assert.Equal(new[] { "Declared", "Later", "First" }, s.Variables.Items.ToArray());
    }

    [Fact]
    public void Copy_and_move_keep_properties_and_clamp_to_the_map()
    {
        MapDocument doc = MapDocument.CreateEmpty("Copy", 8, 8, Rules);
        int a = 0;
        doc.Apply("Place", s => s.Add(new MapObject("euro.peon", 1, 1024, 1024), new ObjectInfo("Bob", "", ObjectFlags.NoAi, Level: 3), out a));
        ImmutableArray<int> copies = default;
        doc.Apply("Copy", s => s.CopyBy([a], 4 * Tile, 0, doc.Width, doc.Height, out copies));
        EditorObject copy = doc.State.Find(copies[0])!;
        Assert.Equal(3, copy.Info.Level);
        Assert.Equal((4 * Tile) + 1024, copy.X);
        doc.Apply("Move", s => s.MoveBy([a], -100 * Tile, 0, doc.Width, doc.Height));
        Assert.Equal(0, doc.State.Find(a)!.X);
    }

    [Fact]
    public void Search_and_replace_of_object_references()
    {
        MapDocument doc = MapDocument.CreateEmpty("Replace", 16, 16, Rules);
        int a = 0, b = 0;
        doc.Apply("Place", s => s.Add(new MapObject("euro.peon", 0, 1024, 1024), null, out a).Add(new MapObject("euro.peon", 0, 3000, 3000), null, out b));
        doc.Apply("Trig", s => s.WithScenario(m => m
            .AddEvent(new ObjectDestroyedEvent("Dead", new NamedObjects(new[] { a }, Quantifier.All)), out _)
            .AddAction(new ScrollViewAction("Look", new ObjectArea(a), new ObjectArea(a)), out _)));
        int replaced = 0;
        doc.Apply("Replace", s => s.ReplaceReferences(a, b, out replaced));
        Assert.Equal(3, replaced);
        Assert.Empty(doc.State.ReferencesTo(a));
        Assert.Equal(3, doc.State.ReferencesTo(b).Length);
    }

    [Fact]
    public void A_scenario_opened_from_compiled_data_builds_back_unchanged()
    {
        MissionContent mill = DemoCampaign.Create().Load("mill");
        MapDocument doc = MapDocument.FromCompiled(mill.Map, mill.Scenario, TerrainRules.WithoutSet(Fixtures.Classic));
        Assert.Equal(mill.Map.ContentHash, doc.BuildMap().ContentHash);
        Assert.Equal(mill.Scenario.ContentHash, doc.BuildScenario().ContentHash);
    }
}
