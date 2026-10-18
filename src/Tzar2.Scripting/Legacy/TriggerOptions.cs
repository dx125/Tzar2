using System;
using Tzar2.Core.Hashing;

namespace Tzar2.Scripting.Legacy;

/// <summary>Why a scenario action or evaluation reported a problem (<c>SimEventKind.ScenarioFault</c>, Arg).</summary>
public enum ScenarioFault
{
    /// <summary>A division or remainder by zero gave 0 (REQ-SCR-02).</summary>
    DivisionByZero = 1,

    /// <summary>The pass ran out of its per-tick budget and continues next tick.</summary>
    BudgetDeferred = 2,

    /// <summary>A conversation said more phrases than the guard allows and was cut (ORIG-13, REQ-SCR-02).</summary>
    ConversationGuard = 3,

    /// <summary>An action could not run (unknown type, no room, bad kingdom); the rule went on with the next action.</summary>
    ActionFailed = 4,

    /// <summary>An action of a type the converter could not decode was skipped.</summary>
    UnknownAction = 5,

    /// <summary>A built-in function the rebuild does not implement yet ran as a no-op (UNK-64).</summary>
    UnsupportedFunction = 7,

    /// <summary>A rule spent the hard step cap of a tick and was cut where it stood (ADR 0130).</summary>
    RuleCut = 8,

    /// <summary>A reserved variable cannot be written there (<c>$x</c>, <c>$y</c>, <c>$maxmanna</c>, or no current object).</summary>
    ReadOnlyVariable = 6,
}

/// <summary>
/// Settings of the legacy trigger engine (ADR 0130). They change what the scenario does, so they are part of the match:
/// the snapshot state carries their fingerprint and a restore with other settings is refused.
/// </summary>
public sealed record TriggerOptions
{
    /// <summary>The defaults.</summary>
    public static TriggerOptions Default { get; } = new();

    /// <summary>
    /// Milliseconds between rule passes: "all rules are checked about every second" (Appendix C.3; TDD §8.5 step 2
    /// <c>triggerIntervalMs</c>). UNVERIFIED: the exact period and phase (UNK-51).
    /// </summary>
    public int IntervalMs { get; init; } = 1000;

    /// <summary>
    /// Expression nodes and object tests a tick may spend on a pass (deterministic count budget, TDD §21). When a pass
    /// uses it up it stops after the current rule and continues in the next tick; every tick finishes at least one rule.
    /// </summary>
    public int StepBudgetPerTick { get; init; } = 200_000;

    /// <summary>
    /// Steps after which a tick cuts the rule that is running (ADR 0130): with the per-rule bounds checked at attach, the
    /// worst case per tick is this cap (the budget is reached within it).
    /// </summary>
    public int HardStepCapPerTick { get; init; } = 1_000_000;

    /// <summary>Most phrases one conversation session says before it is cut (REQ-SCR-02 runtime loop guard).</summary>
    public int MaxConversationSteps { get; init; } = 256;

    /// <summary>
    /// Radius in tiles of an area given as an object when the scenario stores none (Appendix C.4 "an object (its
    /// current position)"). UNVERIFIED (UNK-52).
    /// </summary>
    public int ObjectAreaRadiusTiles { get; init; } = 2;

    /// <summary>
    /// The kingdoms (bit = slot) that answer conversations and see the scenario's presentation: the human players. The
    /// first valid reply of any of them is taken.
    /// </summary>
    public uint ResponderMask { get; init; } = 0xFF;

    /// <summary>Checks the ranges.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(IntervalMs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(IntervalMs, 3_600_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(StepBudgetPerTick, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(HardStepCapPerTick, StepBudgetPerTick);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConversationSteps, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxConversationSteps, 100_000);
        ArgumentOutOfRangeException.ThrowIfNegative(ObjectAreaRadiusTiles);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ObjectAreaRadiusTiles, 64);
    }

    /// <summary>A fingerprint of every value.</summary>
    public ulong Fingerprint()
        => new StateHasher().Add(IntervalMs).Add(StepBudgetPerTick).Add(HardStepCapPerTick).Add(MaxConversationSteps).Add(ObjectAreaRadiusTiles).Add((int)ResponderMask).GetCurrentHash();
}
