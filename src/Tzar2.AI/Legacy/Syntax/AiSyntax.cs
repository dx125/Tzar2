using System.Collections.Immutable;

namespace Tzar2.AI.Legacy.Syntax;

/// <summary>A top-level or block item: an include, a block or a rule (Appendix D.3 <c>item</c>).</summary>
/// <param name="Location">Where the item starts.</param>
public abstract record AiItem(SourceLocation Location);

/// <summary><c>include "file"</c>: textual inclusion of another script, relative to the AI script folder.</summary>
/// <param name="Path">The file name as written.</param>
/// <param name="Location">Where.</param>
public sealed record AiIncludeItem(string Path, SourceLocation Location) : AiItem(Location);

/// <summary><c>{ item* }</c>: a gating scope for <c>critical</c> rules.</summary>
/// <param name="Items">The items in order.</param>
/// <param name="Location">The opening brace.</param>
public sealed record AiBlockItem(ImmutableArray<AiItem> Items, SourceLocation Location) : AiItem(Location);

/// <summary><c>[critical] rule [NAME] clause* end</c>.</summary>
/// <param name="Critical">Whether the rule gates the rest of its block.</param>
/// <param name="Name">The rule name (addressable by <c>forget</c>), or null.</param>
/// <param name="Clauses">The clauses in source order.</param>
/// <param name="Location">The <c>rule</c> (or <c>critical</c>) keyword.</param>
public sealed record AiRuleItem(bool Critical, string? Name, ImmutableArray<AiClause> Clauses, SourceLocation Location) : AiItem(Location);

/// <summary>A clause of a rule.</summary>
/// <param name="Location">The clause keyword.</param>
public abstract record AiClause(SourceLocation Location);

/// <summary><c>when cond (, cond)*</c>: all conditions must hold.</summary>
/// <param name="Conditions">The conditions in order (evaluated left to right, short-circuit).</param>
/// <param name="Location">Where.</param>
public sealed record AiWhenClause(ImmutableArray<AiCondition> Conditions, SourceLocation Location) : AiClause(Location);

/// <summary><c>have goal (, goal)*</c>: goals the AI pursues while the rule is active.</summary>
/// <param name="Goals">The goals in order.</param>
/// <param name="Location">Where.</param>
public sealed record AiHaveClause(ImmutableArray<AiGoalSyntax> Goals, SourceLocation Location) : AiClause(Location);

/// <summary><c>trigger [!]FLAG (, [!]FLAG)*</c>: sets (or, with <c>!</c>, clears) named flags.</summary>
/// <param name="Changes">The flag changes in order.</param>
/// <param name="Location">Where.</param>
public sealed record AiTriggerClause(ImmutableArray<AiFlagChange> Changes, SourceLocation Location) : AiClause(Location);

/// <summary><c>forget NAME</c> or <c>forget THIS</c>: disables a rule for the rest of the match.</summary>
/// <param name="RuleName">The rule name, or null for <c>THIS</c>.</param>
/// <param name="Location">Where.</param>
public sealed record AiForgetClause(string? RuleName, SourceLocation Location) : AiClause(Location);

/// <summary><c>say "text"</c>: a chat message.</summary>
/// <param name="Text">The message.</param>
/// <param name="Location">Where.</param>
public sealed record AiSayClause(string Text, SourceLocation Location) : AiClause(Location);

/// <summary>What a condition tests.</summary>
public enum AiConditionKind : byte
{
    /// <summary><c>[enemy] NAME [N [%]]</c>: a count of objects, a resource stock, <c>units</c> or a technology (resolved when binding).</summary>
    Count = 1,

    /// <summary><c>trigger FLAG</c>: the flag is set.</summary>
    Flag = 2,

    /// <summary><c>random N [%]</c>: true with probability N %.</summary>
    Random = 3,

    /// <summary><c>time N</c>: at least N ms of game time have passed.</summary>
    Time = 4,

    /// <summary><c>difficulty easy|normal|hard</c>.</summary>
    Difficulty = 5,

    /// <summary><c>civ european|asian|arabian</c>.</summary>
    Civilization = 6,

    /// <summary><c>extern "expression"</c>: an expression of the trigger language (Appendix C.6) is non-zero.</summary>
    Extern = 7,
}

/// <summary>One condition.</summary>
/// <param name="Negated">Preceded by <c>not</c>.</param>
/// <param name="Kind">What it tests.</param>
/// <param name="Name">The object/resource name (<see cref="AiConditionKind.Count"/>), flag, difficulty or civilization; the expression text for <see cref="AiConditionKind.Extern"/>.</param>
/// <param name="Number">The number, or null when omitted.</param>
/// <param name="Percent">The number had a <c>%</c>.</param>
/// <param name="Enemy">Preceded by <c>enemy</c> (count over the enemies' objects).</param>
/// <param name="Expression">The parsed expression of an <see cref="AiConditionKind.Extern"/> condition (null if it did not parse).</param>
/// <param name="Location">Where.</param>
public sealed record AiCondition(
    bool Negated,
    AiConditionKind Kind,
    string Name,
    int? Number,
    bool Percent,
    bool Enemy,
    AiExpression? Expression,
    SourceLocation Location);

/// <summary>One goal: <c>[N [%]] NAME</c>.</summary>
/// <param name="Number">The count or amount, or null (= 1 object).</param>
/// <param name="Percent">The number had a <c>%</c> (share of the own units).</param>
/// <param name="Name">The object, resource, technology or pseudo-object (<c>convert</c>, <c>needs_locked</c>, <c>resign</c>).</param>
/// <param name="Location">Where.</param>
public sealed record AiGoalSyntax(int? Number, bool Percent, string Name, SourceLocation Location);

/// <summary>One flag change of a <c>trigger</c> clause.</summary>
/// <param name="Clear">Written <c>!FLAG</c>.</param>
/// <param name="Flag">The flag name as written.</param>
/// <param name="Location">Where.</param>
public readonly record struct AiFlagChange(bool Clear, string Flag, SourceLocation Location);

/// <summary>A parsed script file.</summary>
/// <param name="File">The normalised script name.</param>
/// <param name="Items">The items in order.</param>
/// <param name="Diagnostics">Lexing and parsing messages of this file.</param>
public sealed record AiScriptSyntax(string File, ImmutableArray<AiItem> Items, ImmutableArray<AiDiagnostic> Diagnostics);
