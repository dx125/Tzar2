using System.Collections.Generic;

namespace Tzar2.Tools.Cli.Fidelity.Scenarios;

/// <summary>A point in game-screen pixels (the game's own resolution, e.g. 1024×768; origin top-left).</summary>
internal readonly record struct ScreenPoint(int X, int Y);

/// <summary>A rectangle in game-screen pixels.</summary>
internal readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public ScreenPoint Center => new(X + (Width / 2), Y + (Height / 2));

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
}

/// <summary>HUD geometry of the layout the session forces in <c>tzar.ini</c> (from <c>DATA/LAYOUT.INI</c>).</summary>
internal sealed record GameLayout(string Name, int Width, int Height, ScreenRect View, ScreenRect Minimap, ScreenPoint Park);

/// <summary>Timing defaults of a session (all overridable in <c>session.yaml</c>).</summary>
internal sealed record SessionTiming(
    int KeyHoldMs,
    int StepGapMs,
    int ChatOpenMs,
    int SnapshotTimeoutMs,
    int WindowTimeoutSeconds,
    int LoadSeconds,
    int CloseTimeoutSeconds);

/// <summary><c>tools/capture/session.yaml</c>: defaults shared by every scenario.</summary>
internal sealed record SessionConfig(
    IReadOnlyDictionary<string, string> Ini,
    IReadOnlyDictionary<string, string> Wrapper,
    IReadOnlyDictionary<string, string> Vars,
    GameLayout Layout,
    SessionTiming Timing);

/// <summary>One run of a scenario with its own variables and settings (e.g. one map or one unit type).</summary>
internal sealed record TrialSpec(
    string Name,
    IReadOnlyDictionary<string, string> Vars,
    IReadOnlyDictionary<string, string> Ini,
    IReadOnlyDictionary<string, string> Wrapper);

/// <summary>A capture scenario (<c>tools/capture/scenarios/*.yaml</c>, ADR 0030).</summary>
internal sealed record ScenarioSpec(
    string Id,
    string Title,
    IReadOnlyList<string> Resolves,
    int Priority,
    double Minutes,
    bool Default,
    bool Experimental,
    string Description,
    IReadOnlyDictionary<string, string> Ini,
    IReadOnlyDictionary<string, string> Wrapper,
    IReadOnlyList<string> Launch,
    int? LoadSeconds,
    IReadOnlyList<TrialSpec> Trials,
    IReadOnlyList<StepSpec> Steps,
    IReadOnlyList<string> Analysis,
    string FileName,
    string SourceText);

internal enum MouseButton
{
    Left,
    Right,
}

internal enum ScanMode
{
    /// <summary>All aligned values equal to <c>value</c> (a new scan, or a filter of <c>from</c>).</summary>
    Exact,

    /// <summary>Addresses of <c>from</c> whose value changed since that scan.</summary>
    Changed,

    /// <summary>Addresses of <c>from</c> whose value is unchanged since that scan.</summary>
    Unchanged,

    /// <summary>Addresses of <c>from</c> whose value decreased since that scan.</summary>
    Decreased,

    /// <summary>Addresses of <c>from</c> whose value increased since that scan.</summary>
    Increased,
}

/// <summary>A scenario step; <see cref="Line"/> is its line in the YAML file.</summary>
internal abstract record StepSpec(int Line)
{
    /// <summary>The step kind as written in YAML.</summary>
    public abstract string Kind { get; }
}

internal sealed record WaitStep(int Line, double Seconds) : StepSpec(Line)
{
    public override string Kind => "wait";
}

internal sealed record KeyStep(int Line, string Key, bool Ctrl, bool Shift, bool Alt, int HoldMs, int Repeat) : StepSpec(Line)
{
    public override string Kind => "key";
}

internal sealed record TextStep(int Line, string Text) : StepSpec(Line)
{
    public override string Kind => "text";
}

/// <summary>Enter, type the text, Enter: the in-game chat line (cheats are typed here).</summary>
internal sealed record ChatStep(int Line, string Text) : StepSpec(Line)
{
    public override string Kind => "chat";
}

internal sealed record ClickStep(int Line, ScreenPoint At, MouseButton Button, bool Double, bool Ctrl, bool Shift, bool Alt) : StepSpec(Line)
{
    public override string Kind => "click";
}

internal sealed record DragStep(int Line, ScreenPoint From, ScreenPoint To, MouseButton Button) : StepSpec(Line)
{
    public override string Kind => "drag";
}

/// <summary>Left click on the minimap at fractions (u, v) of its size: centres the camera there.</summary>
internal sealed record MinimapStep(int Line, double U, double V) : StepSpec(Line)
{
    public override string Kind => "minimap";
}

internal sealed record MoveStep(int Line, ScreenPoint At) : StepSpec(Line)
{
    public override string Kind => "move";
}

/// <summary>
/// The game's own F11 snapshot (lossless, the game's 16-bit surface) and/or a capture of the window client area
/// (what Windows displays, 32-bit). Tags are copied to the capture's metadata for the analysis.
/// </summary>
internal sealed record SnapshotStep(int Line, string Name, bool Game, bool Window, IReadOnlyDictionary<string, string> Tags) : StepSpec(Line)
{
    public override string Kind => "snapshot";
}

/// <summary>A series of window captures at a fixed interval, with high-resolution timestamps.</summary>
internal sealed record BurstStep(int Line, string Name, int Count, int IntervalMs, ScreenRect? Region, IReadOnlyDictionary<string, string> Tags) : StepSpec(Line)
{
    public override string Kind => "burst";
}

/// <summary>
/// Read-only scan of the game's writable memory for an integer value (ReadProcessMemory). <see cref="Value"/> is an
/// integer or a <c>${variable}</c> resolved per trial.
/// </summary>
internal sealed record MemScanStep(int Line, string Name, string? Value, int Width, string? From, ScanMode Mode, int MaxResults) : StepSpec(Line)
{
    public override string Kind => "memscan";
}

/// <summary>Samples the addresses of a scan in the background for a while (ReadProcessMemory).</summary>
internal sealed record MemSampleStep(int Line, string Name, string Scan, int MaxAddresses, int IntervalMs, double DurationSeconds) : StepSpec(Line)
{
    public override string Kind => "memsample";
}

internal sealed record NoteStep(int Line, string Text) : StepSpec(Line)
{
    public override string Kind => "note";
}
