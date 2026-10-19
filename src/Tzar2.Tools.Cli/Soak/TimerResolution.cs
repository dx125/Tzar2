using System;
using System.Runtime.InteropServices;

namespace Tzar2.Tools.Cli.Soak;

/// <summary>
/// Windows timer resolution for the soak's relay and peers (<c>timeBeginPeriod(1)</c>, ADR 0009): otherwise a 1 ms sleep
/// lasts 15.6 ms and the relay's 12.5 ms tick deadlines (4x speed) would close in bursts. The Godot client sets it itself.
/// </summary>
internal static partial class TimerResolution
{
    public static void Begin()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = TimeBeginPeriod(1);
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);
}
