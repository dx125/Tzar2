using System.Linq;
using Tzar2.AI.Legacy.Syntax;
using Xunit;

namespace Tzar2.AI.Tests;

/// <summary>The legacy AI DSL grammar (Appendix D.3) and its error messages.</summary>
public class AiParserTests
{
    private static AiScriptSyntax Parse(string text) => AiParser.Parse("t.ai", text);

    private static AiRuleItem SingleRule(string text)
    {
        AiScriptSyntax s = Parse(text);
        Assert.Empty(s.Diagnostics);
        return Assert.IsType<AiRuleItem>(Assert.Single(s.Items));
    }

    [Fact]
    public void A_rule_keeps_its_clauses_in_source_order()
    {
        AiRuleItem rule = SingleRule("rule ST1 when time 120000 trigger Started say \"2 mins\" end");
        Assert.Equal("ST1", rule.Name);
        Assert.False(rule.Critical);
        Assert.Collection(
            rule.Clauses,
            c => Assert.Equal(AiConditionKind.Time, Assert.Single(Assert.IsType<AiWhenClause>(c).Conditions).Kind),
            c => Assert.Equal("Started", Assert.Single(Assert.IsType<AiTriggerClause>(c).Changes).Flag),
            c => Assert.Equal("2 mins", Assert.IsType<AiSayClause>(c).Text));
    }

    [Fact]
    public void Keywords_are_case_insensitive_and_names_keep_their_spelling()
    {
        AiScriptSyntax s = Parse("CRITICAL RULE WHEN not Farm END\nrule when farm forget this end\nCritical rule forget THIS end");
        Assert.Empty(s.Diagnostics);
        var rules = s.Items.Cast<AiRuleItem>().ToArray();
        Assert.True(rules[0].Critical);
        AiCondition c = Assert.Single(Assert.IsType<AiWhenClause>(rules[0].Clauses[0]).Conditions);
        Assert.True(c.Negated);
        Assert.Equal("Farm", c.Name);
        Assert.Null(Assert.IsType<AiForgetClause>(rules[1].Clauses[1]).RuleName);
        Assert.Null(Assert.IsType<AiForgetClause>(rules[2].Clauses[0]).RuleName);
    }

    [Fact]
    public void A_name_before_the_first_clause_is_the_rule_name()
    {
        // As in the corpus, a rule named like a technology and followed by a goal is a named rule without a condition.
        AiRuleItem rule = SingleRule("rule te_masonry have te_moat end");
        Assert.Equal("te_masonry", rule.Name);
        Assert.Equal("te_moat", Assert.Single(Assert.IsType<AiHaveClause>(Assert.Single(rule.Clauses)).Goals).Name);
    }

    [Fact]
    public void Trigger_after_a_comma_is_a_condition_and_without_one_a_clause()
    {
        AiRuleItem rule = SingleRule("rule when storage, wood 700, trigger Ready have te_crossbow end");
        AiWhenClause when = Assert.IsType<AiWhenClause>(rule.Clauses[0]);
        Assert.Equal(3, when.Conditions.Length);
        Assert.Equal(AiConditionKind.Flag, when.Conditions[2].Kind);
        Assert.Equal(700, when.Conditions[1].Number);

        rule = SingleRule("rule when not trigger Chosen trigger Plan_B, Chosen end");
        Assert.True(Assert.Single(Assert.IsType<AiWhenClause>(rule.Clauses[0]).Conditions).Negated);
        Assert.Equal(["Plan_B", "Chosen"], Assert.IsType<AiTriggerClause>(rule.Clauses[1]).Changes.Select(c => c.Flag));
    }

    [Fact]
    public void Conditions_of_every_kind_parse()
    {
        AiRuleItem rule = SingleRule(
            "rule when enemy pikeman 15, not enemy archer 40, peon 20%, units 5, random 40%, random 50, difficulty hard, civ asian, extern \"@wood($aik) >= 100\" end");
        AiCondition[] c = [.. Assert.IsType<AiWhenClause>(rule.Clauses[0]).Conditions];
        Assert.True(c[0].Enemy && c[0].Number == 15);
        Assert.True(c[1].Enemy && c[1].Negated);
        Assert.True(c[2].Percent && c[2].Number == 20);
        Assert.Equal("units", c[3].Name);
        Assert.True(c[4].Kind == AiConditionKind.Random && c[4].Percent && c[4].Number == 40);
        Assert.True(c[5].Kind == AiConditionKind.Random && !c[5].Percent && c[5].Number == 50);
        Assert.Equal((AiConditionKind.Difficulty, "hard"), (c[6].Kind, c[6].Name));
        Assert.Equal((AiConditionKind.Civilization, "asian"), (c[7].Kind, c[7].Name));
        Assert.Equal(AiConditionKind.Extern, c[8].Kind);
        Assert.NotNull(c[8].Expression);
    }

    [Fact]
    public void Goals_take_counts_percentages_and_pseudo_objects()
    {
        AiRuleItem rule = SingleRule("rule have 10 archers, 40% peon, castle, 1000 gold, 500 convert, needs_locked trigger Army, !Old end");
        AiGoalSyntax[] g = [.. Assert.IsType<AiHaveClause>(rule.Clauses[0]).Goals];
        Assert.Equal((10, false, "archers"), (g[0].Number, g[0].Percent, g[0].Name));
        Assert.Equal((40, true, "peon"), (g[1].Number, g[1].Percent, g[1].Name));
        Assert.Equal((null, "castle"), (g[2].Number, g[2].Name));
        Assert.Equal("needs_locked", g[5].Name);
        AiTriggerClause t = Assert.IsType<AiTriggerClause>(rule.Clauses[1]);
        Assert.False(t.Changes[0].Clear);
        Assert.True(t.Changes[1].Clear);
    }

    [Fact]
    public void Blocks_and_includes_nest()
    {
        AiScriptSyntax s = Parse("{ critical rule when farm 2 end\n  { include \"a.ai\" }\n  rule have house end\n}");
        Assert.Empty(s.Diagnostics);
        AiBlockItem block = Assert.IsType<AiBlockItem>(Assert.Single(s.Items));
        Assert.Equal(3, block.Items.Length);
        Assert.Equal("a.ai", Assert.IsType<AiIncludeItem>(Assert.Single(Assert.IsType<AiBlockItem>(block.Items[1]).Items)).Path);
        Assert.Equal(new SourceLocation("t.ai", 2, 3), block.Items[1].Location);
    }

    [Fact]
    public void Comments_tabs_and_crlf_are_ignored()
    {
        AiScriptSyntax s = Parse("; header\r\n\trule when castle ; trailing\r\n\t\thave house end ; it'll never happend\r\n");
        Assert.Empty(s.Diagnostics);
        Assert.Equal(new SourceLocation("t.ai", 2, 2), Assert.Single(s.Items).Location);
    }

    public static TheoryData<string, AiDiagnosticCode, int, int> Errors() => new()
    {
        { "rule when castle have house", AiDiagnosticCode.MissingEnd, 1, 1 },
        { "rule have house end\n}", AiDiagnosticCode.UnmatchedCloseBrace, 2, 1 },
        { "{ rule have house end", AiDiagnosticCode.MissingCloseBrace, 1, 1 },
        { "rule say \"hello end", AiDiagnosticCode.UnterminatedString, 1, 10 },
        { "rule when gold 99999999999 end", AiDiagnosticCode.NumberTooLarge, 1, 16 },
        { "rule when castle # end", AiDiagnosticCode.InvalidCharacter, 1, 18 },
        { "rule when have house end", AiDiagnosticCode.MissingOperand, 1, 11 },
        { "rule have end", AiDiagnosticCode.MissingOperand, 1, 11 },
        { "rule trigger end", AiDiagnosticCode.MissingOperand, 1, 14 },
        { "rule when difficulty medium end", AiDiagnosticCode.InvalidArgument, 1, 11 },
        { "rule when extern \"(@wood(1) >= \" end", AiDiagnosticCode.InvalidExpression, 1, 32 },
        { "critical have house end", AiDiagnosticCode.UnexpectedToken, 1, 10 },
        { "house", AiDiagnosticCode.UnexpectedToken, 1, 1 },
        { "rule 10x end", AiDiagnosticCode.UnexpectedToken, 1, 6 },
        { "include", AiDiagnosticCode.MissingOperand, 1, 8 },
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public void Malformed_input_gives_a_located_error(string text, AiDiagnosticCode code, int line, int column)
    {
        AiDiagnostic d = Parse(text).Diagnostics.First(x => x.Severity == AiDiagnosticSeverity.Error);
        Assert.Equal(code, d.Code);
        Assert.Equal((line, column), (d.Location.Line, d.Location.Column));
        Assert.StartsWith($"t.ai({line},{column}): error AI{(int)code}: ", d.ToString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Parsing_resumes_after_an_error()
    {
        AiScriptSyntax s = Parse("rule when castle have\nrule have house end\nrule ## end\nrule have farm end");
        // "have" without a goal, that rule's missing 'end', and the invalid characters.
        Assert.Equal(3, s.Diagnostics.Count(d => d.Severity == AiDiagnosticSeverity.Error));
        string[] goals = [.. s.Items.Cast<AiRuleItem>().SelectMany(r => r.Clauses.OfType<AiHaveClause>()).SelectMany(h => h.Goals).Select(g => g.Name)];
        Assert.Equal(["house", "farm"], goals);
    }

    [Fact]
    public void Deep_nesting_is_reported_without_recursing()
    {
        string text = new string('{', 100_000) + "rule have house end" + new string('}', 100_000);
        AiScriptSyntax s = Parse(text);
        Assert.Contains(s.Diagnostics, d => d.Code == AiDiagnosticCode.NestingTooDeep);
    }

    [Fact]
    public void Oversized_input_is_rejected_up_front()
    {
        AiScriptSyntax s = AiParser.Parse("big.ai", new string(' ', 100), new AiParseLimits { MaxSourceLength = 10 });
        Assert.Equal(AiDiagnosticCode.SourceTooLarge, Assert.Single(s.Diagnostics).Code);
        Assert.Empty(s.Items);
    }

    [Fact]
    public void Too_many_messages_are_summarised()
    {
        AiScriptSyntax s = AiParser.Parse("bad.ai", string.Concat(Enumerable.Repeat("# ", 500)), new AiParseLimits { MaxDiagnostics = 10 });
        Assert.Equal(11, s.Diagnostics.Length);
        Assert.Equal(AiDiagnosticCode.TooManyDiagnostics, s.Diagnostics[^1].Code);
    }

    [Fact]
    public void A_rule_has_a_bounded_number_of_parts()
    {
        string goals = string.Join(", ", Enumerable.Range(0, 100).Select(i => "house"));
        AiScriptSyntax s = AiParser.Parse("t.ai", $"rule have {goals} end", new AiParseLimits { MaxClausesPerRule = 8 });
        Assert.Contains(s.Diagnostics, d => d.Code == AiDiagnosticCode.RuleTooLarge);
        Assert.Equal(8, Assert.IsType<AiHaveClause>(Assert.IsType<AiRuleItem>(Assert.Single(s.Items)).Clauses[0]).Goals.Length);
    }
}
