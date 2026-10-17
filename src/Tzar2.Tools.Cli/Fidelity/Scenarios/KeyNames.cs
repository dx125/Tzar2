using System;
using System.Collections.Generic;

namespace Tzar2.Tools.Cli.Fidelity.Scenarios;

/// <summary>Key names usable in scenario <c>key</c> steps and their Windows virtual-key codes.</summary>
internal static class KeyNames
{
    private static readonly Dictionary<string, ushort> Codes = Build();

    public static IEnumerable<string> All => Codes.Keys;

    public static bool TryGet(string name, out ushort virtualKey) => Codes.TryGetValue(name, out virtualKey);

    public static ushort Get(string name) => Codes.TryGetValue(name, out ushort vk)
        ? vk
        : throw new ArgumentException($"Unknown key '{name}'.", nameof(name));

    /// <summary>Whether the key needs <c>KEYEVENTF_EXTENDEDKEY</c> (navigation cluster keys).</summary>
    public static bool IsExtended(ushort vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E;

    private static Dictionary<string, ushort> Build()
    {
        var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["Backspace"] = 0x08,
            ["Tab"] = 0x09,
            ["Enter"] = 0x0D,
            ["Return"] = 0x0D,
            ["Shift"] = 0x10,
            ["Ctrl"] = 0x11,
            ["Alt"] = 0x12,
            ["Pause"] = 0x13,
            ["Escape"] = 0x1B,
            ["Esc"] = 0x1B,
            ["Space"] = 0x20,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["End"] = 0x23,
            ["Home"] = 0x24,
            ["Left"] = 0x25,
            ["Up"] = 0x26,
            ["Right"] = 0x27,
            ["Down"] = 0x28,
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,
            ["NumPlus"] = 0x6B,
            ["NumMinus"] = 0x6D,
            ["Plus"] = 0xBB,
            ["Minus"] = 0xBD,
            ["Comma"] = 0xBC,
            ["Period"] = 0xBE,
            ["Slash"] = 0xBF,
            ["Backquote"] = 0xC0,
            ["Backslash"] = 0xDC,
        };
        for (int i = 0; i < 26; i++)
        {
            map[((char)('A' + i)).ToString()] = (ushort)('A' + i);
        }

        for (int i = 0; i < 10; i++)
        {
            map[((char)('0' + i)).ToString()] = (ushort)('0' + i);
        }

        for (int i = 1; i <= 12; i++)
        {
            map["F" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = (ushort)(0x70 + i - 1);
        }

        return map;
    }
}
