using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Formats.Text;

namespace Tzar2.Formats.Maps;

/// <summary>Legacy trigger record type codes (docs/formats/wmp.md §6.2). Actions are 0–27, events 1000–1007.</summary>
public enum LegacyTriggerType
{
    /// <summary>Add Quest: text, image.</summary>
    AddQuest = 0,

    /// <summary>Remove Quest: quest (Add Quest action) name.</summary>
    RemoveQuest = 1,

    /// <summary>Change Diplomacy Settings: kingdom, kingdom, relation flags.</summary>
    ChangeDiplomacy = 2,

    /// <summary>Change Object Owner: objects, kingdom.</summary>
    ChangeObjectOwner = 3,

    /// <summary>Set Object Properties: objects, expression.</summary>
    SetObjectProperties = 4,

    /// <summary>Expression: expression.</summary>
    Expression = 5,

    /// <summary>Set Timer: initial value, count-down flag.</summary>
    SetTimer = 6,

    /// <summary>Stop Timer: timer (Set Timer action) name.</summary>
    StopTimer = 7,

    /// <summary>Player Lose: players, "map|?" text.</summary>
    PlayerLose = 8,

    /// <summary>Player Win: players, "map|?" text.</summary>
    PlayerWin = 9,

    /// <summary>Keep Rule.</summary>
    KeepRule = 10,

    /// <summary>Load Map: map path.</summary>
    LoadMap = 11,

    /// <summary>Lock View: time, area.</summary>
    LockView = 12,

    /// <summary>Send Chat Message: text, players.</summary>
    SendChat = 13,

    /// <summary>Show Story Screen: text, image, sound, music, scroll time, auto-close.</summary>
    ShowStoryScreen = 14,

    /// <summary>Show Message: title, text, image.</summary>
    ShowMessage = 15,

    /// <summary>Spawn Object: class, count, owner, area, race.</summary>
    SpawnObject = 16,

    /// <summary>Unlock View.</summary>
    UnlockView = 17,

    /// <summary>Object Order: objects, area, order.</summary>
    ObjectOrder = 18,

    /// <summary>Scroll View: area, area.</summary>
    ScrollView = 19,

    /// <summary>Conversation: conversation name.</summary>
    Conversation = 20,

    /// <summary>Destroy Object: objects.</summary>
    DestroyObject = 21,

    /// <summary>Explore Area: area, time, reveal flag.</summary>
    ExploreArea = 22,

    /// <summary>Appear Object: objects.</summary>
    AppearObject = 23,

    /// <summary>Change Player Status: kingdom, cannot win, cannot lose.</summary>
    ChangePlayerStatus = 24,

    /// <summary>Lock/Unlock Units: objects, flag (0 lock, 1 unlock).</summary>
    LockUnits = 25,

    /// <summary>Erase Object: objects.</summary>
    EraseObject = 26,

    /// <summary>Delay: time (not in the manual).</summary>
    Delay = 27,

    /// <summary>Object Exist: objects.</summary>
    ObjectExist = 1000,

    /// <summary>Object Destroyed: objects.</summary>
    ObjectDestroyed = 1001,

    /// <summary>Objects Enter Holder: objects, holder.</summary>
    ObjectsEnterHolder = 1002,

    /// <summary>Object Leave Holder: objects, holder.</summary>
    ObjectLeaveHolder = 1003,

    /// <summary>Object In Area: objects, area.</summary>
    ObjectInArea = 1004,

    /// <summary>Player Defeated: players.</summary>
    PlayerDefeated = 1005,

    /// <summary>Player Won: players.</summary>
    PlayerWon = 1006,

    /// <summary>Object Expression: objects, expression.</summary>
    ObjectExpression = 1007,
}

/// <summary>An object specification in a trigger (docs/formats/wmp.md §6.3).</summary>
/// <param name="Typed">False: an explicit object set; true: a class/category with players and a count.</param>
/// <param name="AtMost">Count comparison: false ≥, true ≤.</param>
/// <param name="ClassFilter">Class index, or 0xFFFF any, 0xFFFE units, 0xFFFD military units, 0xFFFC buildings, 0xFFFB major objects.</param>
/// <param name="Players">Player mask, bit 0 = player 1.</param>
/// <param name="All">Quantifier: true All, false Any (⚠ UNVERIFIED encoding).</param>
/// <param name="Race">Race of the class (0 common, 1 European, 2 Arabian, 3 Asian).</param>
/// <param name="ObjectIds">Object ids of an explicit set.</param>
/// <param name="Count">Count as written.</param>
public sealed record LegacyObjectSpec(bool Typed, bool AtMost, ushort ClassFilter, ushort Players, bool All, byte Race, ImmutableArray<ushort> ObjectIds, string Count);

/// <summary>An area in a trigger: a tile rectangle or an object with a distance.</summary>
/// <param name="IsObject">True: the area follows <see cref="ObjectId"/>.</param>
/// <param name="Left">Left tile.</param>
/// <param name="Top">Top tile.</param>
/// <param name="Right">Right tile (inclusive).</param>
/// <param name="Bottom">Bottom tile (inclusive).</param>
/// <param name="ObjectId">Object id.</param>
/// <param name="Distance">Distance around the object as written (tiles).</param>
public sealed record LegacyArea(bool IsObject, int Left, int Top, int Right, int Bottom, ushort ObjectId, string Distance);

/// <summary>A holder in a trigger: a specific object or a class with players.</summary>
/// <param name="Typed">True: any holder of <see cref="ClassFilter"/> owned by <see cref="Players"/>.</param>
/// <param name="ClassFilter">Class index or 0xFFFF any.</param>
/// <param name="ObjectId">Holder object id.</param>
/// <param name="Players">Player mask.</param>
public sealed record LegacyHolder(bool Typed, ushort ClassFilter, ushort ObjectId, ushort Players);

/// <summary>
/// An event or action record: a 32-byte name, a type, and a payload decoded into <see cref="Fields"/> (strings as
/// <see cref="string"/>, numbers as <see cref="int"/>, and <see cref="LegacyObjectSpec"/>, <see cref="LegacyArea"/>,
/// <see cref="LegacyHolder"/>) in payload order (docs/formats/wmp.md §6.4). An unknown type keeps only the payload.
/// </summary>
/// <param name="Name">Record name.</param>
/// <param name="Type">Type code.</param>
/// <param name="Fields">Decoded fields, or empty for an unknown type.</param>
/// <param name="Payload">The raw payload.</param>
public sealed record LegacyTrigger(string Name, int Type, ImmutableArray<object> Fields, ImmutableArray<byte> Payload)
{
    /// <summary>Whether <see cref="Type"/> is known.</summary>
    public bool IsKnown => Enum.IsDefined((LegacyTriggerType)Type);

    /// <summary>Field <paramref name="index"/> as <typeparamref name="T"/>.</summary>
    public T Field<T>(int index) => (T)Fields[index];
}

/// <summary>Payload decoding of trigger records.</summary>
internal static class LegacyTriggerCodec
{
    private const string Format = "WMP";

    private enum F
    {
        U8,
        U16,
        U32,
        Str,
        Obj,
        Area,
        Holder,
    }

    private static readonly Dictionary<int, F[]> Layouts = new()
    {
        [0] = [F.Str, F.Str],
        [1] = [F.Str],
        [2] = [F.U8, F.U8, F.U32],
        [3] = [F.Obj, F.U8],
        [4] = [F.Obj, F.Str],
        [5] = [F.Str],
        [6] = [F.Str, F.U8],
        [7] = [F.Str],
        [8] = [F.U16, F.Str],
        [9] = [F.U16, F.Str],
        [10] = [],
        [11] = [F.Str],
        [12] = [F.Str, F.Area],
        [13] = [F.Str, F.U16],
        [14] = [F.Str, F.Str, F.Str, F.Str, F.Str, F.U8],
        [15] = [F.Str, F.Str, F.Str],
        [16] = [F.U16, F.Str, F.U8, F.Area, F.U8],
        [17] = [],
        [18] = [F.Obj, F.Area, F.U8],
        [19] = [F.Area, F.Area],
        [20] = [F.Str],
        [21] = [F.Obj],
        [22] = [F.Area, F.Str, F.U8],
        [23] = [F.Obj],
        [24] = [F.U8, F.U8, F.U8],
        [25] = [F.Obj, F.U8],
        [26] = [F.Obj],
        [27] = [F.Str],
        [1000] = [F.Obj],
        [1001] = [F.Obj],
        [1002] = [F.Obj, F.Holder],
        [1003] = [F.Obj, F.Holder],
        [1004] = [F.Obj, F.Area],
        [1005] = [F.U16],
        [1006] = [F.U16],
        [1007] = [F.Obj, F.Str],
    };

    /// <summary>Decodes a payload; returns null if the type is unknown or the payload does not match its layout.</summary>
    public static ImmutableArray<object>? Decode(int type, ReadOnlySpan<byte> payload, CodePage codePage, List<byte[]> text)
    {
        if (!Layouts.TryGetValue(type, out F[]? layout))
        {
            return null;
        }

        try
        {
            var c = new Cursor(payload, codePage, text);
            var fields = ImmutableArray.CreateBuilder<object>(layout.Length);
            foreach (F f in layout)
            {
                fields.Add(f switch
                {
                    F.U8 => c.U8(),
                    F.U16 => c.U16(),
                    F.U32 => (object)unchecked((int)c.U32()),
                    F.Str => c.Str(),
                    F.Obj => c.Obj(),
                    F.Area => c.Area(),
                    _ => c.Holder(),
                });
            }

            return c.Offset == payload.Length ? fields.MoveToImmutable() : null;
        }
        catch (LegacyFormatException)
        {
            return null;
        }
    }

    internal ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly CodePage _codePage;
        private readonly List<byte[]> _text;

        public Cursor(ReadOnlySpan<byte> data, CodePage codePage, List<byte[]> text)
        {
            _data = data;
            _codePage = codePage;
            _text = text;
            Offset = 0;
        }

        public int Offset { get; private set; }

        public bool AtEnd => Offset >= _data.Length;

        public int U8()
        {
            byte v = ByteReader.U8(_data, Offset, Format, "u8");
            Offset += 1;
            return v;
        }

        public int U16()
        {
            ushort v = ByteReader.U16(_data, Offset, Format, "u16");
            Offset += 2;
            return v;
        }

        public uint U32()
        {
            uint v = ByteReader.U32(_data, Offset, Format, "u32");
            Offset += 4;
            return v;
        }

        public string Str()
        {
            int end = _data[Offset..].IndexOf((byte)0);
            if (end < 0)
            {
                throw new LegacyFormatException(Format, "unterminated string.");
            }

            ReadOnlySpan<byte> bytes = _data.Slice(Offset, end);
            Offset += end + 1;
            if (bytes.Length > 0)
            {
                _text.Add(bytes.ToArray());
            }

            return LegacyText.Decode(bytes, _codePage);
        }

        public LegacyObjectSpec Obj()
        {
            int typed = U8();
            int atMost = U8();
            int cls = U16();
            int players = U16();
            int all = U8();
            int race = U8();
            int n = U16();
            var ids = ImmutableArray.CreateBuilder<ushort>(n);
            for (int i = 0; i < n; i++)
            {
                ids.Add((ushort)U16());
            }

            return new LegacyObjectSpec(typed != 0, atMost != 0, (ushort)cls, (ushort)players, all != 0, (byte)race, ids.MoveToImmutable(), Str());
        }

        public LegacyArea Area()
        {
            int kind = U8();
            int l = unchecked((int)U32());
            int t = unchecked((int)U32());
            int r = unchecked((int)U32());
            int b = unchecked((int)U32());
            int id = U16();
            return new LegacyArea(kind != 0, l, t, r, b, (ushort)id, Str());
        }

        public LegacyHolder Holder()
        {
            int kind = U8();
            int cls = U16();
            int id = U16();
            int players = U16();
            return new LegacyHolder(kind != 0, (ushort)cls, (ushort)id, (ushort)players);
        }
    }
}
