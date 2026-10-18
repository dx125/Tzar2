namespace Tzar2.Content.Compiled.Scenario.Expressions;

/// <summary>
/// A node of the legacy integer expression language (Appendix C.6, REQ-SCR-01), with every name already resolved to
/// an index of its <see cref="ScenarioData"/> table (ADR 0120). Evaluation semantics (integer width, division by zero,
/// short-circuiting) belong to the trigger VM in Tzar2.Scripting, not to this model.
/// </summary>
public abstract record Expr;

/// <summary>An integer literal.</summary>
/// <param name="Value">The value (the legacy language has integers only).</param>
public sealed record IntLiteral(int Value) : Expr;

/// <summary>A user variable <c>$NAME</c>.</summary>
/// <param name="VariableIndex">Index into <see cref="ScenarioData.Variables"/>.</param>
public sealed record UserVar(int VariableIndex) : Expr;

/// <summary>A reserved variable of the current object/player (<c>$health</c>, <c>$gold</c>…).</summary>
/// <param name="Variable">Which one.</param>
public sealed record ReservedVar(ReservedVariable Variable) : Expr;

/// <summary>An event used as a boolean (1 while the event holds, else 0).</summary>
/// <param name="EventIndex">Index into <see cref="ScenarioData.Events"/>.</param>
public sealed record EventRef(int EventIndex) : Expr;

/// <summary>A timer: the milliseconds counted by a Set Timer action, referenced by the action's name.</summary>
/// <param name="ActionIndex">Index into <see cref="ScenarioData.Actions"/> of a <see cref="SetTimerAction"/>.</param>
public sealed record TimerRef(int ActionIndex) : Expr;

/// <summary>A name the converter could not resolve. Evaluates to 0 (REQ-SCR-02); the conversion report warns.</summary>
/// <param name="Name">The name as written (with its <c>$</c>, if any).</param>
public sealed record UnknownRef(string Name) : Expr;

/// <summary>A unary operation.</summary>
/// <param name="Op">The operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record UnaryExpr(UnaryOp Op, Expr Operand) : Expr;

/// <summary>A binary operation. <c>&amp;&amp;</c> and <c>||</c> short-circuit and yield 0/1.</summary>
/// <param name="Op">The operator.</param>
/// <param name="Left">Left operand.</param>
/// <param name="Right">Right operand.</param>
public sealed record BinaryExpr(BinaryOp Op, Expr Left, Expr Right) : Expr;

/// <summary>An assignment <c>target = value</c>; its value is the assigned value.</summary>
/// <param name="Target">A <see cref="UserVar"/>, or a <see cref="ReservedVar"/> (Set Object Properties, Object Expression).</param>
/// <param name="Value">The assigned expression.</param>
public sealed record AssignExpr(Expr Target, Expr Value) : Expr;

/// <summary>A built-in function call: <c>@gold(p)</c>, <c>@stone(p)</c>, <c>@food(p)</c>, <c>@wood(p)</c>, <c>@rnd(a, b)</c>.</summary>
/// <param name="Function">Which function.</param>
/// <param name="Arguments">Arguments (one for resources, two for rnd).</param>
public sealed record CallExpr(CallKind Function, EquatableArray<Expr> Arguments) : Expr;

/// <summary>Several statements (separated by line breaks or <c>;</c>); the value is the last statement's.</summary>
/// <param name="Statements">The statements, in order.</param>
public sealed record SequenceExpr(EquatableArray<Expr> Statements) : Expr;

/// <summary>A raw text argument, e.g. the movie path of <c>@PlayMovie(movies\outro.ddv)</c>.</summary>
/// <param name="Text">The text between the parentheses, trimmed.</param>
public sealed record TextArg(string Text) : Expr;

/// <summary>
/// Reserved variables of the current object or player. The first group is Appendix C.6; the rest are the further
/// names the original game recognises (ADR 0120). Semantics beyond C.6 are ⚠ UNVERIFIED (UNK-73).
/// </summary>
public enum ReservedVariable : byte
{
    /// <summary><c>$health</c>.</summary>
    Health,

    /// <summary><c>$maxhealth</c>.</summary>
    MaxHealth,

    /// <summary><c>$attack</c>.</summary>
    Attack,

    /// <summary><c>$defense</c>.</summary>
    Defense,

    /// <summary><c>$level</c>.</summary>
    Level,

    /// <summary><c>$manna</c>.</summary>
    Manna,

    /// <summary><c>$maxmanna</c>.</summary>
    MaxManna,

    /// <summary><c>$kingdom</c>.</summary>
    Kingdom,

    /// <summary><c>$x</c>.</summary>
    X,

    /// <summary><c>$y</c>.</summary>
    Y,

    /// <summary><c>$gold</c>.</summary>
    Gold,

    /// <summary><c>$stone</c>.</summary>
    Stone,

    /// <summary><c>$food</c>.</summary>
    Food,

    /// <summary><c>$wood</c>.</summary>
    Wood,

    /// <summary><c>$difficulty</c>.</summary>
    Difficulty,

    /// <summary><c>$underfog</c>.</summary>
    UnderFog,

    /// <summary><c>$flags</c>.</summary>
    Flags,

    /// <summary><c>$program</c>.</summary>
    Program,

    /// <summary><c>$inholder</c>.</summary>
    InHolder,

    /// <summary><c>$isselected</c>.</summary>
    IsSelected,

    /// <summary><c>$cargo</c>.</summary>
    Cargo,

    /// <summary><c>$race</c>.</summary>
    Race,

    /// <summary><c>$canusediplomacy</c>.</summary>
    CanUseDiplomacy,

    /// <summary><c>$candelobjects</c>.</summary>
    CanDelObjects,
}

/// <summary>
/// Built-in functions (<c>@name(…)</c>, case-insensitive). The first five are Appendix C.6; the rest are the further
/// names the original game recognises (ADR 0120), semantics ⚠ UNVERIFIED (UNK-73). Arity is not checked here.
/// </summary>
public enum CallKind : byte
{
    /// <summary><c>@gold(player)</c>.</summary>
    Gold,

    /// <summary><c>@stone(player)</c>.</summary>
    Stone,

    /// <summary><c>@food(player)</c>.</summary>
    Food,

    /// <summary><c>@wood(player)</c>.</summary>
    Wood,

    /// <summary><c>@rnd(min, max)</c> (bounds inclusive, ⚠ UNVERIFIED).</summary>
    Rnd,

    /// <summary><c>@max(a, b)</c>.</summary>
    Max,

    /// <summary><c>@min(a, b)</c>.</summary>
    Min,

    /// <summary><c>@count(…)</c>.</summary>
    Count,

    /// <summary><c>@inprogress(…)</c>.</summary>
    InProgress,

    /// <summary><c>@setnoattacktime(…)</c>.</summary>
    SetNoAttackTime,

    /// <summary><c>@setgold(player, amount)</c>.</summary>
    SetGold,

    /// <summary><c>@setstone(player, amount)</c>.</summary>
    SetStone,

    /// <summary><c>@setwood(player, amount)</c>.</summary>
    SetWood,

    /// <summary><c>@setfood(player, amount)</c>.</summary>
    SetFood,

    /// <summary><c>@build(…)</c>.</summary>
    Build,

    /// <summary><c>@setpoplimit(player, limit)</c>.</summary>
    SetPopLimit,

    /// <summary><c>@hassharedres(a, b)</c>.</summary>
    HasSharedRes,

    /// <summary><c>@hassharedunits(a, b)</c>.</summary>
    HasSharedUnits,

    /// <summary><c>@hassharedvision(a, b)</c>.</summary>
    HasSharedVision,

    /// <summary><c>@isenemy(a, b)</c>.</summary>
    IsEnemy,

    /// <summary><c>@isneutral(a, b)</c>.</summary>
    IsNeutral,

    /// <summary><c>@isally(a, b)</c>.</summary>
    IsAlly,

    /// <summary><c>@getdiplomacy(a, b)</c>.</summary>
    GetDiplomacy,

    /// <summary><c>@gettime()</c>.</summary>
    GetTime,

    /// <summary><c>@settime(hour)</c>.</summary>
    SetTime,

    /// <summary><c>@god(on)</c>.</summary>
    God,

    /// <summary><c>@clearselection()</c>.</summary>
    ClearSelection,

    /// <summary><c>@unselect(…)</c>.</summary>
    Unselect,

    /// <summary><c>@select(…)</c>.</summary>
    Select,

    /// <summary><c>@setai(player, mode)</c>.</summary>
    SetAi,

    /// <summary><c>@kingdomcanrescue(player, on)</c>.</summary>
    KingdomCanRescue,

    /// <summary><c>@str(text)</c> (raw text argument).</summary>
    Str,

    /// <summary><c>@playmovie(path)</c> (raw text argument).</summary>
    PlayMovie,
}

/// <summary>Unary operators.</summary>
public enum UnaryOp : byte
{
    /// <summary><c>!</c> (0 → 1, else 0).</summary>
    Not,

    /// <summary>Unary minus.</summary>
    Negate,
}

/// <summary>Binary operators.</summary>
public enum BinaryOp : byte
{
    /// <summary><c>+</c>.</summary>
    Add,

    /// <summary><c>-</c>.</summary>
    Subtract,

    /// <summary><c>*</c>.</summary>
    Multiply,

    /// <summary><c>/</c>.</summary>
    Divide,

    /// <summary><c>%</c>.</summary>
    Modulo,

    /// <summary><c>&lt;</c>.</summary>
    Less,

    /// <summary><c>&lt;=</c>.</summary>
    LessOrEqual,

    /// <summary><c>&gt;</c>.</summary>
    Greater,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterOrEqual,

    /// <summary><c>==</c>.</summary>
    Equal,

    /// <summary><c>!=</c>.</summary>
    NotEqual,

    /// <summary><c>&amp;&amp;</c>.</summary>
    And,

    /// <summary><c>||</c>.</summary>
    Or,
}

/// <summary>
/// An expression as stored in a scenario: the source text (kept for display, editing and round trips) and its parsed
/// tree. <see cref="Root"/> is null when the text is empty; a text that does not parse keeps <see cref="Error"/>.
/// </summary>
/// <param name="Source">The text as written.</param>
/// <param name="Root">The parsed tree, or null when empty or unparsable.</param>
/// <param name="Error">The parse error, or null.</param>
public sealed record ExpressionDef(string Source, Expr? Root, string? Error = null)
{
    /// <summary>The empty expression.</summary>
    public static ExpressionDef Empty { get; } = new(string.Empty, null);

    /// <summary>Whether the text is empty.</summary>
    public bool IsEmpty => Source.Length == 0;
}
