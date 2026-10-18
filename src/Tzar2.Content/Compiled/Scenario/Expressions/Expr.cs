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

/// <summary>Reserved variables (Appendix C.6).</summary>
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
}

/// <summary>Built-in functions.</summary>
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
