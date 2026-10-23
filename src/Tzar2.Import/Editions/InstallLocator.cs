using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Tzar2.Import.Editions;

/// <summary>An original install found on this machine and where it was found (TDD §12.2).</summary>
public sealed record FoundInstall(string Path, string Source);

/// <summary>
/// Where to look for the original game (TDD §12.2, Windows row; ADR 0241): <c>TZAR2_ORIGIN</c>, GOG (Galaxy and the
/// offline installer, from the registry), Steam libraries (app 825730) and the usual folders. Every source is an input,
/// so tests and other platforms pass their own; <see cref="ForThisMachine"/> fills them from this machine.
/// </summary>
public sealed class InstallSearch
{
    /// <summary>The GOG product id of TZAR (the <c>goggame-1207659252.*</c> files of every GOG depot).</summary>
    public const string GogProductId = "1207659252";

    /// <summary>The Steam app id of TZAR.</summary>
    public const string SteamAppId = "825730";

    /// <summary>Folders named explicitly (the environment), checked first.</summary>
    public IReadOnlyList<string> Explicit { get; init; } = [];

    /// <summary>Install folders the GOG registry keys name.</summary>
    public IReadOnlyList<string> GogPaths { get; init; } = [];

    /// <summary>Steam installs (the folder with <c>steamapps</c>).</summary>
    public IReadOnlyList<string> SteamRoots { get; init; } = [];

    /// <summary>Usual install folders (GOG Games, Program Files, the CD's default).</summary>
    public IReadOnlyList<string> CommonPaths { get; init; } = [];

    /// <summary>The sources of this machine: the environment, and on Windows the registry and the usual folders.</summary>
    public static InstallSearch ForThisMachine()
    {
        var explicitPaths = new List<string>();
        foreach (string variable in new[] { "TZAR2_ORIGIN", "TZAR2_ORIGINAL_PATH" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                explicitPaths.Add(value);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return new InstallSearch { Explicit = explicitPaths };
        }

        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
        var common = new List<string>();
        var steam = new List<string>();
        if (WindowsRegistry.SteamPath() is { } steamPath)
        {
            steam.Add(steamPath);
        }

        foreach (string drive in drives)
        {
            common.Add(Path.Combine(drive, "GOG Games", "Tzar"));
            common.Add(Path.Combine(drive, "GOG Games", "TZAR - The Burden of the Crown"));
            common.Add(Path.Combine(drive, "Games", "Tzar"));
        }

        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            string root = Environment.GetFolderPath(folder);
            if (root.Length > 0)
            {
                common.Add(Path.Combine(root, "GOG Galaxy", "Games", "Tzar"));
                common.Add(Path.Combine(root, "GOG.com", "Tzar"));
                common.Add(Path.Combine(root, "Tzar"));
                common.Add(Path.Combine(root, "HMM", "Tzar"));
                steam.Add(Path.Combine(root, "Steam"));
            }
        }

        return new InstallSearch
        {
            Explicit = explicitPaths,
            GogPaths = WindowsRegistry.GogPaths(),
            SteamRoots = steam,
            CommonPaths = common,
        };
    }
}

/// <summary>Finds original installs (TDD §12.2): candidates from an <see cref="InstallSearch"/> that hold the game.</summary>
public static class InstallLocator
{
    /// <summary>Executables one of which every edition and repack of the original has at its root.</summary>
    public static readonly IReadOnlyList<string> GameExecutables = ["Tzar.exe", "TzarHD.exe"];

    /// <summary>The installs found, in the order of the sources, without duplicates (by full path, case-insensitive).</summary>
    public static IReadOnlyList<FoundInstall> Find(InstallSearch search)
    {
        ArgumentNullException.ThrowIfNull(search);
        var found = new List<FoundInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Consider(string? path, string source)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (IsInstall(full) && seen.Add(full))
            {
                found.Add(new FoundInstall(full, source));
            }
        }

        foreach (string path in search.Explicit)
        {
            Consider(path, "TZAR2_ORIGIN");
        }

        foreach (string path in search.GogPaths)
        {
            Consider(path, "GOG");
        }

        foreach (string root in search.SteamRoots)
        {
            foreach (string path in SteamInstalls(root))
            {
                Consider(path, "Steam");
            }
        }

        foreach (string path in search.CommonPaths)
        {
            Consider(path, "folder");
        }

        return found;
    }

    /// <summary>Whether <paramref name="folder"/> holds the original game (one of <see cref="GameExecutables"/> at its root).</summary>
    public static bool IsInstall(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(folder)
                .Select(Path.GetFileName)
                .Any(name => GameExecutables.Any(exe => string.Equals(exe, name, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// TZAR's folders in the Steam install <paramref name="steamRoot"/> and its other libraries
    /// (<c>steamapps/libraryfolders.vdf</c>), from <c>appmanifest_825730.acf</c>'s <c>installdir</c>.
    /// </summary>
    public static IReadOnlyList<string> SteamInstalls(string steamRoot)
    {
        var libraries = new List<string> { steamRoot };
        string folders = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (TryRead(folders) is { } vdf)
        {
            libraries.AddRange(VdfValues(vdf, "path"));
        }

        var installs = new List<string>();
        foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string manifest = Path.Combine(library, "steamapps", $"appmanifest_{InstallSearch.SteamAppId}.acf");
            if (TryRead(manifest) is { } acf && VdfValues(acf, "installdir").FirstOrDefault() is { Length: > 0 } dir)
            {
                installs.Add(Path.Combine(library, "steamapps", "common", dir));
            }
        }

        return installs;
    }

    /// <summary>The values of every <c>"key" "value"</c> pair named <paramref name="key"/> in Valve's KeyValues text.</summary>
    public static IEnumerable<string> VdfValues(string text, string key)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (Match match in Regex.Matches(text, "\"" + Regex.Escape(key) + "\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            yield return match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal);
        }
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The registry keys GOG and Steam write on Windows.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsRegistry
{
    public static IReadOnlyList<string> GogPaths()
    {
        var paths = new List<string>();
        foreach (string key in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games\", @"SOFTWARE\GOG.com\Games\" })
        {
            if (Read(Microsoft.Win32.Registry.LocalMachine, key + InstallSearch.GogProductId, "path") is { } path)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    public static string? SteamPath()
        => Read(Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
            ?? Read(Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");

    private static string? Read(Microsoft.Win32.RegistryKey hive, string key, string name)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? opened = hive.OpenSubKey(key);
            return opened?.GetValue(name) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
