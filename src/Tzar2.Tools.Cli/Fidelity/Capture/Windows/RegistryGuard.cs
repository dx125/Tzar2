using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Tzar2.Tools.Cli.Fidelity.Capture.Windows;

/// <summary>
/// Temporarily sets per-user (HKCU) registry values for the session and restores the previous state on dispose:
/// the game's install path (<c>Software\Haemimont\Tzar\Path</c>, written by the GOG installer) so the game reads the
/// work copy, and, for the native renderer, a compatibility layer for the work copy's executable. No administrator
/// rights are needed and nothing outside HKCU is touched.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryGuard : IDisposable
{
    public const string GameKey = @"Software\Haemimont\Tzar";
    public const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    private readonly List<(string Key, string Name, object? Previous)> _saved = [];

    public void Set(string key, string name, string value)
    {
        using RegistryKey k = Registry.CurrentUser.CreateSubKey(key, writable: true);
        _saved.Add((key, name, k.GetValue(name)));
        k.SetValue(name, value, RegistryValueKind.String);
    }

    public static string? Get(string key, string name)
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name) as string;
    }

    public void Dispose()
    {
        for (int i = _saved.Count - 1; i >= 0; i--)
        {
            (string key, string name, object? previous) = _saved[i];
            try
            {
                using RegistryKey k = Registry.CurrentUser.CreateSubKey(key, writable: true);
                if (previous is null)
                {
                    k.DeleteValue(name, throwOnMissingValue: false);
                }
                else
                {
                    k.SetValue(name, previous);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                Console.Error.WriteLine($"warning: could not restore HKCU\\{key}\\{name}: {ex.Message}");
            }
        }

        _saved.Clear();
    }
}
