namespace Tzar2.Core.Diagnostics;

/// <summary>How much an area records: each level includes the ones above it (ADR 0176).</summary>
public enum LogLevel : byte
{
    /// <summary>Nothing.</summary>
    Off = 0,

    /// <summary>Failures: exceptions, broken invariants, files that cannot be read.</summary>
    Error = 1,

    /// <summary>Things that went wrong without failing: rejected commands, units that cannot find a path.</summary>
    Warn = 2,

    /// <summary>The story of a match: setup, periodic summaries, deaths, buildings, research, victory.</summary>
    Info = 3,

    /// <summary>Every command, every gameplay event, AI decisions, trigger firings.</summary>
    Debug = 4,

    /// <summary>Everything, including each hit and each tick's hash.</summary>
    Verbose = 5,
}

/// <summary>What a trace record is about; each area has its own level (ADR 0176).</summary>
public enum TraceArea : byte
{
    /// <summary>Match setup and lifecycle: start, pause, speed, save, load, end.</summary>
    Session = 0,

    /// <summary>Commands entering the simulation (players and AI) and their rejections.</summary>
    Commands = 1,

    /// <summary>Gameplay events of the simulation (deaths, buildings, production, gathering...).</summary>
    Events = 2,

    /// <summary>Simulation internals (ticks, hashes, pathing problems).</summary>
    Sim = 3,

    /// <summary>The legacy AI: script passes, executor decisions.</summary>
    Ai = 4,

    /// <summary>Map triggers, conversations and campaign progress.</summary>
    Triggers = 5,

    /// <summary>Multiplayer: connections, lockstep, desyncs.</summary>
    Net = 6,

    /// <summary>The client: screens, input, rendering, assets, audio.</summary>
    Client = 7,

    /// <summary>Content: packages, rules, imports.</summary>
    Content = 8,
}

/// <summary>
/// Where diagnostic records go (ADR 0176). Deterministic code (Sim, AI, Scripting) writes through this interface and never
/// reads anything back, so tracing cannot change a match; the host implements it with a file. Callers ask
/// <see cref="IsEnabled"/> before they build a message, so a disabled area costs one call and allocates nothing.
/// </summary>
public interface ITraceSink
{
    /// <summary>Whether records of <paramref name="area"/> at <paramref name="level"/> are kept.</summary>
    bool IsEnabled(TraceArea area, LogLevel level);

    /// <summary>Keeps a record; <paramref name="tick"/> is the simulation tick, or -1 outside a match.</summary>
    void Write(TraceArea area, LogLevel level, int tick, string message);
}

/// <summary>A sink that keeps nothing: the default everywhere a host has not attached one.</summary>
public sealed class NullTraceSink : ITraceSink
{
    /// <summary>The instance.</summary>
    public static readonly NullTraceSink Instance = new();

    private NullTraceSink()
    {
    }

    /// <inheritdoc />
    public bool IsEnabled(TraceArea area, LogLevel level) => false;

    /// <inheritdoc />
    public void Write(TraceArea area, LogLevel level, int tick, string message)
    {
    }
}
