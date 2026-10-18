namespace Tzar2.Scripting.Legacy;

/// <summary>
/// Observer of the trigger engine for the editor's trigger debugger (TDD §18.3, ADR 0153). It is called on the sim
/// thread inside the tick; it must not change the world or the trigger state, and it is not part of the state or the
/// hashes, so a match with and without an observer stays bit-identical.
/// </summary>
public interface ITriggerTrace
{
    /// <summary>A rule pass started (all events were evaluated).</summary>
    /// <param name="tick">The tick.</param>
    /// <param name="pass">The pass number.</param>
    void PassStarted(int tick, int pass);

    /// <summary>A rule's condition was evaluated (enabled rules only).</summary>
    /// <param name="tick">The tick.</param>
    /// <param name="rule">Rule index.</param>
    /// <param name="value">The condition's value (non-zero = the rule fires).</param>
    void RuleEvaluated(int tick, int rule, int value);

    /// <summary>An action is about to run.</summary>
    /// <param name="tick">The tick.</param>
    /// <param name="rule">The rule running it (−1 for none).</param>
    /// <param name="action">Action index.</param>
    void ActionRun(int tick, int rule, int action);
}
