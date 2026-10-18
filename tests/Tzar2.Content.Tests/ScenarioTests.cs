using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Xunit;

namespace Tzar2.Content.Tests;

/// <summary>The scenario IR (ADR 0120): expression parser, reference checks, canonical JSON and hash.</summary>
public class ScenarioTests
{
    [Theory]
    [InlineData("1 + 2 * 3", "(1 + (2 * 3))")]
    [InlineData("(1 + 2) * 3", "((1 + 2) * 3)")]
    [InlineData("10 - 4 - 3", "((10 - 4) - 3)")]
    [InlineData("-5 + -(2) - -$v", "((-5 + -2) - neg(var0))")]
    [InlineData("!a || b && c", "(not(ev0) || (ev1 && ev2))")]
    [InlineData("1 < 2 == 3 >= 4", "((1 < 2) == (3 >= 4))")]
    [InlineData("$v = $w = 7 % 3", "(var0 = (var1 = (7 % 3)))")]
    [InlineData("@gold(2) == $stone + 100", "(gold(2) == ($stone + 100))")]
    [InlineData("$v = rnd ($v, 4*$v)", "(var0 = rnd(var0, (4 * var0)))")]
    [InlineData("T1 > 5000", "(timer3 > 5000)")]
    [InlineData("$MaxHealth != $DEFENCE", "($maxhealth != $defense)")]
    [InlineData("(5*3 – 8)/12*7 >= ($v/10 + 5)", "(((((5 * 3) - 8) / 12) * 7) >= ((var0 / 10) + 5))")]
    [InlineData("nosuchthing + $health", "(?nosuchthing + $health)")]
    public void Parser_builds_resolved_trees(string source, string expected)
    {
        var scope = new RecordingScope(["a", "b", "c"], ["", "", "", "t1"]);
        ExpressionDef e = ExpressionParser.Parse(source, scope);
        Assert.Null(e.Error);
        Assert.Equal(expected, Show(e.Root!));
    }

    [Theory]
    [InlineData("1 +")]
    [InlineData("(1")]
    [InlineData("3 = 4")]
    [InlineData("@gold(1, 2)")]
    [InlineData("@frobnicate(1)")]
    [InlineData("1 # 2")]
    [InlineData("99999999999")]
    [InlineData("$")]
    public void Parser_reports_errors_without_throwing(string source)
    {
        ExpressionDef e = ExpressionParser.Parse(source, new RecordingScope([], []));
        Assert.Null(e.Root);
        Assert.NotNull(e.Error);
        Assert.Equal(source, e.Source);
    }

    [Fact]
    public void Parser_registers_user_variables_in_first_use_order()
    {
        var scope = new RecordingScope([], []);
        ExpressionParser.Parse("$b = $A + $b + $a", scope);
        Assert.Equal(["b", "A"], scope.Variables);
    }

    [Fact]
    public void Empty_expression_has_no_tree()
    {
        ExpressionDef e = ExpressionParser.Parse("   ", new RecordingScope([], []));
        Assert.Null(e.Root);
        Assert.Null(e.Error);
    }

    [Fact]
    public void Json_round_trip_is_exact_and_hash_is_stable()
    {
        ScenarioData s = Sample();
        byte[] json = ScenarioJson.Write(s);
        ScenarioData back = ScenarioJson.Read(json);
        Assert.Equal(json, ScenarioJson.Write(back));
        Assert.Equal(s.ContentHash, back.ContentHash);
        Assert.Equal(s.Actions, back.Actions);
        Assert.Equal(s.Events, back.Events);
        Assert.Equal(s.Rules, back.Rules);
        Assert.Equal(s.Conversations, back.Conversations);
        Assert.Contains("Добро пожаловать", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        Assert.DoesNotContain("\r", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
    }

    [Fact]
    public void Hash_changes_with_content()
    {
        ScenarioData a = Sample();
        ScenarioData b = Sample(chat: "other");
        Assert.NotEqual(a.ContentHash, b.ContentHash);
        Assert.Equal(a.ContentHash, Sample().ContentHash);
    }

    [Fact]
    public void Out_of_range_references_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Build(rules: [new RuleDef("r", ExpressionDef.Empty, ImmutableArray.Create(5))]));
        Assert.Throws<ArgumentException>(() => Build(events: [new ObjectExistEvent("e", new NamedObjects(ImmutableArray.Create(3), Quantifier.All))]));
        Assert.Throws<ArgumentException>(() => Build(actions: [new StopTimerAction("s", 0)]));
        Assert.Throws<ArgumentException>(() => Build(actions: [new ConversationAction("c", 0)]));
        var bad = new ExpressionDef("$x", new UserVar(0));
        Assert.Throws<ArgumentException>(() => Build(actions: [new ExpressionAction("x", bad)]));
    }

    [Fact]
    public void Campaign_finds_maps_ignoring_case_and_rejects_unknown_targets()
    {
        CampaignMap one = new("m1", "Map1", "maps/m1.t2map", "first", true);
        CampaignMap two = new("m2", "Map2", "maps/m2.t2map", "second", false);
        var c = new CampaignData("c", "C", "d", ImmutableArray.Create(one, two), ImmutableArray.Create(new CampaignEdge("m1", "MAP2", "m2", CampaignEdgeKind.Win, 0)));
        Assert.Same(two, c.FindMap("map2"));
        Assert.Null(c.FindMap("map3"));
        Assert.Throws<ArgumentException>(() => new CampaignData("c", "C", "d", ImmutableArray.Create(one), ImmutableArray.Create(new CampaignEdge("m1", "x", "zz", CampaignEdgeKind.Win, 0))));
    }

    private static ScenarioData Sample(string chat = "hello")
    {
        var scope = new RecordingScope(["near"], ["", "", "clock"]);
        var named = new NamedObjects(ImmutableArray.Create(0, 1), Quantifier.Any);
        var typed = new TypedObjects("euro.knight", PlayerMasks.Of(2), CountComparison.AtLeast, 3, Quantifier.All);
        EventDef[] events =
        [
            new ObjectInAreaEvent("near", named, new RectArea(1, 2, 3, 4)),
            new ObjectExpressionEvent("weak", typed, ExpressionParser.Parse("$health < 10", scope)),
            new UnknownEvent("odd", 99, ImmutableArray.Create<byte>(1, 2, 3)),
        ];
        ActionDef[] actions =
        [
            new AddQuestAction("Find the gold", "Добро пожаловать", new ResourceRef("user/map.png")),
            new SendChatAction("chat", PlayerMasks.AllPlayers, chat),
            new SetTimerAction("clock", ExpressionParser.Parse("60000", scope), true),
            new StopTimerAction("stop", 2),
            new RemoveQuestAction("done", 0),
            new ScrollViewAction("pan", new ObjectArea(1), new RectArea(0, 0, 5, 5)),
            new PlayerWinAction("win", PlayerMasks.Of(1), "Next"),
            new ConversationAction("talk", 0),
            new KeepRuleAction("keep"),
        ];
        PhraseDef[] phrases =
        [
            new("hi", true, 0, ExpressionDef.Empty, ExpressionParser.Parse("$score = 1", scope), "Hello!", null, null, ImmutableArray.Create(1)),
            new("bye", false, null, ExpressionParser.Parse("$score == 1", scope), ExpressionDef.Empty, "Bye.", new ResourceRef("user/a.png"), null, ImmutableArray<int>.Empty),
        ];
        RuleDef[] rules = [new RuleDef("main", ExpressionParser.Parse("near && clock < 1000", scope), ImmutableArray.Create(0, 1, 8))];
        return new ScenarioData(
            new MapProperties("Title", "Me", "Desc", GameType.Standard, VictoryCondition.DestroyCastle, 200, 2, true, false),
            2,
            ImmutableArray.Create(new ObjectInfo("hero", "The Hero", ObjectFlags.Persistent, Level: 3), ObjectInfo.Plain),
            ImmutableArray.Create(new KingdomDef(1, "K", "T", Civilization.Asian, PlayerType.HumanOrAi, true, 100, 200, 300, 400, "", true, false, true, 5, 6, ImmutableArray.Create("e_tower"), default, default, default, ImmutableArray.Create("te_x"))),
            ImmutableArray.Create(new DiplomacyDef(1, 2, Stance.Ally, true, false, true)),
            scope.Variables.ToImmutableArray(),
            events,
            actions,
            rules,
            ImmutableArray.Create(new ConversationDef("talk", phrases)),
            ImmutableArray.Create(new CustomSoundDef("birds", new ResourceRef("user/birds.wav"), SoundRepeat.Random, 1000, 5000, null, 10, 12)),
            ImmutableArray.Create(new ResourceRef("user/1.mid")));
    }

    private static ScenarioData Build(EventDef[]? events = null, ActionDef[]? actions = null, RuleDef[]? rules = null) => new(
        ScenarioData.Empty(2).Properties,
        2,
        default,
        default,
        default,
        default,
        events ?? [],
        actions ?? [],
        rules ?? [],
        default,
        default,
        default);

    private static string Show(Expr e) => e switch
    {
        IntLiteral i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        UserVar v => "var" + v.VariableIndex,
        ReservedVar r => ExpressionParser.Spelling(r.Variable),
        EventRef r => "ev" + r.EventIndex,
        TimerRef t => "timer" + t.ActionIndex,
        UnknownRef u => "?" + u.Name,
        UnaryExpr u => (u.Op == UnaryOp.Not ? "not(" : "neg(") + Show(u.Operand) + ")",
        BinaryExpr b => "(" + Show(b.Left) + " " + Op(b.Op) + " " + Show(b.Right) + ")",
        AssignExpr a => "(" + Show(a.Target) + " = " + Show(a.Value) + ")",
        CallExpr c => c.Function.ToString().ToLowerInvariant() + "(" + string.Join(", ", System.Linq.Enumerable.Select(c.Arguments, Show)) + ")",
        _ => throw new ArgumentException(e.GetType().Name),
    };

    private static string Op(BinaryOp op) => op switch
    {
        BinaryOp.Add => "+",
        BinaryOp.Subtract => "-",
        BinaryOp.Multiply => "*",
        BinaryOp.Divide => "/",
        BinaryOp.Modulo => "%",
        BinaryOp.Less => "<",
        BinaryOp.LessOrEqual => "<=",
        BinaryOp.Greater => ">",
        BinaryOp.GreaterOrEqual => ">=",
        BinaryOp.Equal => "==",
        BinaryOp.NotEqual => "!=",
        BinaryOp.And => "&&",
        _ => "||",
    };

    private sealed class RecordingScope(string[] events, string[] timers) : IExpressionScope
    {
        public List<string> Variables { get; } = [];

        public int UserVariable(string name)
        {
            int i = Variables.FindIndex(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                return i;
            }

            Variables.Add(name);
            return Variables.Count - 1;
        }

        public int Event(string name) => Array.FindIndex(events, e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));

        public int Timer(string name) => Array.FindIndex(timers, t => t.Length > 0 && string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
    }
}
