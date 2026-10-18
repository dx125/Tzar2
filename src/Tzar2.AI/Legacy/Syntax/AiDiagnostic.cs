using System;
using System.Globalization;

namespace Tzar2.AI.Legacy.Syntax;

/// <summary>A position in a script file: 1-based line and column (columns count characters; a tab is one column).</summary>
/// <param name="File">The script name as the loader knows it (normalised, e.g. <c>random.ais</c>).</param>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column.</param>
public readonly record struct SourceLocation(string File, int Line, int Column)
{
    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{File}({Line},{Column})");
}

/// <summary>How bad a diagnostic is.</summary>
public enum AiDiagnosticSeverity : byte
{
    /// <summary>The script still loads; something is ignored or degraded (e.g. a missing include, an unknown name).</summary>
    Warning = 1,

    /// <summary>The script text is malformed; the offending construct is skipped.</summary>
    Error = 2,
}

/// <summary>
/// Stable diagnostic codes of the legacy AI script toolchain (ADR 0050). <c>AI1xxx</c>: lexing and parsing,
/// <c>AI2xxx</c>: loading (includes, limits), <c>AI3xxx</c>: binding names against a ruleset.
/// </summary>
public enum AiDiagnosticCode : ushort
{
    /// <summary>A character that cannot start a token.</summary>
    InvalidCharacter = 1001,

    /// <summary>A string literal without its closing quote on the same line.</summary>
    UnterminatedString = 1002,

    /// <summary>A number that does not fit in 31 bits.</summary>
    NumberTooLarge = 1003,

    /// <summary>A token that cannot appear here.</summary>
    UnexpectedToken = 1004,

    /// <summary>A rule without its closing <c>end</c>.</summary>
    MissingEnd = 1005,

    /// <summary>A <c>{</c> without its <c>}</c>.</summary>
    MissingCloseBrace = 1006,

    /// <summary>A <c>}</c> without its <c>{</c>.</summary>
    UnmatchedCloseBrace = 1007,

    /// <summary>A clause keyword without its operand (e.g. <c>when</c> with no condition, <c>have</c> with no goal).</summary>
    MissingOperand = 1008,

    /// <summary>Blocks or expressions nested deeper than <see cref="AiParseLimits.MaxNestingDepth"/>.</summary>
    NestingTooDeep = 1009,

    /// <summary>A malformed <c>extern</c> expression.</summary>
    InvalidExpression = 1010,

    /// <summary>A string literal longer than <see cref="AiParseLimits.MaxStringLength"/>.</summary>
    StringTooLong = 1011,

    /// <summary>More conditions or actions in one rule than the limits allow.</summary>
    RuleTooLarge = 1012,

    /// <summary>A keyword argument that is not one of its allowed values (e.g. <c>difficulty medium</c>).</summary>
    InvalidArgument = 1013,

    /// <summary>The file is larger than <see cref="AiParseLimits.MaxSourceLength"/>.</summary>
    SourceTooLarge = 2001,

    /// <summary>An included script does not exist (the rest still loads, Appendix D.6).</summary>
    IncludeNotFound = 2002,

    /// <summary>A script includes itself, directly or indirectly; the cyclic include is skipped.</summary>
    IncludeCycle = 2003,

    /// <summary>Includes nested deeper than <see cref="AiParseLimits.MaxIncludeDepth"/>.</summary>
    IncludeTooDeep = 2004,

    /// <summary>The expanded program exceeds <see cref="AiParseLimits.MaxRules"/> rules, <see cref="AiParseLimits.MaxIncludes"/> include expansions or <see cref="AiParseLimits.MaxExpandedItems"/> items.</summary>
    ProgramTooLarge = 2005,

    /// <summary>Too many diagnostics; the rest are dropped.</summary>
    TooManyDiagnostics = 2006,

    /// <summary>A name that is neither an object class, a resource, a technology nor a pseudo-object of the ruleset.</summary>
    UnknownName = 3001,

    /// <summary>A name used where its kind makes no sense (e.g. <c>when needs_locked</c>).</summary>
    MisusedName = 3002,

    /// <summary>A construct the current ruleset or sim cannot execute yet; it is treated as documented (ADR 0050).</summary>
    Unsupported = 3003,
}

/// <summary>One message about a script, with its location, for players, modders and the editor's AI-script view.</summary>
/// <param name="Severity">Warning or error.</param>
/// <param name="Code">Stable code.</param>
/// <param name="Location">Where.</param>
/// <param name="Message">What, in plain English.</param>
public sealed record AiDiagnostic(AiDiagnosticSeverity Severity, AiDiagnosticCode Code, SourceLocation Location, string Message)
{
    /// <summary>The code as printed: <c>AI1004</c>.</summary>
    public string CodeText => string.Create(CultureInfo.InvariantCulture, $"AI{(int)Code:D4}");

    /// <inheritdoc />
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Location}: {(Severity == AiDiagnosticSeverity.Error ? "error" : "warning")} {CodeText}: {Message}");
}

/// <summary>
/// Hard limits that keep the toolchain linear and bounded on hostile input (user and mod scripts are untrusted, like
/// snapshots). The original corpus is far below every limit.
/// </summary>
public sealed record AiParseLimits
{
    /// <summary>The defaults.</summary>
    public static AiParseLimits Default { get; } = new();

    /// <summary>Largest script file in characters.</summary>
    public int MaxSourceLength { get; init; } = 1 << 20;

    /// <summary>Deepest nesting of <c>{ }</c> blocks, and of <c>extern</c> sub-expressions.</summary>
    public int MaxNestingDepth { get; init; } = 64;

    /// <summary>Deepest include chain.</summary>
    public int MaxIncludeDepth { get; init; } = 16;

    /// <summary>Most include expansions in one program (a module included twice counts twice; bounds diamond-shaped include graphs).</summary>
    public int MaxIncludes { get; init; } = 4096;

    /// <summary>Most items (rules, blocks and includes) visited while expanding includes; bounds compile time.</summary>
    public int MaxExpandedItems { get; init; } = 262_144;

    /// <summary>Most rules sharing one name (each <c>forget NAME</c> disables all of them, so this bounds its cost).</summary>
    public int MaxRulesPerName { get; init; } = 64;

    /// <summary>Most nodes in one <c>extern</c> expression (its evaluation cost; deeper trees are rejected as well).</summary>
    public int MaxExpressionNodes { get; init; } = 256;

    /// <summary>Most rules in the program after include expansion.</summary>
    public int MaxRules { get; init; } = 65_536;

    /// <summary>Most parts (conditions, goals, flag changes, forgets and says together) in one rule.</summary>
    public int MaxClausesPerRule { get; init; } = 64;

    /// <summary>Longest string literal in characters.</summary>
    public int MaxStringLength { get; init; } = 1024;

    /// <summary>Most diagnostics kept per load; further ones are summarised by one <see cref="AiDiagnosticCode.TooManyDiagnostics"/>.</summary>
    public int MaxDiagnostics { get; init; } = 200;

    /// <summary>Checks the ranges.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSourceLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxNestingDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxNestingDepth, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxIncludeDepth, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxIncludeDepth, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxIncludes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxExpandedItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRulesPerName, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxExpressionNodes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxExpressionNodes, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRules, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRules, 1 << 22);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxClausesPerRule, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxClausesPerRule, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxStringLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDiagnostics, 1);
    }
}
