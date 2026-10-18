using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tzar2.Formats.Maps;
using Tzar2.Formats.Text;

namespace Tzar2.Tests.Shared;

/// <summary>
/// Test-only writer of legacy maps (<c>.wmp</c>) and campaigns (<c>.wcm</c>) built from docs/formats/wmp.md with
/// invented values (REQ-LEGAL-03: no byte of the original game is used).
/// </summary>
internal sealed class SyntheticMap
{
    public string Name { get; set; } = "testmap";

    public int Side { get; set; } = 16;

    public string Title { get; set; } = "Test Map";

    public string Author { get; set; } = "Tester";

    public string TerrainSet { get; set; } = "grass";

    public string Description { get; set; } = "A synthetic map.";

    public int PopulationLimit { get; set; } = 150;

    public int Difficulty { get; set; } = 2;

    public int Players { get; set; } = 2;

    public CodePage CodePage { get; set; } = CodePage.Windows1252;

    /// <summary>Tile words; defaults to solid terrain 1 (code 9).</summary>
    public ushort[]? Tiles { get; set; }

    public SyntheticKingdom[] Kingdoms { get; } = new SyntheticKingdom[8];

    /// <summary>8 × 8 relation flags.</summary>
    public uint[] Diplomacy { get; } = new uint[64];

    public List<SyntheticObject> Objects { get; } = [];

    public List<(ushort Id, string Title, string Icon)> Extras { get; } = [];

    public List<(string Name, int Type, byte[] Payload)> Events { get; } = [];

    public List<(string Name, int Type, byte[] Payload)> Actions { get; } = [];

    public List<(string Name, string Condition, string[] Actions)> Rules { get; } = [];

    public List<(string Name, List<SyntheticPhrase> Phrases)> Conversations { get; } = [];

    public string[][] InitialResearches { get; } = new string[8][];

    public string[][] ForbiddenUnits { get; } = new string[8][];

    public List<string> Music { get; } = [];

    public List<(string Path, byte[] Data)> Files { get; } = [];

    /// <summary>Extra map members (name after the map prefix → bytes).</summary>
    public List<(string Part, byte[] Data)> ExtraParts { get; } = [];

    public SyntheticMap()
    {
        for (int k = 0; k < 8; k++)
        {
            Kingdoms[k] = new SyntheticKingdom { Name = "Kingdom " + (k + 1), Tzar = "Tzar " + (k + 1), Active = k < 2, Race = (byte)((k % 3) + 1) };
            InitialResearches[k] = [];
            ForbiddenUnits[k] = [];
            for (int j = 0; j < 8; j++)
            {
                Diplomacy[(k * 8) + j] = k == j ? 0x48u : 0x10u;
            }
        }
    }

    /// <summary>A solid tile of terrain-set index <paramref name="terrain"/>, with optional tree and cliff.</summary>
    public static ushort Solid(int terrain, int tree = 0, bool cliff = false) => (ushort)(((terrain * 9) << 4) | (cliff ? 0x400 : 0) | (tree << 11));

    /// <summary>A transition tile between <paramref name="a"/> (the set corners) and <paramref name="b"/>.</summary>
    public static ushort Transition(int a, int b, int cornerMask) => (ushort)((((a * 8) + b) << 4) | (cornerMask - 1));

    public byte[] Build()
    {
        var files = new List<(string, byte[])>
        {
            ("MAPS/" + Name + ".header", Header()),
            ("MAPS/" + Name + ".description", Bytes(Description, true)),
            ("MAPS/" + Name + ".kingdominfo", KingdomInfo()),
            ("MAPS/" + Name + ".diplomacy", DiplomacyBytes()),
            ("MAPS/" + Name + ".tiles", TileBytes()),
            ("MAPS/" + Name + ".objects", ObjectBytes()),
            ("MAPS/" + Name + ".conv", ConvBytes()),
            ("MAPS/" + Name + ".forbidden.buildings", NameLists(new string[8][])),
            ("MAPS/" + Name + ".forbidden.units", NameLists(ForbiddenUnits)),
            ("MAPS/" + Name + ".forbidden.researches", NameLists(new string[8][])),
            ("MAPS/" + Name + ".forbidden.upgrades", NameLists(new string[8][])),
            ("MAPS/" + Name + ".initial.researches", NameLists(InitialResearches)),
            ("MAPS/maps.ini", Encoding.ASCII.GetBytes("[MAPS]\r\n" + Name + "=\r\n")),
            ("MAPS/midis.ini", MidisIni()),
        };
        if (Extras.Count > 0)
        {
            files.Add(("MAPS/" + Name.ToLowerInvariant() + ".objext", ExtraBytes()));
        }

        if (Events.Count > 0 || Actions.Count > 0 || Rules.Count > 0)
        {
            files.Add(("MAPS/" + Name.ToLowerInvariant() + ".events", Records(Events)));
            files.Add(("MAPS/" + Name.ToLowerInvariant() + ".actions", Records(Actions)));
            files.Add(("MAPS/" + Name.ToLowerInvariant() + ".rules", RuleBytes()));
            files.Add(("MAPS/" + Name.ToLowerInvariant() + ".defeat.messages", new byte[16]));
        }

        foreach ((string part, byte[] data) in ExtraParts)
        {
            files.Add(("MAPS/" + Name + "." + part, data));
        }

        files.AddRange(Files);
        return SyntheticFormats.Hpfs(files);
    }

    /// <summary>A campaign holding <paramref name="maps"/> (name, entry flag, map image).</summary>
    public static byte[] Campaign(string title, string description, IReadOnlyList<(string Name, bool Entry, byte[] Map)> maps, CodePage codePage = CodePage.Windows1252)
    {
        var ini = new StringBuilder("[maps]\r\n");
        var files = new List<(string, byte[])>();
        foreach ((string name, bool entry, byte[] map) in maps)
        {
            ini.Append(name).Append('=').Append(entry ? "yes" : "no").Append("\r\n");
            files.Add(("MAPS/" + name + ".wmp", map));
        }

        byte[] desc = LegacyText.Encode(title + "\r\n" + description, codePage);
        files.Insert(0, ("MAPS/campaign description.ini", [.. desc, 0]));
        files.Insert(1, ("MAPS/maps.ini", LegacyText.Encode(ini.ToString(), codePage)));
        return SyntheticFormats.Hpfs(files, truncate: 100);
    }

    private byte[] Bytes(string text, bool terminate) => terminate ? [.. LegacyText.Encode(text, CodePage), 0] : LegacyText.Encode(text, CodePage);

    private void Fixed(byte[] target, int offset, int length, string text)
    {
        byte[] b = LegacyText.Encode(text, CodePage);
        Array.Copy(b, 0, target, offset, Math.Min(b.Length, length - 1));
    }

    private byte[] Header()
    {
        byte[] h = new byte[LegacyMapReader.HeaderSize];
        W32(h, 0, LegacyMapReader.Magic);
        W16(h, 6, Side);
        W32(h, 8, (uint)Objects.Count);
        W16(h, 0x0C, Rules.Count);
        W16(h, 0x0E, Actions.Count);
        W16(h, 0x10, Events.Count);
        W32(h, 0x16, (uint)(Extras.Count * LegacyMapReader.ObjectExtraSize));
        W32(h, 0x1A, (uint)(Side * Side * 2));
        h[0x4C] = 2;
        h[0x4D] = (byte)Players;
        h[0x50] = (byte)Difficulty;
        h[0x51] = 8;
        Fixed(h, 0x56, 64, Title);
        Fixed(h, 0x96, 64, Author);
        Fixed(h, 0xD6, 64, TerrainSet);
        W32(h, 0x116, (uint)Bytes(Description, true).Length);
        W32(h, 0x122, (uint)ConvBytes().Length);
        W16(h, 0x128, PopulationLimit);
        W16(h, 0x12A, 30);
        W16(h, 0x12E, 100);
        W32(h, 0x132, 7);
        return h;
    }

    private byte[] KingdomInfo()
    {
        byte[] b = new byte[LegacyMapReader.KingdomSize * 8];
        for (int k = 0; k < 8; k++)
        {
            SyntheticKingdom s = Kingdoms[k];
            int o = k * LegacyMapReader.KingdomSize;
            Fixed(b, o, 32, s.Name);
            Fixed(b, o + 0x20, 32, s.Tzar);
            b[o + 0x40] = s.Active ? (byte)1 : (byte)0;
            b[o + 0x42] = (byte)(k + 1);
            b[o + 0x43] = (byte)(k + 1);
            for (int r = 0; r < 4; r++)
            {
                W32(b, o + 0x44 + (4 * r), (uint)s.Resources[r]);
            }

            W16(b, o + 0x54, s.StartX < 0 ? 0xFFFF : s.StartX);
            W16(b, o + 0x56, s.StartY < 0 ? 0xFFFF : s.StartY);
            b[o + 0x58] = s.Race;
            Fixed(b, o + 0x59, 32, s.AiScript);
            b[o + 0x79] = s.AiFlags;
            b[o + 0x7A] = s.PlayerType;
        }

        return b;
    }

    private byte[] DiplomacyBytes()
    {
        byte[] b = new byte[256];
        for (int i = 0; i < 64; i++)
        {
            W32(b, i * 4, Diplomacy[i]);
        }

        return b;
    }

    private byte[] TileBytes()
    {
        ushort[] tiles = Tiles ?? FilledTiles();
        byte[] b = new byte[tiles.Length * 2];
        for (int i = 0; i < tiles.Length; i++)
        {
            W16(b, i * 2, tiles[i]);
        }

        return b;
    }

    private ushort[] FilledTiles()
    {
        var t = new ushort[Side * Side];
        Array.Fill(t, Solid(1));
        return t;
    }

    private byte[] ObjectBytes()
    {
        using var ms = new MemoryStream();
        foreach (SyntheticObject o in Objects)
        {
            byte[] p = o.Payload(this);
            ms.WriteByte(0);
            byte[] head = new byte[8];
            W32(head, 0, LegacyClasses.Encode(o.ClassIndex));
            W32(head, 4, (uint)p.Length);
            ms.Write(head);
            ms.Write(p);
        }

        return ms.ToArray();
    }

    private byte[] ExtraBytes()
    {
        byte[] b = new byte[Extras.Count * LegacyMapReader.ObjectExtraSize];
        for (int i = 0; i < Extras.Count; i++)
        {
            int o = i * LegacyMapReader.ObjectExtraSize;
            W16(b, o, Extras[i].Id);
            Fixed(b, o + 2, 128, Extras[i].Title);
            Fixed(b, o + 130, 128, Extras[i].Icon);
        }

        return b;
    }

    private byte[] Records(List<(string Name, int Type, byte[] Payload)> records)
    {
        using var ms = new MemoryStream();
        foreach ((string name, int type, byte[] payload) in records)
        {
            byte[] head = new byte[0x24];
            Fixed(head, 0, 0x20, name);
            W16(head, 0x20, type);
            W16(head, 0x22, payload.Length);
            ms.Write(head);
            ms.Write(payload);
        }

        return ms.ToArray();
    }

    private byte[] RuleBytes()
    {
        using var ms = new MemoryStream();
        foreach ((string name, string condition, string[] actions) in Rules)
        {
            using var data = new MemoryStream();
            data.Write(Bytes(condition, true));
            foreach (string a in actions)
            {
                data.Write(Bytes(a, true));
            }

            byte[] head = new byte[0x24];
            Fixed(head, 0, 0x20, name);
            W16(head, 0x20, actions.Length);
            W16(head, 0x22, (int)data.Length);
            ms.Write(head);
            ms.Write(data.ToArray());
        }

        return ms.ToArray();
    }

    private byte[] ConvBytes()
    {
        var p = new SyntheticPayload(CodePage).U16(Conversations.Count);
        foreach ((string name, List<SyntheticPhrase> phrases) in Conversations)
        {
            p.Str(name).U16(phrases.Count);
            foreach (SyntheticPhrase ph in phrases)
            {
                p.U16(ph.Id).U16(ph.Source).U8(ph.Initial ? 1 : 0).Str(ph.Name).Str(ph.Condition).Str(ph.Result).Str(ph.Icon).Str(ph.Sound).Str(ph.Text).U16(ph.FollowUps.Length);
                foreach (int f in ph.FollowUps)
                {
                    p.U16(f);
                }
            }
        }

        return p.ToArray();
    }

    private byte[] NameLists(string[]?[] lists)
    {
        using var ms = new MemoryStream();
        foreach (string[]? list in lists)
        {
            foreach (string n in list ?? [])
            {
                ms.Write(Bytes(n, true));
            }

            ms.WriteByte(0);
        }

        return ms.ToArray();
    }

    private byte[] MidisIni()
    {
        var sb = new StringBuilder("[MUSIC]\r\nSONGSNUMBER=").Append(Music.Count).Append("\r\n");
        for (int i = 0; i < Music.Count; i++)
        {
            sb.Append("SONG").Append(i + 1).Append('=').Append(Music[i]).Append("\r\n");
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    internal static void W16(byte[] b, int o, int v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
    }

    internal static void W32(byte[] b, int o, uint v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 16);
        b[o + 3] = (byte)(v >> 24);
    }
}

internal sealed class SyntheticKingdom
{
    public string Name { get; set; } = string.Empty;

    public string Tzar { get; set; } = string.Empty;

    public bool Active { get; set; }

    /// <summary>Gold, stone, food, wood (file order).</summary>
    public int[] Resources { get; } = [100, 200, 300, 400];

    public int StartX { get; set; } = -1;

    public int StartY { get; set; } = -1;

    public byte Race { get; set; } = 1;

    public string AiScript { get; set; } = string.Empty;

    public byte AiFlags { get; set; } = 3;

    public byte PlayerType { get; set; }
}

internal sealed class SyntheticObject
{
    public string ClassName { get; set; } = "peon";

    public int ClassIndex => LegacyClasses.IndexOf(ClassName);

    public int X { get; set; }

    public int Y { get; set; }

    public byte Owner { get; set; } = 1;

    public byte Race { get; set; } = 1;

    public ushort Id { get; set; } = 0xFFFF;

    public ushort Flags { get; set; } = 0x0BC0;

    public int Health { get; set; } = 50;

    public int Attack { get; set; } = 5;

    public int Defence { get; set; } = 1;

    public int Mana { get; set; }

    public int? Amount { get; set; }

    public string? Image { get; set; }

    public string? ScrollText { get; set; }

    /// <summary>Raw class extension written at 0x38 (gates, bridges, walls).</summary>
    public byte[]? Extension { get; set; }

    public int Direction { get; set; }

    public byte[] Payload(SyntheticMap map)
    {
        int size = ClassName switch
        {
            "wall_stone" or "wall_wood" or "gate_stone" or "gate_wood" or "bridge" => 58,
            "fishpatch" => 60,
            "res_gold" or "res_stone" => 72,
            "ruin" => 88,
            "ground_object" or "ground_patch" => 116,
            "item_scroll" => 1318,
            "item_plusplus" or "item_health" => 106,
            _ => 56,
        };
        byte[] p = new byte[size];
        SyntheticMap.W16(p, 0, size);
        SyntheticMap.W16(p, 2, ClassName is "ground_object" or "ground_patch" ? LegacyClasses.IndexOf("animation") : ClassIndex);
        SyntheticMap.W16(p, 4, Flags);
        SyntheticMap.W16(p, 8, X);
        SyntheticMap.W16(p, 0x0A, Y);
        p[0x0C] = Owner;
        p[0x0D] = Race;
        SyntheticMap.W16(p, 0x0E, Id);
        SyntheticMap.W16(p, 0x10, Direction);
        SyntheticMap.W16(p, 0x12, Health);
        SyntheticMap.W16(p, 0x14, Health);
        SyntheticMap.W16(p, 0x16, Attack);
        SyntheticMap.W16(p, 0x18, Defence);
        SyntheticMap.W16(p, 0x1A, 6);
        SyntheticMap.W16(p, 0x24, Mana);
        if (Extension is not null)
        {
            Array.Copy(Extension, 0, p, 0x38, Extension.Length);
        }

        switch (ClassName)
        {
            case "fishpatch":
                SyntheticMap.W32(p, 0x38, (uint)(Amount ?? 500));
                break;
            case "res_gold" or "res_stone":
                SyntheticMap.W16(p, 0x38, ClassName == "res_gold" ? 0 : 1);
                SyntheticMap.W32(p, 0x3A, (uint)(Amount ?? 10000));
                break;
            case "ground_object" or "ground_patch" or "ruin":
                Copy(p, 0x38, Image ?? "decal", map.CodePage);
                if (size >= 0x6A)
                {
                    SyntheticMap.W16(p, 0x66, (X * 32) + 5);
                    SyntheticMap.W16(p, 0x68, (Y * 32) + 7);
                }

                break;
            case "item_scroll":
                Copy(p, 0x38, "scroll", map.CodePage);
                Copy(p, 0x60, ScrollText ?? string.Empty, map.CodePage);
                SyntheticMap.W32(p, 0x520, 12000);
                p[0x524] = 1;
                break;
            case "item_plusplus" or "item_health":
                Copy(p, 0x38, "plusplus", map.CodePage);
                SyntheticMap.W16(p, 0x60, 2);
                break;
        }

        return p;
    }

    private static void Copy(byte[] p, int offset, string text, CodePage cp)
    {
        byte[] b = LegacyText.Encode(text, cp);
        Array.Copy(b, 0, p, offset, b.Length);
    }
}

internal sealed class SyntheticPhrase
{
    public int Id { get; set; }

    public int Source { get; set; }

    public bool Initial { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Condition { get; set; } = string.Empty;

    public string Result { get; set; } = string.Empty;

    public string Icon { get; set; } = string.Empty;

    public string Sound { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public int[] FollowUps { get; set; } = [];
}

/// <summary>Builds trigger payloads in the field order of docs/formats/wmp.md §6.</summary>
internal sealed class SyntheticPayload(CodePage codePage = CodePage.Windows1252)
{
    private readonly MemoryStream _ms = new();

    public SyntheticPayload U8(int v)
    {
        _ms.WriteByte((byte)v);
        return this;
    }

    public SyntheticPayload U16(int v)
    {
        _ms.WriteByte((byte)v);
        _ms.WriteByte((byte)(v >> 8));
        return this;
    }

    public SyntheticPayload U32(uint v)
    {
        for (int i = 0; i < 4; i++)
        {
            _ms.WriteByte((byte)(v >> (8 * i)));
        }

        return this;
    }

    public SyntheticPayload Str(string s)
    {
        _ms.Write(LegacyText.Encode(s, codePage));
        _ms.WriteByte(0);
        return this;
    }

    /// <summary>An explicit object set.</summary>
    public SyntheticPayload Named(bool all, params int[] ids)
    {
        U8(0).U8(0).U16(0xFFFF).U16(0).U8(all ? 1 : 0).U8(0).U16(ids.Length);
        foreach (int id in ids)
        {
            U16(id);
        }

        return Str("0");
    }

    /// <summary>A class or category with players and a count.</summary>
    public SyntheticPayload Typed(int classFilter, int players, bool atMost, string count, int race = 0)
        => U8(1).U8(atMost ? 1 : 0).U16(classFilter).U16(players).U8(0).U8(race).U16(0).Str(count);

    public SyntheticPayload Rect(int l, int t, int r, int b) => U8(0).U32((uint)l).U32((uint)t).U32((uint)r).U32((uint)b).U16(0).Str("0");

    public SyntheticPayload AtObject(int id, string distance) => U8(1).U32(0).U32(0).U32(0).U32(0).U16(id).Str(distance);

    public SyntheticPayload Holder(int id) => U8(0).U16(0xFFFF).U16(id).U16(0);

    public byte[] ToArray() => _ms.ToArray();
}
