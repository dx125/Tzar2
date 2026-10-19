using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using static Tzar2.Tools.Cli.Fidelity.Capture.Windows.NativeMethods;

namespace Tzar2.Tools.Cli.Fidelity.Capture.Windows;

/// <summary>
/// Reads, without changing anything, what <see cref="MachineSettings"/> needs from this PC: the DirectDraw, waveOut,
/// DirectSound and midiOut device counts the game compares with its <c>tzar.ini</c>, the processor clock, and the
/// compatibility databases registered for <c>Tzar.exe</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class DeviceProbe
{
    private const string CustomShimsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Custom\";

    public static MachineDevices Detect() => new(
        Try(() =>
        {
            int count = 0;
            return DirectDrawEnumerateA(&CountDirectDraw, (nint)(&count)) == 0 ? count : (int?)null;
        }),
        Try(() => (int)WaveOutGetNumDevs()),
        Try(() =>
        {
            int count = 0;
            return DirectSoundEnumerateW(&CountDirectSound, (nint)(&count)) == 0 ? count : (int?)null;
        }),
        Try(() => (int)MidiOutGetNumDevs()),
        Try(() => Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("~MHz") is int mhz ? mhz : null));

    /// <summary>Ids of the shim databases (<c>{guid}</c>) registered for <paramref name="exeName"/> in either registry view.</summary>
    public static IReadOnlyList<string> CompatDatabases(string exeName)
    {
        var ids = new List<string>();
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? key = root.OpenSubKey(CustomShimsKey + exeName);
                if (key is null)
                {
                    continue;
                }

                // Value names are "{guid}.sdb" (the value is the install time).
                ids.AddRange(key.GetValueNames()
                    .Select(n => n.EndsWith(".sdb", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
                    .Where(n => n.StartsWith('{') && n.EndsWith('}')));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                // Not readable: nothing to warn about.
            }
        }

        return [.. ids.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static int? Try(Func<int?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [UnmanagedCallersOnly]
    private static int CountDirectDraw(Guid* guid, byte* description, byte* name, nint context)
    {
        (*(int*)context)++;
        return 1;
    }

    [UnmanagedCallersOnly]
    private static int CountDirectSound(Guid* guid, char* description, char* module, nint context)
    {
        (*(int*)context)++;
        return 1;
    }
}
