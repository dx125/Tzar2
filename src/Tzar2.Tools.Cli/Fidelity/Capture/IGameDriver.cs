using System;
using System.Collections.Generic;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;

namespace Tzar2.Tools.Cli.Fidelity.Capture;

/// <summary>
/// Black-box access to one running game process (ADR 0030): start it, find its window, send input, capture the
/// window, read (never write) its memory. The Windows implementation is <see cref="Windows.Win32GameDriver"/>; tests
/// use a fake. The game executable is never modified.
/// </summary>
internal interface IGameDriver : IDisposable
{
    /// <summary>Starts the game. Throws when the process cannot be started.</summary>
    void Launch(string exePath, string workingDirectory, IReadOnlyList<string> arguments);

    bool HasExited { get; }

    int? ExitCode { get; }

    /// <summary>Waits until the game shows a top-level window; false on timeout or exit.</summary>
    bool WaitForWindow(TimeSpan timeout);

    /// <summary>Client-area size of the game window in physical pixels (0×0 before a window exists).</summary>
    (int Width, int Height) ClientSize { get; }

    /// <summary>Brings the game window to the foreground (best effort; returns whether it is now the foreground).</summary>
    bool Focus();

    /// <summary>Presses or releases a key (Windows virtual-key code).</summary>
    void Key(ushort virtualKey, bool down);

    /// <summary>Types printable ASCII text as key strokes.</summary>
    void Type(string text);

    /// <summary>Moves the mouse to a point of the game screen (client coordinates).</summary>
    void MouseMove(ScreenPoint point);

    void MouseButton(MouseButton button, bool down);

    /// <summary>Captures the window client area (what Windows displays), or null when that is impossible.</summary>
    CaptureImage? CaptureWindow();

    /// <summary>Read-only memory access, or null when the process cannot be opened for reading.</summary>
    IMemoryReader? Memory { get; }

    /// <summary>Asks the game to close and terminates it after the timeout.</summary>
    void Close(TimeSpan timeout);
}

/// <summary>A committed, writable region of the game's address space.</summary>
internal readonly record struct MemoryRegion(ulong BaseAddress, long Size);

/// <summary>Read-only access to another process's memory (ReadProcessMemory). Never writes.</summary>
internal interface IMemoryReader
{
    /// <summary>The committed private/mapped regions that are readable and writable (where game state lives).</summary>
    IReadOnlyList<MemoryRegion> WritableRegions();

    /// <summary>Reads memory; returns the number of bytes read (0 when the range is not readable).</summary>
    int Read(ulong address, Span<byte> buffer);
}

/// <summary>Files and settings of the game copy a session runs against (see <see cref="WorkCopy"/>).</summary>
internal interface IGameEnvironment
{
    string ExePath { get; }

    string WorkingDirectory { get; }

    /// <summary>Folders where the game writes its F11 snapshots (watched for new files).</summary>
    IReadOnlyList<string> SnapshotDirectories { get; }

    /// <summary>Applies <c>tzar.ini</c> (SECTION.KEY) and wrapper (<c>ddraw.ini</c>) settings before a launch.</summary>
    void ApplySettings(IReadOnlyDictionary<string, string> ini, IReadOnlyDictionary<string, string> wrapper);

    /// <summary>A one-line description of the renderer set-up (for provenance).</summary>
    string RendererDescription { get; }
}
