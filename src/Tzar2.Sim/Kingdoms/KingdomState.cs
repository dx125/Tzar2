using System;
using System.Runtime.InteropServices;

namespace Tzar2.Sim.Kingdoms;

/// <summary>Per-kingdom cheat switches (GAME-19; only with <see cref="SimulationConfig.CheatsAllowed"/>).</summary>
[Flags]
public enum CheatFlags : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary><c>hmnofog</c>: explored areas stay in sight.</summary>
    NoFog = 1,

    /// <summary><c>hmnotech</c>: every requirement counts as met.</summary>
    NoTech = 2,

    /// <summary><c>hmgod</c>: the kingdom's units and buildings take no damage.</summary>
    God = 4,

    /// <summary><c>hmbuildozer</c>: construction, training and research finish at once.</summary>
    Buildozer = 8,
}

/// <summary>
/// Per-kingdom world state (GAME-13/14/17/19; ADRs 0102, 0103, 0106): match statistics (F4 scores and the statistics
/// file), the tavern loan, tribute rate limiting and cheat switches. Plain data, block-copied into snapshots and hashed
/// every tick.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct KingdomState
{
#pragma warning disable CA1051 // Plain data.
    /// <summary>Resources gathered and delivered, by resource type id.</summary>
    public ResourceAmounts Gathered;

    /// <summary>Total resources gathered (all types).</summary>
    public long GatheredTotal;

    /// <summary>Resource value (cost) of enemy units killed and buildings destroyed.</summary>
    public long KillValue;

    /// <summary>Resource value (cost) of own buildings completed and units trained.</summary>
    public long BuildValue;

    /// <summary>Profit earned by trade.</summary>
    public long TradeIncome;

    /// <summary>Resources sent as tribute.</summary>
    public long TributeSent;

    /// <summary>Resources received as tribute.</summary>
    public long TributeReceived;

    /// <summary>Units trained.</summary>
    public int UnitsTrained;

    /// <summary>Own units killed.</summary>
    public int UnitsLost;

    /// <summary>Enemy units killed.</summary>
    public int UnitsKilled;

    /// <summary>Own buildings completed.</summary>
    public int BuildingsBuilt;

    /// <summary>Own buildings destroyed (killed or burned down).</summary>
    public int BuildingsLost;

    /// <summary>Enemy buildings destroyed.</summary>
    public int BuildingsDestroyed;

    /// <summary>Buildings captured.</summary>
    public int BuildingsCaptured;

    /// <summary>Technologies researched.</summary>
    public int TechnologiesResearched;

    /// <summary>The tick of the last tribute sent (rate limit).</summary>
    public int LastTributeTick;

    /// <summary>Resource of the outstanding loan, or <see cref="NoLoan"/>.</summary>
    public int LoanResource;

    /// <summary>Amount of the outstanding loan (to pay back, interest included).</summary>
    public int LoanDue;

    /// <summary>How much the next loan may be.</summary>
    public int LoanLimit;

    /// <summary>From this tick on, <see cref="LoanLimit"/> is raised by the step after a repayment (-1 = no pending raise).</summary>
    public int LoanRaiseTick;

    /// <summary>The tick the kingdom was defeated or won (-1 while playing).</summary>
    public int OutcomeTick;

    /// <summary>Cheat switches.</summary>
    public CheatFlags Cheats;

    /// <summary>Tavern offers of <see cref="TavernPeriod"/> already hired (bit = offer slot; GAME-14).</summary>
    public byte TavernTaken;

    /// <summary>Padding, always 0.</summary>
    public short Reserved1;

    /// <summary>The tavern period <see cref="TavernTaken"/> belongs to (offers change every <c>world.tavernRefreshMs</c>).</summary>
    public int TavernPeriod;
#pragma warning restore CA1051

    /// <summary><see cref="LoanResource"/> when no loan is outstanding.</summary>
    public const int NoLoan = -1;
}
