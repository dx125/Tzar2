using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Tzar2.Formats.Hpfs;
using Tzar2.Formats.Ini;
using Tzar2.Formats.Text;

namespace Tzar2.Formats.Maps;

/// <summary>
/// Reader of legacy maps (<c>.wmp</c>) and campaigns (<c>.wcm</c>), docs/formats/wmp.md. Text is decoded with the
/// code page given by the caller; <see cref="TextSamples"/> returns the raw bytes of every text field so a caller can
/// detect the code page first. Malformed input throws <see cref="LegacyFormatException"/>; irregularities that do not
/// prevent decoding become <see cref="FormatWarning"/>s.
/// </summary>
public static class LegacyMapReader
{
    /// <summary>Size of the map header member.</summary>
    public const int HeaderSize = 1570;

    /// <summary>Size of one kingdom record.</summary>
    public const int KingdomSize = 152;

    /// <summary>Kingdom records per map.</summary>
    public const int KingdomCount = 8;

    /// <summary>Size of one <c>objext</c> record.</summary>
    public const int ObjectExtraSize = 258;

    /// <summary>The header magic (the ASCII bytes <c>2mzt</c>).</summary>
    public const uint Magic = 0x747A6D32;

    private const string Format = "WMP";
    private const string CampaignFormat = "WCM";

    /// <summary>Reads a map image (top-level <c>.wmp</c> or a map embedded in a campaign).</summary>
    public static LegacyMap ReadMap(ReadOnlySpan<byte> data, CodePage codePage = CodePage.Windows1252)
        => new MapParser(codePage, []).Parse(data);

    /// <summary>The raw bytes of every text field of a map (for code page detection).</summary>
    public static IReadOnlyList<byte[]> TextSamples(ReadOnlySpan<byte> data)
    {
        var text = new List<byte[]>();
        new MapParser(CodePage.Windows1252, text).Parse(data);
        return text;
    }

    /// <summary>Reads a campaign container. Embedded maps stay as bytes; read them with <see cref="ReadMap"/>.</summary>
    public static LegacyCampaign ReadCampaign(ReadOnlySpan<byte> data, CodePage codePage = CodePage.Windows1252)
        => ReadCampaign(data, codePage, null);

    /// <summary>The raw bytes of the campaign's own text (title, description, map names).</summary>
    public static IReadOnlyList<byte[]> CampaignTextSamples(ReadOnlySpan<byte> data)
    {
        var text = new List<byte[]>();
        ReadCampaign(data, CodePage.Windows1252, text);
        return text;
    }

    private static LegacyCampaign ReadCampaign(ReadOnlySpan<byte> data, CodePage codePage, List<byte[]>? text)
    {
        HpfsArchive archive = HpfsArchive.Open(data, codePage);
        var warnings = new List<FormatWarning>();
        string title = string.Empty;
        string description = string.Empty;
        var maps = ImmutableArray.CreateBuilder<(string, bool)>();
        var mapFiles = ImmutableArray.CreateBuilder<LegacyFile>();
        var files = ImmutableArray.CreateBuilder<LegacyFile>();
        bool sawMapsIni = false;
        foreach (HpfsEntry e in archive.ReadAll())
        {
            if (e.IsDirectory)
            {
                continue;
            }

            string name = FileName(e.Path);
            if (e.IsNestedArchive)
            {
                mapFiles.Add(new LegacyFile(e.Path, e.Data));
            }
            else if (name.Equals("campaign description.ini", StringComparison.OrdinalIgnoreCase))
            {
                ReadOnlySpan<byte> raw = e.Data;
                int nul = raw.IndexOf((byte)0);
                raw = nul >= 0 ? raw[..nul] : raw;
                text?.Add(raw.ToArray());
                string all = LegacyText.Decode(raw, codePage);
                int eol = all.IndexOf('\n', StringComparison.Ordinal);
                title = (eol < 0 ? all : all[..eol]).TrimEnd('\r');
                description = eol < 0 ? string.Empty : all[(eol + 1)..];
            }
            else if (name.Equals("maps.ini", StringComparison.OrdinalIgnoreCase) && Parent(e.Path).Equals("MAPS", StringComparison.OrdinalIgnoreCase))
            {
                sawMapsIni = true;
                text?.Add(e.Data);
                IniDocument ini = IniDocument.Parse(e.Data, codePage);
                IniSection? section = ini.FindSection("maps");
                if (section is null)
                {
                    warnings.Add(new FormatWarning(0, "maps.ini has no [maps] section."));
                }
                else
                {
                    foreach (IniEntry entry in section.Entries)
                    {
                        if (entry.Key is { Length: > 0 } key)
                        {
                            maps.Add((key.Trim(), entry.Value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)));
                        }
                    }
                }
            }
            else
            {
                files.Add(new LegacyFile(e.Path, e.Data));
            }
        }

        if (!sawMapsIni)
        {
            throw new LegacyFormatException(CampaignFormat, "no MAPS/maps.ini.");
        }

        if (mapFiles.Count == 0)
        {
            throw new LegacyFormatException(CampaignFormat, "no embedded maps (a map, not a campaign?).");
        }

        return new LegacyCampaign(title, description, maps.ToImmutable(), mapFiles.ToImmutable(), files.ToImmutable(), [.. warnings]);
    }

    private static string FileName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string Parent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private sealed class MapParser(CodePage codePage, List<byte[]> text)
    {
        private readonly List<FormatWarning> _warnings = [];

        public LegacyMap Parse(ReadOnlySpan<byte> data)
        {
            HpfsArchive archive = HpfsArchive.Open(data, codePage);
            IReadOnlyList<HpfsEntry> entries = archive.ReadAll();
            string? mapName = null;
            foreach (HpfsEntry e in entries)
            {
                if (!e.IsDirectory && Parent(e.Path).Equals("MAPS", StringComparison.OrdinalIgnoreCase)
                    && e.Path.EndsWith(".header", StringComparison.OrdinalIgnoreCase))
                {
                    if (mapName is not null)
                    {
                        throw new LegacyFormatException(Format, "more than one map header.");
                    }

                    string file = FileName(e.Path);
                    mapName = file[..^".header".Length];
                }
            }

            if (mapName is null)
            {
                throw new LegacyFormatException(Format, "no MAPS/<name>.header member (not a map).");
            }

            var parts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var files = ImmutableArray.CreateBuilder<LegacyFile>();
            string prefix = mapName + ".";
            foreach (HpfsEntry e in entries)
            {
                if (e.IsDirectory)
                {
                    continue;
                }

                string file = FileName(e.Path);
                if (Parent(e.Path).Equals("MAPS", StringComparison.OrdinalIgnoreCase) && file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string part = file[prefix.Length..];
                    if (!parts.TryAdd(part, e.Data))
                    {
                        Warn(0, $"duplicate map member '{file}' ignored.");
                    }
                }
                else
                {
                    files.Add(new LegacyFile(e.Path, e.Data));
                }
            }

            LegacyMapHeader header = Header(Require(parts, "header"));
            string description = Text(Optional(parts, "description"));
            ImmutableArray<LegacyKingdom> kingdoms = Kingdoms(Require(parts, "kingdominfo"));
            ImmutableArray<uint> diplomacy = Diplomacy(Optional(parts, "diplomacy"));
            ImmutableArray<ushort> tiles = Tiles(Require(parts, "tiles"), header);
            ImmutableArray<LegacyObject> objects = Objects(Optional(parts, "objects"), header);
            ImmutableArray<LegacyObjectExtra> extras = Extras(Optional(parts, "objext"));
            ImmutableArray<LegacyTrigger> events = Triggers(Optional(parts, "events"), "events", header.EventCount);
            ImmutableArray<LegacyTrigger> actions = Triggers(Optional(parts, "actions"), "actions", header.ActionCount);
            ImmutableArray<LegacyRule> rules = Rules(Optional(parts, "rules"), header.RuleCount);
            ImmutableArray<LegacyConversation> conversations = Conversations(Optional(parts, "conv"));
            ImmutableArray<string> music = Music(files);
            ImmutableArray<ImmutableArray<string>> forbiddenBuildings = NameLists(Optional(parts, "forbidden.buildings"), "forbidden.buildings");
            ImmutableArray<ImmutableArray<string>> forbiddenUnits = NameLists(Optional(parts, "forbidden.units"), "forbidden.units");
            ImmutableArray<ImmutableArray<string>> forbiddenResearches = NameLists(Optional(parts, "forbidden.researches"), "forbidden.researches");
            ImmutableArray<ImmutableArray<string>> forbiddenUpgrades = NameLists(Optional(parts, "forbidden.upgrades"), "forbidden.upgrades");
            ImmutableArray<ImmutableArray<string>> initialResearches = NameLists(Optional(parts, "initial.researches"), "initial.researches");
            ImmutableArray<byte> defeat = [.. Optional(parts, "defeat.messages")];
            foreach (string known in KnownParts)
            {
                parts.Remove(known);
            }

            var unknown = new List<string>(parts.Keys);
            unknown.Sort(StringComparer.Ordinal);
            foreach (string part in unknown)
            {
                Warn(0, $"unknown map member '{mapName}.{part}' kept as a file.");
                files.Add(new LegacyFile("MAPS/" + mapName + "." + part, parts[part]));
            }

            return new LegacyMap(
                mapName,
                header,
                description,
                kingdoms,
                diplomacy,
                forbiddenBuildings,
                forbiddenUnits,
                forbiddenResearches,
                forbiddenUpgrades,
                initialResearches,
                tiles,
                objects,
                extras,
                events,
                actions,
                rules,
                conversations,
                defeat,
                music,
                files.ToImmutable(),
                [.. _warnings]);
        }

        private static readonly string[] KnownParts =
        [
            "header", "description", "kingdominfo", "diplomacy", "tiles", "objects", "objext", "events", "actions", "rules",
            "conv", "defeat.messages", "forbidden.buildings", "forbidden.units", "forbidden.researches", "forbidden.upgrades",
            "initial.researches",
        ];

        private void Warn(int line, string message) => _warnings.Add(new FormatWarning(line, message));

        private static byte[] Require(Dictionary<string, byte[]> parts, string name)
            => parts.TryGetValue(name, out byte[]? data) ? data : throw new LegacyFormatException(Format, $"missing map member '{name}'.");

        private static byte[] Optional(Dictionary<string, byte[]> parts, string name) => parts.TryGetValue(name, out byte[]? data) ? data : [];

        private string Text(ReadOnlySpan<byte> bytes)
        {
            int nul = bytes.IndexOf((byte)0);
            ReadOnlySpan<byte> s = nul >= 0 ? bytes[..nul] : bytes;
            if (s.Length > 0)
            {
                text.Add(s.ToArray());
            }

            return LegacyText.Decode(s, codePage);
        }

        private string Fixed(ReadOnlySpan<byte> data, int offset, int length) => Text(data.Slice(offset, length));

        private LegacyMapHeader Header(byte[] h)
        {
            if (h.Length < HeaderSize)
            {
                throw new LegacyFormatException(Format, $"header is {h.Length} bytes (expected {HeaderSize}).");
            }

            if (ByteReader.U32(h, 0, Format, "magic") != Magic)
            {
                throw new LegacyFormatException(Format, "bad header magic.");
            }

            int side = ByteReader.U16(h, 6, Format, "side");
            if (side < 1 || side > 1024)
            {
                throw new LegacyFormatException(Format, $"map side {side} out of range.");
            }

            return new LegacyMapHeader(
                side,
                (int)ByteReader.U32(h, 0x08, Format, "object count"),
                ByteReader.U16(h, 0x0C, Format, "rule count"),
                ByteReader.U16(h, 0x0E, Format, "action count"),
                ByteReader.U16(h, 0x10, Format, "event count"),
                (int)ByteReader.U32(h, 0x16, Format, "objext size"),
                (int)ByteReader.U32(h, 0x1A, Format, "tiles size"),
                h[0x4D],
                h[0x50],
                Fixed(h, 0x56, 64),
                Fixed(h, 0x96, 64),
                Fixed(h, 0xD6, 64),
                (int)ByteReader.U32(h, 0x116, Format, "description length"),
                (int)ByteReader.U32(h, 0x122, Format, "conversation size"),
                ByteReader.U16(h, 0x128, Format, "population limit"),
                ByteReader.U16(h, 0x12A, Format, "time limit"),
                ByteReader.U16(h, 0x12E, Format, "score limit"),
                (int)ByteReader.U32(h, 0x132, Format, "version"),
                [.. h]);
        }

        private ImmutableArray<LegacyKingdom> Kingdoms(byte[] b)
        {
            if (b.Length < KingdomSize * KingdomCount)
            {
                throw new LegacyFormatException(Format, $"kingdominfo is {b.Length} bytes (expected {KingdomSize * KingdomCount}).");
            }

            var result = ImmutableArray.CreateBuilder<LegacyKingdom>(KingdomCount);
            for (int k = 0; k < KingdomCount; k++)
            {
                ReadOnlySpan<byte> r = b.AsSpan(k * KingdomSize, KingdomSize);
                ushort sx = ByteReader.U16(r, 0x54, Format, "start x");
                ushort sy = ByteReader.U16(r, 0x56, Format, "start y");
                result.Add(new LegacyKingdom(
                    k + 1,
                    Fixed(r, 0x00, 32),
                    Fixed(r, 0x20, 32),
                    r[0x40] != 0,
                    r[0x42],
                    r[0x43],
                    [(int)ByteReader.U32(r, 0x44, Format, "res"), (int)ByteReader.U32(r, 0x48, Format, "res"), (int)ByteReader.U32(r, 0x4C, Format, "res"), (int)ByteReader.U32(r, 0x50, Format, "res")],
                    sx == 0xFFFF ? -1 : sx,
                    sy == 0xFFFF ? -1 : sy,
                    r[0x58],
                    Fixed(r, 0x59, 32),
                    r[0x79],
                    r[0x7A],
                    [.. r]));
            }

            if (b.Length != KingdomSize * KingdomCount)
            {
                Warn(0, $"kingdominfo has {b.Length - (KingdomSize * KingdomCount)} surplus bytes.");
            }

            return result.MoveToImmutable();
        }

        private ImmutableArray<uint> Diplomacy(byte[] b)
        {
            var result = new uint[KingdomCount * KingdomCount];
            if (b.Length == 0)
            {
                Warn(0, "no diplomacy member; all kingdoms are treated as enemies.");
                return [.. result];
            }

            if (b.Length < result.Length * 4)
            {
                throw new LegacyFormatException(Format, $"diplomacy is {b.Length} bytes (expected {result.Length * 4}).");
            }

            for (int i = 0; i < result.Length; i++)
            {
                result[i] = ByteReader.U32(b, i * 4, Format, "diplomacy");
            }

            return [.. result];
        }

        private ImmutableArray<ImmutableArray<string>> NameLists(byte[] b, string what)
        {
            var lists = ImmutableArray.CreateBuilder<ImmutableArray<string>>(KingdomCount);
            int o = 0;
            for (int k = 0; k < KingdomCount; k++)
            {
                var names = ImmutableArray.CreateBuilder<string>();
                while (o < b.Length)
                {
                    int end = Array.IndexOf(b, (byte)0, o);
                    if (end < 0)
                    {
                        throw new LegacyFormatException(Format, $"{what}: unterminated name list.");
                    }

                    if (end == o)
                    {
                        o++;
                        break;
                    }

                    names.Add(Text(b.AsSpan(o, end - o)));
                    o = end + 1;
                }

                lists.Add(names.ToImmutable());
            }

            if (o < b.Length && b.AsSpan(o).IndexOfAnyExcept((byte)0) >= 0)
            {
                Warn(0, $"{what}: {b.Length - o} surplus bytes.");
            }

            return lists.MoveToImmutable();
        }

        private ImmutableArray<ushort> Tiles(byte[] b, LegacyMapHeader header)
        {
            int count = header.Side * header.Side;
            if (b.Length < count * 2)
            {
                throw new LegacyFormatException(Format, $"tiles is {b.Length} bytes (expected {count * 2}).");
            }

            var tiles = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                tiles[i] = (ushort)(b[i * 2] | (b[(i * 2) + 1] << 8));
            }

            return [.. tiles];
        }

        private ImmutableArray<LegacyObject> Objects(byte[] b, LegacyMapHeader header)
        {
            var result = ImmutableArray.CreateBuilder<LegacyObject>();
            int o = 0;
            while (o < b.Length)
            {
                byte kind = ByteReader.U8(b, o, Format, "object kind");
                uint encoded = ByteReader.U32(b, o + 1, Format, "object class");
                uint size = ByteReader.U32(b, o + 5, Format, "object size");
                ByteReader.Require(b, o + 9, size, Format, "object record");
                result.Add(Object(result.Count, kind, encoded, b.AsSpan(o + 9, (int)size)));
                o += 9 + (int)size;
            }

            if (result.Count != header.ObjectCount)
            {
                Warn(0, $"objects holds {result.Count} records, the header says {header.ObjectCount}.");
            }

            return result.ToImmutable();
        }

        private LegacyObject Object(int index, byte kind, uint encoded, ReadOnlySpan<byte> p)
        {
            int cls = LegacyClasses.Decode(encoded);
            string name = cls >= 0 ? LegacyClasses.Names[cls] : "#" + encoded.ToString("x8", CultureInfo.InvariantCulture);
            if (p.Length < 0x38)
            {
                Warn(index, $"object {index} ({name}) has a {p.Length}-byte record (expected at least 56); kept raw.");
                return new LegacyObject(index, kind, cls, name, LegacyObjectFlags.None, 0, 0, 0, 0, 0xFFFF, 0, 0, 0, 0, 0, 0, 0, 0, [.. p]);
            }

            // The class word inside the record (0x02) is the creating class: decorations store "animation" there while the
            // record header says ground_patch/ground_object. The record header is authoritative.
            var o = new LegacyObject(
                index,
                kind,
                cls,
                name,
                (LegacyObjectFlags)ByteReader.U16(p, 0x04, Format, "flags"),
                ByteReader.I16(p, 0x08, Format, "x"),
                ByteReader.I16(p, 0x0A, Format, "y"),
                p[0x0C],
                p[0x0D],
                ByteReader.U16(p, 0x0E, Format, "id"),
                ByteReader.U16(p, 0x10, Format, "direction"),
                ByteReader.U16(p, 0x12, Format, "health"),
                ByteReader.U16(p, 0x14, Format, "max health"),
                ByteReader.U16(p, 0x16, Format, "attack"),
                ByteReader.U16(p, 0x18, Format, "defence"),
                ByteReader.U16(p, 0x1A, Format, "sight"),
                ByteReader.U16(p, 0x1C, Format, "level"),
                ByteReader.U16(p, 0x24, Format, "mana"),
                [.. p]);
            return Extend(o, p);
        }

        private LegacyObject Extend(LegacyObject o, ReadOnlySpan<byte> p)
        {
            switch (o.ClassName)
            {
                case "wall_stone" or "wall_wood" when p.Length >= 0x3A:
                    return o with { Shape = ByteReader.U16(p, 0x38, Format, "wall") };
                case "gate_stone" or "gate_wood" when p.Length >= 0x3A:
                    return o with { Shape = p[0x38] | (p[0x39] << 8) };
                case "bridge" when p.Length >= 0x39:
                    return o with { Shape = p[0x38] };
                case "fishpatch" when p.Length >= 0x3C:
                    return o with { Amount = (int)ByteReader.U32(p, 0x38, Format, "amount") };
                case "res_gold" or "res_stone" when p.Length >= 0x40:
                    return o with { Amount = (int)ByteReader.U32(p, 0x3A, Format, "amount"), Shape = p[0x3E] | (p[0x3F] << 8) };
                case "ruin" when p.Length >= 0x58:
                    return o with { ImageName = Fixed(p, 0x38, 32) };
                case "ground_object" or "ground_patch" or "animation" when p.Length >= 0x6A:
                    return o with
                    {
                        ImageName = Fixed(p, 0x38, 32),
                        PixelPosition = (ByteReader.U16(p, 0x66, Format, "pixel x"), ByteReader.U16(p, 0x68, Format, "pixel y")),
                    };
                case "item_scroll" when p.Length >= 0x525:
                    return o with
                    {
                        Item = new LegacyItem(
                            Fixed(p, 0x38, 40),
                            0,
                            Text: Fixed(p, 0x60, 0x400),
                            Image: Fixed(p, 0x460, 64),
                            Sound: Fixed(p, 0x4A0, 64),
                            Music: Fixed(p, 0x4E0, 64),
                            ScrollTimeMs: (int)ByteReader.U32(p, 0x520, Format, "scroll time"),
                            AutoClose: p[0x524] != 0),
                    };
                case "item_magic" when p.Length >= 0x68:
                    return o with
                    {
                        Item = new LegacyItem(
                            Fixed(p, 0x38, 40),
                            p[0x60],
                            MagicType: p[0x60],
                            NeedsMana: p[0x63] != 0,
                            CooldownSeconds: ByteReader.U16(p, 0x64, Format, "cooldown"),
                            Charges: ByteReader.U16(p, 0x66, Format, "charges")),
                    };
                case "resitem" when p.Length >= 0x64:
                    return o with { Item = new LegacyItem(Fixed(p, 0x38, 40), p[0x60], Amount: ByteReader.U16(p, 0x62, Format, "amount")) };
                case "item" or "item_plusplus" or "item_health" or "item_soul" or "item_flag" when p.Length >= 0x62:
                    return o with { Item = new LegacyItem(Fixed(p, 0x38, 40), ByteReader.U16(p, 0x60, Format, "value")) };
                case "item" or "item_plusplus" or "item_health" or "item_soul" or "item_flag" when p.Length >= 0x60:
                    return o with { Item = new LegacyItem(Fixed(p, 0x38, 40), 0) };
                default:
                    return o;
            }
        }

        private ImmutableArray<LegacyObjectExtra> Extras(byte[] b)
        {
            if (b.Length % ObjectExtraSize != 0)
            {
                Warn(0, $"objext size {b.Length} is not a multiple of {ObjectExtraSize}; the tail is ignored.");
            }

            var result = ImmutableArray.CreateBuilder<LegacyObjectExtra>(b.Length / ObjectExtraSize);
            for (int o = 0; o + ObjectExtraSize <= b.Length; o += ObjectExtraSize)
            {
                result.Add(new LegacyObjectExtra(ByteReader.U16(b, o, Format, "objext id"), Fixed(b, o + 2, 128), Fixed(b, o + 130, 128)));
            }

            return result.MoveToImmutable();
        }

        private ImmutableArray<LegacyTrigger> Triggers(byte[] b, string what, int expected)
        {
            var result = ImmutableArray.CreateBuilder<LegacyTrigger>();
            foreach ((string name, int type, byte[] payload) in Records(b, what))
            {
                ImmutableArray<object>? fields = LegacyTriggerCodec.Decode(type, payload, codePage, text);
                if (fields is null)
                {
                    Warn(result.Count, $"{what} record '{name}' (type {type}) could not be decoded; kept raw.");
                }

                result.Add(new LegacyTrigger(name, type, fields ?? [], [.. payload]));
            }

            if (result.Count != expected)
            {
                Warn(0, $"{what} holds {result.Count} records, the header says {expected}.");
            }

            return result.ToImmutable();
        }

        private List<(string Name, int Type, byte[] Payload)> Records(byte[] b, string what)
        {
            var result = new List<(string, int, byte[])>();
            int o = 0;
            while (o < b.Length)
            {
                ByteReader.Require(b, o, 0x24, Format, $"{what} record header");
                string name = Fixed(b, o, 0x20);
                int type = ByteReader.U16(b, o + 0x20, Format, "record type");
                int length = ByteReader.U16(b, o + 0x22, Format, "record length");
                ByteReader.Require(b, o + 0x24, length, Format, $"{what} record '{name}'");
                result.Add((name, type, b.AsSpan(o + 0x24, length).ToArray()));
                o += 0x24 + length;
            }

            return result;
        }

        private ImmutableArray<LegacyRule> Rules(byte[] b, int expected)
        {
            var result = ImmutableArray.CreateBuilder<LegacyRule>();
            foreach ((string name, int count, byte[] payload) in Records(b, "rules"))
            {
                var strings = new List<string>();
                int o = 0;
                while (o < payload.Length)
                {
                    int end = Array.IndexOf(payload, (byte)0, o);
                    if (end < 0)
                    {
                        Warn(result.Count, $"rule '{name}': unterminated text; the tail is ignored.");
                        break;
                    }

                    strings.Add(Text(payload.AsSpan(o, end - o)));
                    o = end + 1;
                }

                string condition = strings.Count > 0 ? strings[0] : string.Empty;
                ImmutableArray<string> actions = strings.Count > 1 ? [.. strings.GetRange(1, strings.Count - 1)] : [];
                if (actions.Length != count)
                {
                    Warn(result.Count, $"rule '{name}' lists {actions.Length} actions, its record says {count}.");
                }

                result.Add(new LegacyRule(name, condition, actions));
            }

            if (result.Count != expected)
            {
                Warn(0, $"rules holds {result.Count} records, the header says {expected}.");
            }

            return result.ToImmutable();
        }

        private ImmutableArray<LegacyConversation> Conversations(byte[] b)
        {
            if (b.Length == 0)
            {
                return [];
            }

            var c = new LegacyTriggerCodec.Cursor(b, codePage, text);
            int count = c.U16();
            var result = ImmutableArray.CreateBuilder<LegacyConversation>(count);
            for (int i = 0; i < count; i++)
            {
                string name = c.Str();
                int phrases = c.U16();
                var list = ImmutableArray.CreateBuilder<LegacyPhrase>(phrases);
                for (int p = 0; p < phrases; p++)
                {
                    ushort id = (ushort)c.U16();
                    ushort source = (ushort)c.U16();
                    bool initial = c.U8() != 0;
                    string phraseName = c.Str();
                    string condition = c.Str();
                    string resultText = c.Str();
                    string icon = c.Str();
                    string sound = c.Str();
                    string body = c.Str();
                    int follow = c.U16();
                    var ids = ImmutableArray.CreateBuilder<ushort>(follow);
                    for (int f = 0; f < follow; f++)
                    {
                        ids.Add((ushort)c.U16());
                    }

                    list.Add(new LegacyPhrase(id, source, initial, phraseName, condition, resultText, icon, sound, body, ids.MoveToImmutable()));
                }

                result.Add(new LegacyConversation(name, list.MoveToImmutable()));
            }

            if (!c.AtEnd)
            {
                Warn(0, $"conv has {b.Length - c.Offset} surplus bytes.");
            }

            return result.MoveToImmutable();
        }

        private ImmutableArray<string> Music(ImmutableArray<LegacyFile>.Builder files)
        {
            foreach (LegacyFile f in files)
            {
                if (f.Path.Equals("MAPS/midis.ini", StringComparison.OrdinalIgnoreCase))
                {
                    IniSection? music = IniDocument.Parse(f.Data, codePage).FindSection("MUSIC");
                    if (music is null)
                    {
                        return [];
                    }

                    int n = int.TryParse(music.GetValue("SONGSNUMBER"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
                    var songs = ImmutableArray.CreateBuilder<string>();
                    for (int i = 1; i <= n; i++)
                    {
                        string? song = music.GetValue("SONG" + i.ToString(CultureInfo.InvariantCulture));
                        if (!string.IsNullOrWhiteSpace(song))
                        {
                            songs.Add(song.Trim());
                        }
                    }

                    return songs.ToImmutable();
                }
            }

            return [];
        }
    }
}
