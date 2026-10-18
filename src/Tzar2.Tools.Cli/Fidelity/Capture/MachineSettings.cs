using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Tzar2.Tools.Cli.Fidelity.Capture;

/// <summary>What the operator's PC reports (Windows: <c>DeviceProbe</c>); null = could not be determined.</summary>
internal sealed record MachineDevices(int? DirectDrawDevices, int? WaveOutDevices, int? DirectSoundDevices, int? MidiOutDevices, int? CpuMhz);

/// <summary>
/// The machine-specific <c>tzar.ini</c> values the game checks at start-up (first capture session, 2026-10-18,
/// Windows 11 25H2). <c>Tzar.exe</c> compares <c>VIDEO.DIRECTDRAW_DEVICESNUMBER</c> and the three
/// <c>SOUND.SOUNDDEVICE*NUMBER</c> values with what it enumerates; on any mismatch it reports an invalid configuration,
/// starts its configuration program and exits. GOG ships its packaging PC's values, so they must be detected on the
/// operator's PC (they also change when, say, Bluetooth headphones connect) and are never committed.
/// <para>The rule below reproduces what the game's own <c>Setup.exe</c> wrote on the operator's PC (2 waveOut, 3
/// DirectSound including the primary driver, 1 midiOut → 3 / 3 / 2; DirectDraw 2 with one monitor) and is consistent
/// with GOG's shipped 2 / 5 / 5 / 2: waveOut and midiOut count their mapper as an extra device, DirectSound and
/// DirectDraw count every entry of their enumeration (primary driver included).</para>
/// </summary>
internal static class MachineSettings
{
    public const string DirectDrawKey = "VIDEO.DIRECTDRAW_DEVICESNUMBER";
    public const string WaveKey = "SOUND.SOUNDDEVICENUMBER";
    public const string DirectSoundKey = "SOUND.SOUNDDEVICEDXNUMBER";
    public const string MidiKey = "SOUND.SOUNDDEVICEMIDINUMBER";
    public const string CpuKey = "SOUND.CPUSPEED";

    /// <summary>The <c>tzar.ini</c> values for <paramref name="devices"/> (only those that were detected).</summary>
    public static IReadOnlyDictionary<string, string> IniFor(MachineDevices devices)
    {
        var ini = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Put(string key, int? value)
        {
            if (value is { } v)
            {
                ini[key] = v.ToString(CultureInfo.InvariantCulture);
            }
        }

        Put(DirectDrawKey, devices.DirectDrawDevices);
        Put(WaveKey, devices.WaveOutDevices + 1);
        Put(DirectSoundKey, devices.DirectSoundDevices);
        Put(MidiKey, devices.MidiOutDevices + 1);
        Put(CpuKey, devices.CpuMhz);
        return ini;
    }

    /// <summary>
    /// The session's <c>ini</c> with the detected values underneath: anything the session file (or the operator's local
    /// copy of it) sets explicitly wins.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Underlay(IReadOnlyDictionary<string, string> session, IReadOnlyDictionary<string, string> detected)
    {
        var merged = new Dictionary<string, string>(detected, StringComparer.Ordinal);
        foreach ((string key, string value) in session)
        {
            merged[key] = value;
        }

        return merged;
    }

    /// <summary>
    /// The warning for a compatibility database registered for <c>Tzar.exe</c> (HKLM, by the GOG installer): it applies to
    /// the work copy too (it matches the file name) and made the game crash in its sound initialisation on Windows 11,
    /// even with sound off. The harness never uninstalls it; the operator decides.
    /// </summary>
    public static IReadOnlyList<string> CompatDatabaseWarning(IEnumerable<string> databaseIds, string install)
    {
        string[] ids = [.. databaseIds.Distinct(StringComparer.OrdinalIgnoreCase)];
        if (ids.Length == 0)
        {
            return [];
        }

        var lines = new List<string>
        {
            "A compatibility database is registered for Tzar.exe (HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Custom\\Tzar.exe).",
            "It also applies to the session's work copy; on Windows 11 the game then crashed while initialising sound (even with sound off).",
            "If the game crashes at start-up, remove it for the session in an administrator terminal:",
        };
        lines.AddRange(ids.Select(id => $"    sdbinst -u -g {id}"));
        lines.Add("and reinstall it afterwards (the GOG install keeps the file):");
        lines.Add($"    sdbinst \"{System.IO.Path.Combine(install, "goggame.sdb")}\"");
        return lines;
    }
}
