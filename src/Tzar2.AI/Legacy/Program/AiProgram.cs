using System.Collections.Immutable;
using Tzar2.AI.Legacy.Syntax;

namespace Tzar2.AI.Legacy.Program;

/// <summary>What a compiled condition tests.</summary>
public enum AiConditionOp : byte
{
    /// <summary>Own count of a symbol (object class, resource, <c>units</c>, technology) ≥ N (or ≥ N % of the own units).</summary>
    Count = 1,

    /// <summary>The enemies' count of an object class (or their units) ≥ N (or ≥ N % of their units).</summary>
    EnemyCount = 2,

    /// <summary>A flag is set.</summary>
    Flag = 3,

    /// <summary>True with probability N % (one draw from the player's <c>ai[p]</c> stream).</summary>
    Random = 4,

    /// <summary>Game time ≥ N ms.</summary>
    Time = 5,

    /// <summary>The player's difficulty is operand (0 easy, 1 normal, 2 hard; -1 = invalid, always false).</summary>
    Difficulty = 6,

    /// <summary>The player's civilization is <see cref="AiProgram.Civilizations"/>[operand].</summary>
    Civilization = 7,

    /// <summary><see cref="AiProgram.Externs"/>[operand] evaluates to non-zero (null = did not parse, always false).</summary>
    Extern = 8,
}

/// <summary>One compiled condition.</summary>
/// <param name="Op">What it tests.</param>
/// <param name="Negated">Preceded by <c>not</c>.</param>
/// <param name="Percent">The number had a <c>%</c>.</param>
/// <param name="Operand">Symbol, flag, difficulty, civilization or extern index.</param>
/// <param name="Number">The number (1 when a count was omitted, 0 when a number was missing).</param>
public readonly record struct AiConditionCode(AiConditionOp Op, bool Negated, bool Percent, int Operand, int Number);

/// <summary>What a compiled action does when its rule is active.</summary>
public enum AiActionOp : byte
{
    /// <summary>Pursue goal symbol operand: N objects (or N % of the units), N of a resource, a technology or a pseudo-object.</summary>
    Goal = 1,

    /// <summary>Set flag operand.</summary>
    SetFlag = 2,

    /// <summary>Clear flag operand.</summary>
    ClearFlag = 3,

    /// <summary>Disable every rule named <see cref="AiProgram.RuleNames"/>[operand].</summary>
    Forget = 4,

    /// <summary>Disable this rule.</summary>
    ForgetThis = 5,

    /// <summary>Post chat message <see cref="AiProgram.Texts"/>[operand] (when the rule becomes active).</summary>
    Say = 6,
}

/// <summary>One compiled action.</summary>
/// <param name="Op">What it does.</param>
/// <param name="Operand">Symbol, flag, rule-name or text index.</param>
/// <param name="Number">The goal's number, or -1 when omitted.</param>
/// <param name="Percent">The goal's number had a <c>%</c>.</param>
public readonly record struct AiActionCode(AiActionOp Op, int Operand, int Number, bool Percent);

/// <summary>One compiled rule.</summary>
/// <param name="BlockEnd">The rule index just after the rule's block: where a failing <c>critical</c> rule jumps.</param>
/// <param name="FirstCondition">Index of its first condition in <see cref="AiProgram.Conditions"/>.</param>
/// <param name="ConditionCount">Number of conditions (0 = always active).</param>
/// <param name="FirstAction">Index of its first action in <see cref="AiProgram.Actions"/>.</param>
/// <param name="ActionCount">Number of actions.</param>
/// <param name="Name">Index into <see cref="AiProgram.RuleNames"/>, or -1.</param>
/// <param name="Critical">Whether it gates the rest of its block.</param>
public readonly record struct AiRuleCode(int BlockEnd, int FirstCondition, int ConditionCount, int FirstAction, int ActionCount, int Name, bool Critical);

/// <summary>
/// A legacy AI script compiled to a flat rule array with its includes expanded (ADR 0050). Ruleset-independent and
/// immutable: one program serves every AI player that uses the script; names are bound per ruleset and civilization by
/// <c>AiBinding</c>. Tables are in order of first appearance, so equal sources give equal programs and hashes.
/// </summary>
public sealed class AiProgram
{
    internal AiProgram(
        string root,
        ImmutableArray<AiRuleCode> rules,
        ImmutableArray<SourceLocation> ruleLocations,
        ImmutableArray<AiConditionCode> conditions,
        ImmutableArray<AiActionCode> actions,
        ImmutableArray<string> symbols,
        ImmutableArray<string> flags,
        ImmutableArray<string> ruleNames,
        ImmutableArray<ImmutableArray<int>> rulesByName,
        ImmutableArray<string> texts,
        ImmutableArray<string> civilizations,
        ImmutableArray<AiExpression?> externs,
        ImmutableArray<int> externCosts,
        ImmutableArray<SourceLocation> symbolLocations,
        ImmutableArray<string> files,
        ImmutableArray<AiDiagnostic> diagnostics,
        ulong contentHash)
    {
        Root = root;
        Rules = rules;
        RuleLocations = ruleLocations;
        Conditions = conditions;
        Actions = actions;
        Symbols = symbols;
        Flags = flags;
        RuleNames = ruleNames;
        RulesByName = rulesByName;
        Texts = texts;
        Civilizations = civilizations;
        Externs = externs;
        ExternCosts = externCosts;
        SymbolLocations = symbolLocations;
        Files = files;
        Diagnostics = diagnostics;
        ContentHash = contentHash;
    }

    /// <summary>The normalised name of the root script.</summary>
    public string Root { get; }

    /// <summary>The rules in evaluation order.</summary>
    public ImmutableArray<AiRuleCode> Rules { get; }

    /// <summary>Where each rule is (file, line, column of its <c>rule</c>/<c>critical</c> keyword).</summary>
    public ImmutableArray<SourceLocation> RuleLocations { get; }

    /// <summary>All conditions; a rule owns a contiguous range.</summary>
    public ImmutableArray<AiConditionCode> Conditions { get; }

    /// <summary>All actions in source order per rule; a rule owns a contiguous range.</summary>
    public ImmutableArray<AiActionCode> Actions { get; }

    /// <summary>Object, resource, technology and pseudo-object names (lower case).</summary>
    public ImmutableArray<string> Symbols { get; }

    /// <summary>Flag names (case-insensitive: interned lower case; UNVERIFIED, the corpus writes <c>trigger peon</c> for <c>Peon</c>).</summary>
    public ImmutableArray<string> Flags { get; }

    /// <summary>Rule names (lower case).</summary>
    public ImmutableArray<string> RuleNames { get; }

    /// <summary>The rule indices carrying each rule name (an included module can appear several times).</summary>
    public ImmutableArray<ImmutableArray<int>> RulesByName { get; }

    /// <summary>Chat texts of <c>say</c>.</summary>
    public ImmutableArray<string> Texts { get; }

    /// <summary>Civilization names used by <c>civ</c> conditions (lower case).</summary>
    public ImmutableArray<string> Civilizations { get; }

    /// <summary>Parsed <c>extern</c> expressions (null = invalid).</summary>
    public ImmutableArray<AiExpression?> Externs { get; }

    /// <summary>The instruction cost of evaluating each extern expression (1 + nodes / 8).</summary>
    public ImmutableArray<int> ExternCosts { get; }

    /// <summary>Where each symbol first appears (for binding diagnostics).</summary>
    public ImmutableArray<SourceLocation> SymbolLocations { get; }

    /// <summary>The scripts the program was built from (root first, then includes in first-use order).</summary>
    public ImmutableArray<string> Files { get; }

    /// <summary>Messages of parsing, include resolution and compilation.</summary>
    public ImmutableArray<AiDiagnostic> Diagnostics { get; }

    /// <summary>Whether any message is an error (the program still runs; malformed parts are skipped).</summary>
    public bool HasErrors
    {
        get
        {
            foreach (AiDiagnostic d in Diagnostics)
            {
                if (d.Severity == AiDiagnosticSeverity.Error)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>xxHash64 of the compiled code and tables: AI state in a snapshot is only valid for the same program.</summary>
    public ulong ContentHash { get; }
}
