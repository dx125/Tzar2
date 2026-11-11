using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>Where a name or an object is used in the triggers (ADR 0290).</summary>
public class TriggerUseTests
{
    private const int Tile = MapData.UnitsPerTile;

    private static (MapDocument Doc, int Hero) Map()
    {
        MapDocument doc = MapDocument.CreateEmpty("Uses", 32, 20, Fixtures.RulesFor(null));
        int hero = 0;
        doc.Apply("Objects", s => s.Add(new MapObject("euro.footman", 0, (4 * Tile) + 1024, (10 * Tile) + 1024), new ObjectInfo("Hero", "", ObjectFlags.None), out hero));
        doc.Apply("Goals", s => s.WithScenario(m =>
        {
            m = MissionGoals.Add(m, new MissionGoal(GoalKind.HoldOut, "Hold on.", Minutes: 2));
            m = MissionGoals.Add(m, new MissionGoal(GoalKind.KeepAlive, "Keep him alive.", ObjectId: hero));
            return m.AddAction(new ExpressionAction("Count", ScenarioModel.Source("$Waves = $waves + 1")), out _)
                .AddAction(new StopTimerAction("Stop it", m.Actions.Items.ToList().FindIndex(a => a.Name == "Goal1Timer")), out _);
        }));
        return (doc, hero);
    }

    [Fact]
    public void A_timer_is_found_where_it_is_read_set_and_stopped()
    {
        (MapDocument doc, _) = Map();
        string[] uses = [.. TriggerUses.OfName(doc.State.Scenario, NameKind.Timer, "goal1timer").Select(u => u.Text)];
        Assert.Contains("rule 'Goal 1 done': condition", uses);
        Assert.Contains("action 'Stop it': stops it", uses);
        Assert.DoesNotContain(TriggerUses.OfName(doc.State.Scenario, NameKind.Variable, "Goal1Timer"), _ => true); // not as a variable
    }

    [Fact]
    public void A_variable_is_found_in_every_spelling_and_an_event_by_its_name()
    {
        (MapDocument doc, _) = Map();
        TriggerUse use = Assert.Single(TriggerUses.OfName(doc.State.Scenario, NameKind.Variable, "$waves"));
        Assert.Equal(LocationKind.Action, use.At.Kind);
        Assert.Equal("action 'Count': expression", use.Text);
        Assert.Contains(TriggerUses.OfName(doc.State.Scenario, NameKind.Event, "Goal2Lost"), u => u.Text == "rule 'Goal 2 fails': condition");
    }

    [Fact]
    public void An_object_is_found_where_the_triggers_refer_to_it()
    {
        (MapDocument doc, int hero) = Map();
        TriggerUse use = Assert.Single(TriggerUses.OfObject(doc.State.Scenario, hero));
        Assert.Equal((LocationKind.Event, "event 'Goal2Lost'"), (use.At.Kind, use.Text));
        Assert.Empty(TriggerUses.OfObject(doc.State.Scenario, hero + 100));
    }
}
