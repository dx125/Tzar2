using System.Linq;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
using Tzar2.Scripting.Legacy;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Components;
using Tzar2.Sim.Events;
using Tzar2.Sim.Kingdoms;
using Tzar2.Sim.Systems;
using Xunit;
using Tzar2.Tests.Scenario;
using static Tzar2.Tests.Scenario.ScenarioBuilder;
using SimStance = Tzar2.Sim.Kingdoms.Stance;

namespace Tzar2.Scripting.Tests;

/// <summary>The 27 actions of Appendix C.7, each on a synthetic scenario (invented names, texts and values).</summary>
public class ActionTests
{
    private static int Tiles(int t) => (t * Tile) + (Tile / 2);

    // A scenario whose single rule runs the given actions once in the first tick.
    private static TriggerDriver Once(ScenarioBuilder b, params ActionDef[] actions)
    {
        b.Rule("1", actions.Select(b.Action).ToArray());
        TriggerDriver d = b.Start(players: 3);
        d.Step();
        return d;
    }

    [Fact]
    public void Add_and_remove_quest()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        int add = b.Action(new AddQuestAction("Find the well", "Walk to the old well.", null));
        int other = b.Action(new AddQuestAction("Second", "Another invented task.", null));
        b.Rule("1", add, other, add);
        b.Rule("$done == 1", b.Action(new RemoveQuestAction("done", add)));
        b.Rule("1", b.Action(new ExpressionAction("arm", b.Expr("$done = 1"))));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal([add, other], d.State.Quests);
        Assert.Equal(2, d.Count(SimEventKind.QuestAdded)); // a quest is added once
        d.Seconds(1);
        Assert.Equal([other], d.State.Quests);
        Assert.Equal(add, d.Of(SimEventKind.QuestRemoved).Single().Arg);
    }

    [Fact]
    public void Appear_object_brings_a_hidden_object_with_its_properties()
    {
        var b = new ScenarioBuilder();
        int hero = b.Unit("euro.footman", 0, 6, 6, new ObjectInfo("Brannoc", "", ObjectFlags.NotAppear, Health: 33));
        int house = b.Structure("euro.house", 0, 10, 10, new ObjectInfo("", "", ObjectFlags.NotAppear));
        b.Rule("$go", b.Action(new AppearObjectAction("show", All(hero))), b.Action(new AppearObjectAction("build", Typed("euro.house", 1, CountComparison.AtLeast, 1))));
        b.Rule("1", b.Action(new ExpressionAction("arm", b.Expr("$go = 1"))));
        using TriggerDriver d = b.Start();
        Assert.DoesNotContain(d.World.Identities.Entities.ToArray(), e => d.World.TypeOf(e).Key == "euro.footman");
        d.Step();
        Assert.True(d.State.Hidden[hero]);
        d.Seconds(1);
        EntityId e = d.Object(hero);
        Assert.True(d.World.Entities.IsAlive(e));
        Assert.Equal("Brannoc", d.World.Names.Get(e).Value);
        Assert.Equal(33, d.World.Healths.Get(e).Current);
        Assert.Equal(new Position(Tiles(6), Tiles(6)), d.World.Positions.Get(e));
        Assert.True(d.World.Entities.IsAlive(d.Object(house)));
        Assert.False(d.State.Hidden[house]);
    }

    [Fact]
    public void Change_diplomacy_sets_both_directions_and_the_sharing()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        using TriggerDriver d = Once(b, new ChangeDiplomacyAction("peace", 1, 3, Content.Compiled.Scenario.Stance.Ally, SharedVision: true, SharedResources: false, CommandUnits: true));
        Assert.Equal(SimStance.Ally, d.World.Diplomacy.StanceOf(0, 2));
        Assert.Equal(SimStance.Ally, d.World.Diplomacy.StanceOf(2, 0));
        Assert.Equal(Sharing.Vision | Sharing.UnitControl, d.World.Diplomacy.OffersOf(2, 0));
        Assert.True(d.World.Diplomacy.Shares(0, 2, Sharing.Vision));
    }

    [Fact]
    public void Change_object_owner_and_change_player_status()
    {
        var b = new ScenarioBuilder();
        int peon = b.Unit("euro.peon", 1, 3, 3);
        b.Unit("euro.peon", 0, 30, 3);
        using TriggerDriver d = Once(b, new ChangeObjectOwnerAction("convert", All(peon), 1), new ChangePlayerStatusAction("immortal", 2, CannotWin: true, CannotLose: true), new ChangeObjectOwnerAction("bad", All(peon), 9));
        Assert.Equal(0, d.World.Identities.Get(d.Object(peon)).Owner);
        Assert.True(d.Triggers.CannotLose(1));
        Assert.True(d.Triggers.CannotWin(1));
        Assert.False(d.Triggers.CannotLose(0));
        Assert.Contains(d.Of(SimEventKind.ScenarioFault), e => e.Arg == (int)ScenarioFault.ActionFailed);
    }

    [Fact]
    public void Destroy_kills_and_erase_removes_without_a_corpse()
    {
        var b = new ScenarioBuilder();
        int a = b.Unit("euro.footman", 1, 3, 3);
        int c = b.Unit("euro.footman", 1, 6, 3);
        using TriggerDriver d = Once(b, new DestroyObjectAction("slay", All(a)), new EraseObjectAction("vanish", All(c)));
        Assert.False(d.World.Entities.IsAlive(d.Object(a)));
        Assert.False(d.World.Entities.IsAlive(d.Object(c)));
        Assert.Equal(1, d.World.Decals.Count);
    }

    [Fact]
    public void Explore_area_reveals_it_for_a_while()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 2, 2);
        using TriggerDriver d = Once(b, new ExploreAreaAction("scout", 2000, true, Rect(36, 20, 42, 26)));
        d.Step();
        Assert.True(d.World.Visibility.IsVisible(0, 39, 23));
        d.Seconds(3);
        Assert.False(d.World.Visibility.IsVisible(0, 39, 23));
        Assert.True(d.World.Visibility.IsExplored(0, 39, 23));
    }

    [Fact]
    public void Load_map_player_win_and_player_lose_request_the_next_map()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 2, 2);
        b.Unit("euro.peon", 1, 20, 2);
        b.Unit("euro.peon", 2, 40, 2);
        using (TriggerDriver d = Once(b, new LoadMapAction("next", "Valley")))
        {
            Assert.Equal(0, d.State.MapRequestAction);
            Assert.Equal(0, d.Of(SimEventKind.MapRequested).Single().Y);
        }

        var w = new ScenarioBuilder();
        w.Unit("euro.peon", 0, 2, 2);
        w.Unit("euro.peon", 1, 20, 2);
        w.Unit("euro.peon", 2, 40, 2);
        using (TriggerDriver d = Once(w, new PlayerLoseAction("lose3", PlayerMasks.Of(3), null), new PlayerWinAction("win1", PlayerMasks.Of(1), "Valley")))
        {
            Assert.True((d.World.Players[0].Flags & PlayerFlags.Victorious) != 0);
            Assert.True((d.World.Players[1].Flags & PlayerFlags.Defeated) != 0);
            Assert.True((d.World.Players[2].Flags & PlayerFlags.Defeated) != 0);
            Assert.Equal(1, d.State.MapRequestAction);
            Assert.Equal(1, d.Of(SimEventKind.MapRequested).Single().Y);
        }
    }

    [Fact]
    public void View_actions_emit_presentation_events_only()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 2, 2);
        using TriggerDriver d = Once(b, new LockViewAction("hold", 3000, Rect(10, 10, 12, 12)), new ScrollViewAction("pan", Rect(0, 0, 0, 0), Rect(20, 20, 20, 20)), new UnlockViewAction("free"));
        SimEvent locked = d.Of(SimEventKind.ViewLocked).Single();
        Assert.Equal((Tiles(11), Tiles(11), 3000), (locked.X, locked.Y, locked.Arg));
        SimEvent scrolled = d.Of(SimEventKind.ViewScrolled).Single();
        Assert.Equal((Tiles(20), Tiles(20)), (scrolled.X, scrolled.Y));
        Assert.Single(d.Of(SimEventKind.ViewUnlocked));
    }

    [Fact]
    public void Lock_units_refuses_player_commands_until_unlocked()
    {
        var b = new ScenarioBuilder();
        int peon = b.Unit("euro.peon", 0, 3, 3);
        b.Rule("1", b.Action(new LockUnitsAction("freeze", All(peon), true)));
        b.Rule("$free", b.Action(new LockUnitsAction("thaw", All(peon), false)));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.True(d.Triggers.IsLocked(d.Object(peon)));
        d.Order(0, CommandKind.Move, [d.Object(peon)], Tiles(20), Tiles(3));
        d.Step();
        Assert.Equal(MotionState.Idle, d.World.Motions.Get(d.Object(peon)).State);
        d.State.Variables[0] = 1; // $free
        d.Seconds(1);
        Assert.False(d.Triggers.IsLocked(d.Object(peon)));
        d.Order(0, CommandKind.Move, [d.Object(peon)], Tiles(20), Tiles(3));
        d.Step();
        Assert.Equal(MotionState.Moving, d.World.Motions.Get(d.Object(peon)).State);
    }

    [Fact]
    public void Object_order_attack_on_an_object_stages_a_fight_between_allies()
    {
        var b = new ScenarioBuilder();
        int hero = b.Unit("euro.footman", 0, 10, 8);
        int rival = b.Unit("euro.footman", 1, 12, 8);
        using TriggerDriver d = Once(
            b,
            new ChangeDiplomacyAction("friends", 1, 2, Content.Compiled.Scenario.Stance.Ally, SharedVision: false, SharedResources: false, CommandUnits: false),
            new ObjectOrderAction("duel", All(hero), new ObjectArea(rival, RadiusTiles: 0), "attack"));
        Assert.Equal(SimStance.Ally, d.World.Diplomacy.StanceOf(0, 1));
        Assert.Equal(CombatMode.Scripted, d.World.Combats.Get(d.Object(hero)).Mode);
        d.Seconds(5);
        Assert.Contains(d.Of(SimEventKind.Damaged), e => e.Entity == d.Object(rival));
    }

    [Fact]
    public void Object_order_moves_units_of_any_kingdom_even_when_locked()
    {
        var b = new ScenarioBuilder();
        int mine = b.Unit("euro.footman", 0, 3, 3);
        int theirs = b.Unit("euro.footman", 1, 3, 8);
        using TriggerDriver d = Once(b, new LockUnitsAction("freeze", All(mine), true), new ObjectOrderAction("march", Any(mine, theirs), Rect(30, 5, 32, 7), "move"));
        d.Seconds(20); // 27 tiles at SPEED 5 x 10 px/s (ADR 0171)
        Assert.InRange(d.World.Positions.Get(d.Object(mine)).X / Tile, 28, 34);
        Assert.InRange(d.World.Positions.Get(d.Object(theirs)).X / Tile, 28, 34);
    }

    [Fact]
    public void Chat_message_story_screen_and_show_message_name_their_action()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        using TriggerDriver d = Once(
            b,
            new SendChatAction("hail", (ushort)(PlayerMasks.Of(1) | PlayerMasks.Of(2)), "Invented greeting."),
            new ShowMessageAction("note", "A title", "An invented note.", null),
            new ShowStoryScreenAction("story", 20000, true, "Once upon an invented time.", null, null, null));
        Assert.Equal((0b11, 0), (d.Of(SimEventKind.ScenarioChat).Single().X, d.Of(SimEventKind.ScenarioChat).Single().Arg));
        Assert.Equal(1, d.Of(SimEventKind.ScenarioMessage).Single().Arg);
        Assert.Equal(2, d.Of(SimEventKind.StoryScreen).Single().Arg);
    }

    [Fact]
    public void Set_object_properties_writes_reserved_variables_of_each_object()
    {
        var b = new ScenarioBuilder();
        int a = b.Unit("euro.footman", 0, 3, 3);
        int c = b.Unit("euro.footman", 0, 5, 3);
        int doomed = b.Unit("euro.peon", 0, 7, 3);
        using TriggerDriver d = Once(
            b,
            new SetObjectPropertiesAction("buff", Any(a, c), b.Expr("$attack = $attack + 5")),
            new SetObjectPropertiesAction("hurt", All(a), b.Expr("$health = $maxhealth / 2")),
            new SetObjectPropertiesAction("traitor", All(c), b.Expr("$kingdom = 2")),
            new SetObjectPropertiesAction("rich", All(a), b.Expr("$gold = 1234")),
            new SetObjectPropertiesAction("move", All(a), b.Expr("$x = 9")),
            new SetObjectPropertiesAction("kill", All(doomed), b.Expr("$health = 0")));
        EntityId fa = d.Object(a);
        int baseAttack = GameplayQueries.AttackOf(d.World, d.Object(c));
        Assert.Equal(baseAttack, GameplayQueries.AttackOf(d.World, fa));
        Assert.Equal(5, d.World.Experiences.Get(fa).BonusAttack);
        Assert.Equal(d.World.Healths.Get(fa).Max / 2, d.World.Healths.Get(fa).Current);
        Assert.Equal(1, d.World.Identities.Get(d.Object(c)).Owner);
        Assert.Equal(1234, d.World.Players[0].Resources[d.World.Ruleset.ResourceId("gold")]);
        Assert.False(d.World.Entities.IsAlive(d.Object(doomed)));
        Assert.Contains(d.Of(SimEventKind.ScenarioFault), e => e.Arg == (int)ScenarioFault.ReadOnlyVariable);
    }

    [Fact]
    public void Spawn_object_places_units_on_free_tiles_of_the_area()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        using TriggerDriver d = Once(b, new SpawnObjectAction("army", "euro.footman", 2, 5, Rect(20, 10, 22, 12)), new SpawnObjectAction("bad", "no.such", 1, 1, Rect(1, 1, 1, 1)));
        EntityId[] spawned = [.. d.World.Identities.Entities.ToArray().Where(e => d.World.TypeOf(e).Key == "euro.footman")];
        Assert.Equal(5, spawned.Length);
        Assert.All(spawned, e => Assert.Equal(1, d.World.Identities.Get(e).Owner));
        Assert.All(spawned, e => Assert.InRange(d.World.Positions.Get(e).X / Tile, 20, 22));
        Assert.Equal(5, spawned.Select(e => d.World.Positions.Get(e)).Distinct().Count());
        Assert.Contains(d.Of(SimEventKind.ScenarioFault), e => e.Arg == (int)ScenarioFault.ActionFailed);
    }

    [Fact]
    public void Unknown_actions_are_skipped_with_a_fault_and_the_rule_goes_on()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        using TriggerDriver d = Once(b, new UnknownAction("mystery", 77, new byte[] { 9 }), new ExpressionAction("after", b.Expr("$after = 1")));
        Assert.Equal(1, d.Variable("after"));
        SimEvent fault = d.Of(SimEventKind.ScenarioFault).Single();
        Assert.Equal((0, 0, (int)ScenarioFault.UnknownAction), (fault.X, fault.Y, fault.Arg));
    }
}
