using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Game.Scenario;
using Tzar2.Game.Session;
using Tzar2.Tests.Scenario;
using Xunit;
using static Tzar2.Tests.Scenario.ScenarioBuilder;

namespace Tzar2.Game.Tests;

/// <summary>Scenario presentation in a game session (ADR 0131, ADR 0132): notices, quest log, conversations, the pause.</summary>
public sealed class ScenarioSessionTests
{
    internal static (MatchSetup Setup, ScenarioBuilder Builder) Mission()
    {
        var b = new ScenarioBuilder();
        int elder = b.Unit("euro.peon", 1, 20, 5, new ObjectInfo("elder", "Old Miller", ObjectFlags.None));
        b.Unit("euro.footman", 0, 4, 4);
        b.Unit("euro.peon", 0, 5, 4);
        int talk = b.Conversation(
            Phrase(b, "q", true, "Will you help the mill?", source: elder, followUps: [1, 2]),
            Phrase(b, "yes", false, "Gladly.", result: "$help = 1"),
            Phrase(b, "no", false, "Not today."));
        b.Rule(
            "1",
            b.Action(new ShowStoryScreenAction("Prologue", 12000, true, "Snow fell on the valley. (invented)", new ResourceRef("user/valley.png"), null, new ResourceRef("user/theme.mid"))),
            b.Action(new AddQuestAction("Visit the miller", "Find the old miller by the river.", null)),
            b.Action(new ConversationAction("ask", talk)));
        b.Rule("$help == 1", b.Action(new ShowMessageAction("thanks", "Miller", "Thank you, friend.", null)), b.Action(new SendChatAction("chat", PlayerMasks.Of(1), "Invented chat line.")), b.Action(new LockViewAction("look", 2000, Rect(19, 4, 21, 6))));
        var setup = new MatchSetup
        {
            Map = new FixedMapSource("synthetic", b.Map()),
            Players = [new PlayerSetup(0, "euro", ControllerKind.Human), new PlayerSetup(1, "euro", ControllerKind.Computer)],
            Scenario = b.Scenario(),
            ComputerAi = false,
            Victory = Sim.VictoryCondition.NotSpecified,
        };
        return (setup, b);
    }

    [Fact]
    public void Notices_carry_the_scenario_texts_and_the_view_shows_quests_and_choices()
    {
        (MatchSetup setup, _) = Mission();
        using var session = new GameSession(setup, Classic);
        Assert.NotNull(session.Scenario);
        session.Step(1);
        var notices = new List<ScenarioNotice>();
        session.DrainNotices(notices);
        ScenarioNotice story = notices.Single(n => n.Kind == ScenarioNoticeKind.StoryScreen);
        Assert.Equal(("Snow fell on the valley. (invented)", "user/valley.png", "user/theme.mid", 12000, true, true), (story.Text, story.Image, story.Music, story.DurationMs, story.AutoClose, story.ForLocalPlayer));
        Assert.Equal("Visit the miller", notices.Single(n => n.Kind == ScenarioNoticeKind.QuestAdded).Title);

        GameFrame frame = session.AcquireFrame(out _)!;
        Assert.Equal("Find the old miller by the river.", frame.Scenario!.Quests.Single().Text);
        ConversationView talk = frame.Scenario.Conversation!;
        Assert.Equal(("Old Miller", "Will you help the mill?"), (talk.Choices.Single().Speaker, talk.Choices.Single().Text));
        Assert.True(session.HoldsForScenario);

        session.ReplyToConversation(talk.Serial, 0);
        Assert.False(session.HoldsForScenario); // the reply's tick may run
        session.Step(1);
        talk = session.AcquireFrame(out _)!.Scenario!.Conversation!;
        Assert.Equal(["Gladly.", "Not today."], talk.Choices.Select(c => c.Text));
        session.ReplyToConversation(talk.Serial, 0);
        session.Step(21);
        Assert.Null(session.AcquireFrame(out _)!.Scenario!.Conversation);
        Assert.False(session.HoldsForScenario);
        notices.Clear();
        session.DrainNotices(notices);
        Assert.Contains(notices, n => n.Kind == ScenarioNoticeKind.PhraseSaid && n.Text == "Gladly." && n.Title == "");
        Assert.Contains(notices, n => n.Kind == ScenarioNoticeKind.Message && n.Title == "Miller" && n.Text == "Thank you, friend.");
        Assert.Contains(notices, n => n.Kind == ScenarioNoticeKind.Chat && n.Text == "Invented chat line." && n.ForLocalPlayer);
        ScenarioNotice look = notices.Single(n => n.Kind == ScenarioNoticeKind.LockView);
        Assert.Equal((20 * Tile + Tile / 2, 5 * Tile + Tile / 2, 2000), (look.X, look.Y, look.DurationMs));
    }

    [Fact]
    public void A_scenario_session_restores_from_its_snapshot_with_the_trigger_state()
    {
        (MatchSetup setup, _) = Mission();
        using var session = new GameSession(setup, Classic);
        session.Step(1);
        byte[] snapshot = session.CaptureSnapshot();
        using GameSession restored = GameSession.Restore(setup, Classic, snapshot);
        Assert.Equal(session.Simulation.ComputeFullHash(), restored.Simulation.ComputeFullHash());
        Assert.NotNull(restored.AcquireFrame(out _)!.Scenario!.Conversation);
        Assert.Single(restored.AcquireFrame(out _)!.Scenario!.Quests);
    }

    [Fact]
    public void The_scenario_is_part_of_the_match_fingerprint()
    {
        (MatchSetup setup, ScenarioBuilder b) = Mission();
        MatchFingerprint with = MatchFingerprint.Of(setup, Classic, b.Map());
        MatchFingerprint without = MatchFingerprint.Of(setup with { Scenario = null }, Classic, b.Map());
        Assert.NotEqual(with.SettingsHash, without.SettingsHash);
        Assert.Throws<MatchMismatchException>(() => with.EnsureCompatible(without));
    }
}
