using System;
using System.Collections.Generic;
using System.Globalization;
using Tzar2.Content.Compiled;

namespace Tzar2.Client.Logic;

/// <summary>The animations the renderer picks from per entity state.</summary>
public enum UnitAnim
{
    /// <summary>Walk (units) or the building / scenery image itself.</summary>
    Base = 0,

    /// <summary>Standing.</summary>
    Idle,

    /// <summary>Attack swing.</summary>
    Attack,

    /// <summary>Death (the last frame stays as the corpse).</summary>
    Die,

    /// <summary>Chopping wood.</summary>
    Chop,

    /// <summary>Mining gold or stone.</summary>
    Mine,

    /// <summary>Gathering food.</summary>
    Farm,

    /// <summary>Building.</summary>
    Build,
}

/// <summary>How an entity type looks and sounds, from the user's imported object INIs (never shipped).</summary>
/// <param name="TypeId">Ruleset type id.</param>
/// <param name="Key">Ruleset key (<c>euro.peon</c>).</param>
/// <param name="Class">Original class (<c>peon</c>).</param>
/// <param name="Civilization">Civilization key.</param>
/// <param name="Name">Display name (STRINGS.INI <c>[objects]</c>).</param>
/// <param name="Images">IMAGES.INI entry names per animation.</param>
/// <param name="Menu">MENU.INI section of the command card, or null.</param>
/// <param name="IconIndex">Cell of <c>icons.bmp</c> (48×48), or −1.</param>
/// <param name="SoundSelect">Sound set or file played on selection, or null.</param>
/// <param name="SoundCommand">Sound set played when ordered (acknowledgement), or null.</param>
/// <param name="SoundAttack">Sound set of a hit, or null.</param>
/// <param name="SoundDie">Sound set of a death, or null.</param>
public sealed record TypeVisual(
    ushort TypeId,
    string Key,
    string Class,
    string Civilization,
    string Name,
    IReadOnlyDictionary<UnitAnim, string> Images,
    string? Menu,
    int IconIndex,
    string? SoundSelect,
    string? SoundCommand,
    string? SoundAttack,
    string? SoundDie)
{
    /// <summary>The image of <paramref name="anim"/>, falling back to the base image.</summary>
    public string? Image(UnitAnim anim)
        => Images.TryGetValue(anim, out string? name) ? name : Images.TryGetValue(UnitAnim.Base, out string? b) ? b : null;
}

/// <summary>What a command-card button does.</summary>
public enum CardAction
{
    /// <summary>Not supported by this build (shown disabled).</summary>
    Unsupported = 0,
#pragma warning disable CS1591 // Named after ORDERS.INI actions.
    Move, Attack, Harvest, Patrol, Guard, Repair, Stop, Train, Build, Submenu, Back, Cancel,
#pragma warning restore CS1591
}

/// <summary>A button of a command card (MENU.INI entry + ORDERS.INI order).</summary>
/// <param name="Order">ORDERS.INI section name.</param>
/// <param name="Hotkey">Letter, or '\0'.</param>
/// <param name="Action">What it does.</param>
/// <param name="Argument">Class to train or build, or the submenu name.</param>
/// <param name="ImageIndex">Cell of <c>orders/buttons.bmp</c> (48×48), or −1.</param>
/// <param name="Tooltip">STRINGS.INI <c>[orders_names]</c> text.</param>
public sealed record CardButton(string Order, char Hotkey, CardAction Action, string Argument, int ImageIndex, string Tooltip);

/// <summary>
/// Joins the compiled ruleset with the imported presentation tables (object INIs, IMAGES.INI names, ICONS.INI,
/// MENU.INI, ORDERS.INI, SOUNDS.INI, KEYS.INI, STRINGS.INI): the client's single source of "what does type X look like,
/// sound like and offer on its command card" (ADR 0071). Godot-free and unit-tested on synthetic tables.
/// </summary>
public sealed class PresentationCatalog
{
    /// <summary>Folder of the object INIs.</summary>
    public const string ObjectsFolder = "tables/data/objects/";

    private static readonly (UnitAnim Anim, string Key)[] AnimKeys =
    [
        (UnitAnim.Base, "image"), (UnitAnim.Idle, "image_idle"), (UnitAnim.Attack, "image_attack"), (UnitAnim.Die, "image_die"),
        (UnitAnim.Chop, "image_chop"), (UnitAnim.Mine, "image_mine"), (UnitAnim.Farm, "image_dig"), (UnitAnim.Build, "image_build"),
    ];

    private readonly TypeVisual[] _types;
    private readonly IniTable _menus;
    private readonly IniTable _orders;
    private readonly IniTable _sounds;
    private readonly IniTable _strings;
    private readonly IniTable _icons;
    private readonly Dictionary<string, string[]> _keyClasses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the catalog.</summary>
    /// <param name="ruleset">The compiled ruleset.</param>
    /// <param name="read">Reads a package file (null when missing).</param>
    /// <param name="objectTables">Paths of the object INI tables (<see cref="ObjectsFolder"/>*.ini.json).</param>
    public PresentationCatalog(Ruleset ruleset, Func<string, byte[]?> read, IEnumerable<string> objectTables)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(objectTables);
        Ruleset = ruleset;
        IniTable Load(string path) => read(path) is { } bytes ? IniTable.Parse(bytes) : IniTable.Empty;
        _menus = Load("tables/data/menu.ini.json");
        _orders = Load("tables/data/orders.ini.json");
        _sounds = Load("tables/data/sounds.ini.json");
        _strings = Load("tables/local/strings.ini.json");
        _icons = Load("tables/data/icons.ini.json");
        IniTable keys = Load("tables/data/keys.ini.json");
        if (keys.Section("Ctrl") is { } ctrl)
        {
            foreach (KeyValuePair<string?, string> e in ctrl.Entries)
            {
                if (e.Key is { Length: 1 })
                {
                    _keyClasses[e.Key] = e.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                }
            }
        }

        var byClass = new Dictionary<string, IniTable>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in objectTables)
        {
            IniTable ini = Load(path);
            if (ini.Get("general", "class") is { Length: > 0 } cls)
            {
                byClass.TryAdd(cls, ini);
            }
        }

        _types = new TypeVisual[ruleset.EntityTypes.Count];
        for (int i = 0; i < _types.Length; i++)
        {
            _types[i] = Describe(ruleset.EntityTypes[i], byClass);
        }
    }

    /// <summary>The ruleset.</summary>
    public Ruleset Ruleset { get; }

    /// <summary>The visual of type <paramref name="typeId"/>.</summary>
    public TypeVisual Type(ushort typeId) => _types[typeId];

    /// <summary>A STRINGS.INI text, or <paramref name="fallback"/>.</summary>
    public string Text(string section, string key, string fallback)
    {
        string? s = _strings.Get(section, key);
        if (string.IsNullOrEmpty(s))
        {
            return fallback;
        }

        // "TITLE| tooltip" entries: the part before the bar is the label.
        int bar = s.IndexOf('|', StringComparison.Ordinal);
        return bar >= 0 ? s[..bar].Trim() : s;
    }

    /// <summary>The classes KEYS.INI binds to Ctrl+<paramref name="letter"/>.</summary>
    public IReadOnlyList<string> ClassesOfKey(char letter)
        => _keyClasses.TryGetValue(letter.ToString(), out string[]? classes) ? classes : [];

    /// <summary>The icons.bmp cell of an ICONS.INI section, or −1.</summary>
    public int IconIndex(string? name)
        => name is not null && _icons.Get(name, "index") is { } v && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : -1;

    /// <summary>
    /// The files of a sound set (SOUNDS.INI section of <c>file = weight</c> lines) or of a direct file reference, as
    /// package paths (<c>sound/…</c>) with their weights.
    /// </summary>
    public IReadOnlyList<(string Path, int Weight)> Sounds(string? set)
    {
        if (string.IsNullOrWhiteSpace(set))
        {
            return [];
        }

        if (_sounds.Section(set) is { } section)
        {
            var list = new List<(string, int)>();
            foreach (KeyValuePair<string?, string> e in section.Entries)
            {
                if (e.Key is { Length: > 0 } file && file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(e.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int weight);
                    list.Add((SoundPath(file), Math.Max(1, weight)));
                }
            }

            return list;
        }

        return set.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? [(SoundPath(set), 1)] : [];
    }

    /// <summary>SOUNDS.INI <c>[priority]</c> of a category (0-255; default 128).</summary>
    public int SoundPriority(string category)
        => _sounds.Get("priority", category) is { } v && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : 128;

    /// <summary>The package path of a SOUNDS.INI file reference (<c>command\foot1.wav</c> → <c>sound/command/foot1.wav</c>).</summary>
    public static string SoundPath(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return "sound/" + file.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }

    /// <summary>The command card of MENU.INI section <paramref name="menu"/>.</summary>
    public IReadOnlyList<CardButton> Card(string? menu)
    {
        var buttons = new List<CardButton>();
        if (_menus.Section(menu) is not { } section)
        {
            return buttons;
        }

        foreach (KeyValuePair<string?, string> e in section.Entries)
        {
            if (e.Key is null || string.Equals(e.Key, "default", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string order = e.Key;
            char hotkey = e.Value.Length == 1 ? char.ToUpperInvariant(e.Value[0]) : '\0';
            IniSection? o = _orders.Section(order);
            string action = o?.Get("ACTION") ?? string.Empty;
            string? submenu = o?.Get("SUBMENU");
            int image = o?.Get("IMG") is { } img && int.TryParse(img, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ix) ? ix : -1;
            string tooltip = _strings.Get("orders_names", order) ?? order;
            (CardAction kind, string argument) = Classify(action, submenu);
            buttons.Add(new CardButton(order, hotkey, kind, argument, image, tooltip));
        }

        return buttons;
    }

    /// <summary>Maps an ORDERS.INI <c>ACTION</c> onto what this build can do.</summary>
    public static (CardAction Action, string Argument) Classify(string action, string? submenu)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!string.IsNullOrEmpty(submenu))
        {
            return (CardAction.Submenu, submenu);
        }

        string[] words = action.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string verb = words.Length > 0 ? words[0].ToLowerInvariant() : string.Empty;
        string argument = words.Length > 1 ? words[1] : string.Empty;
        return verb switch
        {
            "move" => (CardAction.Move, string.Empty),
            "attack" => (CardAction.Attack, string.Empty),
            "harvest" => (CardAction.Harvest, string.Empty),
            "patrol" => (CardAction.Patrol, string.Empty),
            "guard" => (CardAction.Guard, string.Empty),
            "repair" => (CardAction.Repair, string.Empty),
            "stop" => (CardAction.Stop, string.Empty),
            "create" => (CardAction.Train, argument),
            "build" => (CardAction.Build, argument),
            _ => (CardAction.Unsupported, action),
        };
    }

    /// <summary>The ruleset type a card button trains or builds for <paramref name="civilization"/>, or −1.</summary>
    public int TypeOfClass(string civilization, string cls)
    {
        int id = Ruleset.EntityTypeId(civilization + "." + cls);
        return id >= 0 ? id : Ruleset.EntityTypeId("comm." + cls);
    }

    private TypeVisual Describe(EntityTypeDefinition type, Dictionary<string, IniTable> byClass)
    {
        string cls = type.Traits.LegacyClass;
        string civ = type.Traits.Civilization;
        if (cls.Length == 0)
        {
            int dot = type.Key.IndexOf('.', StringComparison.Ordinal);
            cls = dot >= 0 ? type.Key[(dot + 1)..] : type.Key;
            civ = dot >= 0 ? type.Key[..dot] : string.Empty;
        }

        var images = new Dictionary<UnitAnim, string>();
        string? menu = null, icon = null, select = null, command = null, attack = null, die = null;
        if (byClass.TryGetValue(cls, out IniTable? ini))
        {
            IniSection? section = ini.Section(civ) ?? FirstOtherThanGeneral(ini);
            if (section is not null)
            {
                foreach ((UnitAnim anim, string key) in AnimKeys)
                {
                    if (section.Get(key) is { Length: > 0 } image)
                    {
                        images[anim] = image;
                    }
                }

                menu = section.Get("menu");
                icon = section.Get("icon");
                select = section.Get("sound_select");
                command = section.Get("sound_command");
                attack = section.Get("sound_attack");
                die = section.Get("sound_die");
            }
        }

        string suffix = civ switch { "euro" => " e", "asia" => " a", "arab" => " r", _ => string.Empty };
        string name = _strings.Get("objects", cls + suffix) ?? _strings.Get("objects", cls) ?? cls;
        return new TypeVisual(type.Id, type.Key, cls, civ, name, images, Empty(menu), IconIndex(Empty(icon) ?? (images.TryGetValue(UnitAnim.Base, out string? b) ? b : cls)), Empty(select), Empty(command), Empty(attack), Empty(die));
    }

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static IniSection? FirstOtherThanGeneral(IniTable ini)
    {
        foreach (IniSection s in ini.Sections)
        {
            if (!string.Equals(s.Name, "general", StringComparison.OrdinalIgnoreCase) && s.Get("image") is not null)
            {
                return s;
            }
        }

        return null;
    }
}
