namespace Tzar2.Scripting.Legacy;

/// <summary>
/// The integer arithmetic of legacy expressions (Appendix C.6, UNK-41; ADR 0130): 32-bit signed, saturating on
/// overflow, division and remainder by zero give 0 (REQ-SCR-02), comparisons and logic give 0/1. UNVERIFIED: the
/// original's width and overflow behaviour (UNK-41); the legacy AI's <c>extern</c> arithmetic makes the same choice
/// (docs/mechanics/ai-placeholders.md S24), so both script languages agree.
/// </summary>
public static class ScenarioArithmetic
{
    /// <summary>Clamps a 64-bit intermediate into the 32-bit range.</summary>
    public static int Saturate(long value) => value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;

    /// <summary><c>a + b</c>.</summary>
    public static int Add(int a, int b) => Saturate((long)a + b);

    /// <summary><c>a - b</c>.</summary>
    public static int Subtract(int a, int b) => Saturate((long)a - b);

    /// <summary><c>a * b</c>.</summary>
    public static int Multiply(int a, int b) => Saturate((long)a * b);

    /// <summary><c>a / b</c>, truncated toward zero; 0 when <paramref name="b"/> is 0.</summary>
    public static int Divide(int a, int b) => b == 0 ? 0 : Saturate((long)a / b);

    /// <summary><c>a % b</c> with the sign of <paramref name="a"/>; 0 when <paramref name="b"/> is 0.</summary>
    public static int Remainder(int a, int b) => b == 0 ? 0 : (int)((long)a % b);

    /// <summary><c>-a</c>.</summary>
    public static int Negate(int a) => Saturate(-(long)a);
}
