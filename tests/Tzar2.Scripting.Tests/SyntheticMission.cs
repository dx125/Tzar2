using Tzar2.Content.Compiled.Scenario;
using Tzar2.Sim.Commands;
using Tzar2.Tests.Scenario;
using static Tzar2.Tests.Scenario.ScenarioBuilder;

namespace Tzar2.Scripting.Tests;

/// <summary>
/// A trigger-heavy synthetic mission (invented layout, names and texts; REQ-LEGAL-03): a briefing story screen and a
/// conversation with a choice, quests added and removed, a countdown timer that spawns enemy waves that march on the
/// player, an escort hidden until a gate area is reached, typed death counters, locked units, a kept rule that changes
/// properties every pass, diplomacy, and victory by trigger when the enemy castle falls (defeat when the player's hero
/// dies). The headless driver answers the conversation and plays a fixed command script.
/// </summary>
internal static class SyntheticMission
{
    public static (ScenarioBuilder Builder, int Hero, int EnemyCastle) Build()
    {
        var b = new ScenarioBuilder { Width = 64, Height = 40 };
        int hero = b.Unit("euro.footman", 0, 6, 20, new ObjectInfo("Vaskor", "Captain", ObjectFlags.Persistent, Level: 2));
        for (int i = 0; i < 12; i++)
        {
            b.Unit("euro.footman", 0, 4 + (i % 4), 22 + (i / 4));
        }

        b.Unit("euro.peon", 0, 4, 16);
        int escort = b.Unit("euro.footman", 0, 30, 20, new ObjectInfo("Ilsa", "", ObjectFlags.NotAppear));
        int castle = b.Structure("euro.castle", 1, 54, 17, new ObjectInfo("Keep", "", ObjectFlags.None, MaxHealth: 400));
        for (int i = 0; i < 2; i++)
        {
            b.Unit("euro.footman", 1, 50, 15 + i);
        }

        b.Unit("euro.peon", 2, 40, 35);

        b.Event(new ObjectInAreaEvent("AtGate", Any(hero), Rect(28, 17, 32, 23)));
        b.Event(new ObjectDestroyedEvent("CastleDown", All(castle)));
        b.Event(new ObjectDestroyedEvent("HeroDead", All(hero)));
        b.Event(new ObjectDestroyedEvent("Raiders", Typed("euro.footman", 2, CountComparison.AtLeast, 3)));
        b.Event(new ObjectExistEvent("EscortHere", All(escort)));
        b.Event(new ObjectExpressionEvent("Hurt", Typed("euro.footman", 1, CountComparison.AtLeast, 1), b.Expr("$health < $maxhealth")));

        int story = b.Action(new ShowStoryScreenAction("Briefing", 15000, true, "The pass must be held. (invented)", null, null, null));
        int quest1 = b.Action(new AddQuestAction("Reach the gate", "Lead the captain to the old gate.", null));
        int quest2 = b.Action(new AddQuestAction("Raze the keep", "Destroy the keep in the east.", null));
        int talk = b.Conversation(
            Phrase(b, "hello", true, "Captain, which road?", followUps: [1, 2]),
            Phrase(b, "north", false, "The north road.", result: "$road = 1"),
            Phrase(b, "south", false, "The south road.", result: "$road = 2"));
        int wave = b.Action(new SetTimerAction("WaveTimer", b.Expr("9000"), CountDown: true));

        b.Rule("1", story, quest1, b.Action(new ConversationAction("Ask", talk)), wave, b.Action(new LockUnitsAction("hold", Typed("euro.peon", 1, CountComparison.AtLeast, 1), true)));
        b.Rule("$road != 0", b.Action(new ChangeDiplomacyAction("truce", 1, 3, Stance.Neutral, false, false, false)), b.Action(new SendChatAction("chat", PlayerMasks.Of(1), "Scouts report movement.")));
        b.Rule(
            "WaveTimer == 0 && $waves < 3",
            b.Action(new KeepRuleAction("again")),
            b.Action(new SpawnObjectAction("wave", "euro.footman", 2, 2, Rect(48, 26, 52, 30))),
            b.Action(new ObjectOrderAction("attack", Typed("euro.footman", 2, CountComparison.AtLeast, 1), Rect(6, 20, 8, 22), "attack")),
            b.Action(new ExpressionAction("count", b.Expr("$waves = $waves + 1"))),
            b.Action(new SetTimerAction("WaveTimer2", b.Expr("0"), CountDown: false)),
            wave);
        b.Rule("AtGate", b.Action(new AppearObjectAction("escort", All(escort))), b.Action(new RemoveQuestAction("gate done", quest1)), quest2, b.Action(new ScrollViewAction("look", Rect(28, 17, 32, 23), Rect(54, 17, 58, 21))));
        b.Rule("Hurt", b.Action(new KeepRuleAction("keep")), b.Action(new SetObjectPropertiesAction("heal", Typed("euro.footman", 2, CountComparison.AtLeast, 1), b.Expr("$health = $health + 1"))));
        b.Rule("Raiders", b.Action(new ShowMessageAction("raid", "Raiders", "Three raiders fell. (invented)", null)), b.Action(new ExploreAreaAction("scout", 5000, true, Rect(50, 14, 58, 22))));
        b.Rule("CastleDown", b.Action(new PlayerWinAction("win", PlayerMasks.Of(1), "next-map")));
        b.Rule("$escortSeen && !EscortHere", b.Action(new ShowMessageAction("mourn", "Loss", "The escort fell. (invented)", null)));
        b.Rule("EscortHere", b.Action(new ExpressionAction("seen", b.Expr("$escortSeen = 1"))));
        b.Rule("HeroDead", b.Action(new PlayerLoseAction("lose", PlayerMasks.Of(1), "retry-map")));
        return (b, hero, castle);
    }

    /// <summary>
    /// The scripted player: answers the conversation with "south" when asked, marches the captain to the gate, and
    /// sends the rest of the army against the castle. Called before each tick.
    /// </summary>
    public static void Play(TriggerDriver d, int hero, int castle)
    {
        int tick = d.World.Tick.Value;
        if (d.State.Conversation >= 0 && d.State.PendingReply < 0 && tick % 7 == 3)
        {
            d.Reply(0, d.State.Candidates.Count > 1 ? 1 : 0);
        }

        if (tick == 40)
        {
            d.Order(0, CommandKind.Move, [d.Object(hero)], (30 * Tile) + 1024, (20 * Tile) + 1024);
        }

        if (d.State.EventValues[0] && d.State.Quests.Count == 1 && d.World.Motions.Get(d.Object(hero)).OrderY > 10 * Tile)
        {
            d.Order(0, CommandKind.Move, [d.Object(hero)], (8 * Tile) + 1024, (4 * Tile) + 1024); // back to safety
        }

        if (tick == 400 || tick == 900)
        {
            var army = new System.Collections.Generic.List<Core.Ids.EntityId>();
            foreach (Core.Ids.EntityId e in d.World.Identities.Entities)
            {
                if (d.World.Identities.Get(e).Owner == 0 && d.World.TypeOf(e).Key == "euro.footman" && e != d.Object(hero))
                {
                    army.Add(e);
                }
            }

            d.Order(0, CommandKind.Attack, army.ToArray(), target: d.Object(castle));
        }
    }
}
