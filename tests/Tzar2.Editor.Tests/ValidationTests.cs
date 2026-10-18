using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Content.Editing;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>The live validation panel's checks (TDD §18.3, Appendix C.10).</summary>
public class ValidationTests
{
    private static ValidationResult Check(MapDocument doc) => MapValidator.Validate(doc, new ValidationOptions { Ruleset = Fixtures.Classic });

    [Fact]
    public void The_sample_map_has_no_errors()
    {
        ValidationResult r = Check(PackageTests.Sample());
        Assert.DoesNotContain(r.Issues, i => i.Severity >= ValidationSeverity.Error);
        Assert.False(r.BlocksPlay);
    }

    [Fact]
    public void Unknown_types_and_bad_owners_block_play_but_the_map_stays_editable()
    {
        MapDocument doc = MapDocument.CreateEmpty("Bad", 16, 16, Fixtures.RulesFor("grass"));
        doc.Apply("Place", s => s
            .Add(new MapObject("orcs.warlord", 0, 1024, 1024), null, out _)
            .Add(new MapObject("euro.peon", 12, 3000, 3000), null, out _)
            .Add(MapObject.AtTile("euro.castle", 0, 14, 14), null, out _));
        ValidationResult r = Check(doc);
        Assert.True(r.BlocksPlay);
        Assert.Single(r.WithCode("object.type"));
        Assert.Single(r.WithCode("object.owner"));
        Assert.Single(r.WithCode("object.footprint"));
        int bad = doc.State.Objects[0].Id;
        Assert.Equal(new EditorLocation(LocationKind.Object, bad, X: 0, Y: 0), r.WithCode("object.type")[0].Location);
        doc.Apply("Fix", s => s.Remove(s.Objects.Select(o => o.Id).ToArray()));
        Assert.False(Check(doc).BlocksPlay);
    }

    [Fact]
    public void Expression_names_and_trigger_references_are_checked()
    {
        MapDocument doc = MapDocument.CreateEmpty("Triggers", 16, 16, Fixtures.RulesFor("grass"));
        doc.Apply("T", s => s.WithScenario(m => m
            .AddVariable("health")
            .AddEvent(new PlayerDefeatedEvent("lost", PlayerMasks.Of(2)), out _)
            .AddEvent(new PlayerDefeatedEvent("lost", PlayerMasks.Of(3)), out _)
            .AddAction(new StopTimerAction("stop", -1), out _)
            .AddAction(new ExpressionAction("calc", ScenarioModel.Source("$x = (1 +")), out _)
            .AddRule(new RuleDef("r1", ScenarioModel.Source("lost && nothing"), new[] { 0 }), out _)
            .AddRule(new RuleDef("r2", ExpressionDef.Empty, System.Array.Empty<int>()), out _)));
        ValidationResult r = Check(doc);
        Assert.Single(r.WithCode("variable.reserved"));
        Assert.Single(r.WithCode("event.name"));
        Assert.Single(r.WithCode("action.timer"));
        Assert.Single(r.WithCode("expression.parse"));
        Assert.Single(r.WithCode("expression.name"));
        Assert.Single(r.WithCode("rule.empty"));
        Assert.Single(r.WithCode("rule.condition"));
        Assert.Single(r.WithCode("action.unused"));
        Assert.Equal(LocationKind.Rule, r.WithCode("expression.name")[0].Location.Kind);
    }

    [Fact]
    public void Conversations_follow_the_original_limits_and_cycles_are_reported()
    {
        MapDocument doc = MapDocument.CreateEmpty("Talk", 16, 16, Fixtures.RulesFor("grass"));
        var loop = new ConversationDef("loop", new[]
        {
            new PhraseDef("a", true, null, ExpressionDef.Empty, ExpressionDef.Empty, new string('x', 1001), null, null, new[] { 1 }),
            new PhraseDef("b", false, null, ExpressionDef.Empty, ExpressionDef.Empty, "back", null, null, new[] { 0 }),
        });
        doc.Apply("T", s => s.WithScenario(m => m
            .AddConversation(loop, out _)
            .AddConversation(new ConversationDef("mute", new[] { new PhraseDef("x", false, null, ExpressionDef.Empty, ExpressionDef.Empty, "x", null, null, System.Array.Empty<int>()) }), out _)));
        ValidationResult r = Check(doc);
        Assert.Single(r.WithCode("conversation.cycle"));
        Assert.Single(r.WithCode("phrase.text"));
        Assert.Single(r.WithCode("conversation.initial"));
    }

    [Fact]
    public void Missing_resources_and_start_locations_are_warnings()
    {
        MapDocument doc = MapDocument.CreateEmpty("Res", 16, 16, Fixtures.RulesFor("grass"));
        doc.Apply("T", s => s.WithScenario(m => m
            .WithKingdom(ScenarioModel.DefaultKingdom(1))
            .AddAction(new ShowMessageAction("hi", "Hi", "Hello", new ResourceRef("user/missing.png")), out _)));
        ValidationResult r = Check(doc);
        Assert.Single(r.WithCode("resource.missing"));
        Assert.Single(r.WithCode("kingdom.start"));
        Assert.Empty(MapValidator.Validate(doc, new ValidationOptions { ExternalAsset = p => p == "user/missing.png" }).WithCode("resource.missing"));
    }

    [Fact]
    public void Cliffs_on_water_and_land_units_in_water_are_flagged()
    {
        MapDocument doc = MapDocument.CreateEmpty("Coast", 8, 8, Fixtures.RulesFor("grass"));
        using (EditTransaction tx = doc.Begin("Sea"))
        {
            for (int x = 0; x <= 2; x++)
            {
                for (int y = 0; y <= 2; y++)
                {
                    tx.Paint(x, y, 0);
                }
            }

            tx.SetCliff(0, 0, true);
            tx.Commit();
        }

        doc.Apply("Unit", s => s.Add(new MapObject("euro.footman", 0, 1024, 1024), null, out _));
        ValidationResult r = Check(doc);
        Assert.Single(r.WithCode("terrain.cliff"));
        Assert.Single(r.WithCode("object.terrain"));
    }
}
