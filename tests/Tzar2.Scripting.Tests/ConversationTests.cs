using System.Linq;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Scripting.Legacy;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Xunit;
using static Tzar2.Scripting.Tests.ScenarioBuilder;

namespace Tzar2.Scripting.Tests;

/// <summary>Conversations (Appendix C.8; ADR 0131): offers, choices, conditions, results, the loop guard and the queue.</summary>
public class ConversationTests
{
    // Phrases (invented): 0 "greet" (initial), 1 "ask" (initial, only when $friend), 2 "reply" with result $told = 1,
    // 3 "bye"; greet → reply | bye, ask → bye.
    private static ScenarioBuilder Talk(out int conversation)
    {
        var b = new ScenarioBuilder();
        int elder = b.Unit("euro.peon", 1, 5, 5, new ObjectInfo("Elder", "", ObjectFlags.None));
        b.Unit("euro.footman", 0, 6, 5);
        conversation = b.Conversation(
            Phrase(b, "greet", true, "Welcome, traveller.", source: elder, followUps: [2, 3]),
            Phrase(b, "ask", true, "Do you know me?", condition: "$friend == 1", followUps: [3]),
            Phrase(b, "reply", false, "The bridge is out.", result: "$told = $told + 1", source: elder),
            Phrase(b, "bye", false, "Farewell."));
        return b;
    }

    [Fact]
    public void A_conversation_offers_its_initial_phrases_and_follows_the_choices()
    {
        ScenarioBuilder b = Talk(out int c);
        b.Rule("1", b.Action(new ConversationAction("talk", c)));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(c, d.State.Conversation);
        Assert.Equal([0], d.State.Candidates); // "ask" needs $friend
        SimEvent offered = d.Of(SimEventKind.ConversationOffered).Single();
        Assert.Equal((c, d.State.ConversationSerial, 1), (offered.X, offered.Y, offered.Arg));

        d.Reply(0, 0);
        d.Step();
        Assert.Equal(0, d.Of(SimEventKind.PhraseSaid).Single().Y);
        Assert.Equal([2, 3], d.State.Candidates);

        d.Reply(0, 0); // "reply": its result runs
        d.Step();
        Assert.Equal(1, d.Variable("told"));
        Assert.Equal(TriggerState.NoConversation, d.State.Conversation); // no follow-ups: the end
        Assert.Single(d.Of(SimEventKind.ConversationEnded));
    }

    [Fact]
    public void Bad_replies_are_refused()
    {
        ScenarioBuilder b = Talk(out int c);
        b.Rule("1", b.Action(new ConversationAction("talk", c)));
        using TriggerDriver d = b.Start(options: TriggerOptions.Default with { ResponderMask = 1 });
        d.Step();
        int serial = d.State.ConversationSerial;
        d.Order(0, CommandKind.ScenarioReply, [], arg0: serial + 1, arg1: 0); // stale session
        d.Order(0, CommandKind.ScenarioReply, [], arg0: serial, arg1: 5); // no such candidate
        d.Order(1, CommandKind.ScenarioReply, [], arg0: serial, arg1: 0); // not a responder
        d.Step();
        Assert.Equal(3, d.Of(SimEventKind.CommandRejected).Count(e => e.Arg == (int)CommandRejection.NoScenarioPrompt));
        Assert.Equal([0], d.State.Candidates);

        d.Order(0, CommandKind.ScenarioReply, [], arg0: serial, arg1: 0);
        d.Order(0, CommandKind.ScenarioReply, [], arg0: serial, arg1: 0); // one reply per tick
        d.Step();
        Assert.Single(d.Of(SimEventKind.PhraseSaid));
        Assert.Single(d.Of(SimEventKind.CommandRejected).Where(e => e.Arg == (int)CommandRejection.NoScenarioPrompt).Skip(3));
    }

    [Fact]
    public void Phrase_conditions_are_evaluated_when_offered_and_a_second_conversation_waits()
    {
        ScenarioBuilder b = Talk(out int c);
        int other = b.Conversation(Phrase(b, "aside", true, "Psst."));
        b.Rule("1", b.Action(new ExpressionAction("befriend", b.Expr("$friend = 1"))), b.Action(new ConversationAction("talk", c)), b.Action(new ConversationAction("later", other)));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal([0, 1], d.State.Candidates);
        Assert.Equal([other], d.State.QueuedConversations);
        d.Reply(0, 1); // "ask" → "bye"
        d.Step();
        d.Reply(0, 0);
        d.Step();
        Assert.Equal(other, d.State.Conversation);
        Assert.Empty(d.State.QueuedConversations);
    }

    [Fact]
    public void A_looping_conversation_is_cut_by_the_step_guard()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        int c = b.Conversation(Phrase(b, "again", true, "Round and round.", result: "$n = $n + 1", followUps: [0]));
        b.Rule("1", b.Action(new ConversationAction("loop", c)));
        using TriggerDriver d = b.Start(options: TriggerOptions.Default with { MaxConversationSteps = 5 });
        d.Step();
        for (int i = 0; i < 10; i++)
        {
            if (d.State.Conversation >= 0)
            {
                d.Reply(0, 0);
            }

            d.Step();
        }

        Assert.Equal(5, d.Variable("n"));
        Assert.Equal(TriggerState.NoConversation, d.State.Conversation);
        Assert.Equal(1, d.Of(SimEventKind.ConversationEnded).Single().Arg);
        Assert.Contains(d.Of(SimEventKind.ScenarioFault), e => e.Arg == (int)ScenarioFault.ConversationGuard);
    }

    [Fact]
    public void A_conversation_without_offerable_phrases_ends_at_once()
    {
        var b = new ScenarioBuilder();
        b.Unit("euro.peon", 0, 3, 3);
        int c = b.Conversation(Phrase(b, "never", true, "Hidden.", condition: "0"));
        b.Rule("1", b.Action(new ConversationAction("talk", c)));
        using TriggerDriver d = b.Start();
        d.Step();
        Assert.Equal(TriggerState.NoConversation, d.State.Conversation);
        Assert.Single(d.Of(SimEventKind.ConversationEnded));
        Assert.Empty(d.Of(SimEventKind.ConversationOffered));
    }
}
