using System;
using System.Collections.Generic;

namespace Tzar2.Formats.Maps;

/// <summary>
/// The original's built-in object class table (docs/formats/wmp.md §4.2). Map files store a class as
/// <c>(group &lt;&lt; 16) | (index − groupStart)</c>; trigger and object records store the plain class index. The names
/// equal the <c>class =</c> values of <c>DATA/OBJECTS/*.INI</c>. The order is a fact of the format (an enumeration),
/// recovered from the editor and checked against all maps of the reference build.
/// </summary>
public static class LegacyClasses
{
    /// <summary>Class names by index (index 0 = none).</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "none", "peon", "trader", "footman", "archer", "maceman", "longbowman", "pikeman", "knight", "samurai",
        "horse_archer", "crusader", "monk", "ninja", "spy", "bat", "troll", "mage", "priest", "catapult",
        "hcatapult", "ballista", "carpet", "galleon", "hulk", "xebec", "bear", "dwarf", "ork", "enichar",
        "jinn", "ghost", "dragon", "jihad_warrior", "horse_samurai", "heavy_cavalry", "skeleton", "chariot", "messiah", "tower_stone",
        "tower_wood", "tower_wood2", "tower_stone2", "tower_rocket", "castle", "barrack", "lumber", "farm", "tavern", "church",
        "stable", "storage", "outpost", "market", "harbor", "cathedral", "academy", "shipyard", "blacksmith", "workshop",
        "magetower", "wall_stone", "wall_wood", "field", "bridge", "tcguild", "house", "gate_wood", "gate_stone", "magicworkshop",
        "shaolinn", "mosque", "magicgateway", "evilcastle", "arrow", "cursor", "res_gold", "fog_revealer", "res_stone", "item",
        "resitem", "item_plusplus", "animation", "ruin", "cloud", "open_next_map", "not", "debris", "not", "explosion",
        "command_move", "command_attack", "command_harvest", "command_capture", "command_pickitem", "command_patrol", "cannon", "command_default", "fire_rain", "command_repair",
        "command_guard", "lightning", "treeselection", "item_scroll", "item_magic", "magic", "item_health", "ground_object", "ground_patch", "command_spell",
        "cow", "fish", "bird", "command_unload", "command_trade", "fishpatch", "crusadeanchor", "fakebuilding", "fakeresource", "eagle",
        "bolt", "flame_bolt", "visualobject", "command_explore", "freeze_bolt", "item_soul", "command_murder", "command_impersonate", "command_bribe", "command_board",
        "whirlpool", "sound_object", "flame_ball", "dummy", "rocket_arrow", "item_flag", "fishing_boat", "command_fishing", "command_attackground", "enemy_ground",
        "fakewall", "fakeitem", "rally_flag", "fakegate",
    ];

    /// <summary>First class index of each group 1–6 (index 0 unused).</summary>
    private static readonly int[] GroupStart = [0, 0, 1, 3, 39, 44, 74];

    /// <summary>Decodes a stored class (group &lt;&lt; 16 | offset) to a class index, or −1 if out of range.</summary>
    public static int Decode(uint encoded)
    {
        int group = (int)(encoded >> 16);
        int offset = (int)(encoded & 0xFFFF);
        if (group < 1 || group >= GroupStart.Length)
        {
            return -1;
        }

        int index = GroupStart[group] + offset;
        int end = group + 1 < GroupStart.Length ? GroupStart[group + 1] : Names.Count;
        return index < end ? index : -1;
    }

    /// <summary>Encodes a class index the way map files store it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is not in the table.</exception>
    public static uint Encode(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Names.Count);
        int group = 1;
        while (group + 1 < GroupStart.Length && GroupStart[group + 1] <= index)
        {
            group++;
        }

        return ((uint)group << 16) | (uint)(index - GroupStart[group]);
    }

    /// <summary>The name of class <paramref name="index"/>, or <c>#index</c> when unknown.</summary>
    public static string NameOf(int index) => (uint)index < (uint)Names.Count ? Names[index] : "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The index of class <paramref name="name"/> (case-insensitive), or −1.</summary>
    public static int IndexOf(string name)
    {
        for (int i = 0; i < Names.Count; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
