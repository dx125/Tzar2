using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Game.Ui;

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

    /// <summary>Milking a cow (<c>image_milk</c>).</summary>
    Milk,

    /// <summary>Walking with logs (the base image + <c>_tree</c>).</summary>
    CarryWood,

    /// <summary>Walking with gold (<c>_gold</c>).</summary>
    CarryGold,

    /// <summary>Walking with stone (<c>_stone</c>).</summary>
    CarryStone,

    /// <summary>Walking with food from a field (<c>_food</c>).</summary>
    CarryFood,

    /// <summary>Walking with a pail of milk (<c>image_carrymilk</c>).</summary>
    CarryMilk,

    /// <summary>Walking with meat (<c>image_carrymeat</c>).</summary>
    CarryMeat,
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

/// <summary>
/// Joins the compiled ruleset with the imported presentation tables (object INIs, IMAGES.INI names, ICONS.INI,
/// MENU.INI, ORDERS.INI, SOUNDS.INI, KEYS.INI, STRINGS.INI): the client's single source of "what does type X look like,
/// sound like and offer on its command card" (ADR 0071). Godot-free and unit-tested on synthetic tables.
/// </summary>
public sealed class PresentationCatalog : ICardSource
{
    /// <summary>Folder of the object INIs.</summary>
    public const string ObjectsFolder = "tables/data/objects/";

    private static readonly (UnitAnim Anim, string Key)[] AnimKeys =
    [
        (UnitAnim.Base, "image"), (UnitAnim.Idle, "image_idle"), (UnitAnim.Attack, "image_attack"), (UnitAnim.Die, "image_die"),
        (UnitAnim.Chop, "image_chop"), (UnitAnim.Mine, "image_mine"), (UnitAnim.Farm, "image_dig"), (UnitAnim.Build, "image_build"),
        (UnitAnim.Milk, "image_milk"), (UnitAnim.CarryMilk, "image_carrymilk"), (UnitAnim.CarryMeat, "image_carrymeat"),
    ];

    // Carrying sprites the object INIs do not list: IMAGES.INI names them after the walk image (e_peon_tree, e_peon_gold...).
    private static readonly (UnitAnim Anim, string Suffix)[] CarrySuffixes =
    [
        (UnitAnim.CarryWood, "_tree"), (UnitAnim.CarryGold, "_gold"), (UnitAnim.CarryStone, "_stone"), (UnitAnim.CarryFood, "_food"),
    ];

    private readonly TypeVisual[] _types;
    private readonly IniTable _menus;
    private readonly IniTable _orders;
    private Dictionary<string, int>? _imageByClass;
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

    /// <inheritdoc/>
    public int ItemIcon(ushort typeId, string? look) => IconIndex(look ?? _types[typeId].Image(UnitAnim.Base));

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
            (CardAction kind, string argument) = Classify(action, submenu, o?.Get("CLASSNAME"));
            if (kind == CardAction.Stance && argument == "toggle")
            {
                // holdfire and fireatwill share the action; the order says which way it goes.
                argument = string.Equals(order, "fireatwill", StringComparison.OrdinalIgnoreCase) ? "0" : "2";
            }

            buttons.Add(new CardButton(order, hotkey, kind, argument, image, tooltip));
        }

        return buttons;
    }

    /// <inheritdoc/>
    public bool CapturesKeys(string? menu)
        => _menus.Section(menu) is { } section
            && section.Entries.Any(e => e.Key is null && string.Equals(e.Value.Trim(), "capturekeys", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps an ORDERS.INI <c>ACTION</c> (with the order's <c>CLASSNAME</c>: the technology or upgrade key) onto what
    /// this build can do.
    /// </summary>
    public static (CardAction Action, string Argument) Classify(string action, string? submenu, string? className = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        string[] words = action.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string verb = words.Length > 0 ? words[0].ToLowerInvariant() : string.Empty;
        string argument = words.Length > 1 ? words[1] : string.Empty;
        if (verb == "get" && argument.ToLowerInvariant() is "heroes" or "spys" or "items")
        {
            // The original's HEROES, SPYS and ITEMS menus are empty and filled at run time: the card's kingdom lists.
            return (CardAction.KingdomList, argument.ToLowerInvariant() == "spys" ? "spies" : argument.ToLowerInvariant());
        }

        if (!string.IsNullOrEmpty(submenu))
        {
            return (CardAction.Submenu, submenu);
        }

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
            "technology" when !string.IsNullOrEmpty(className) => (CardAction.Research, className),
            "upgrade" when !string.IsNullOrEmpty(className) => (CardAction.Upgrade, className),
            "capture" => (CardAction.Capture, string.Empty),
            "move_advance" or "default_adv" => (CardAction.MoveAdvance, string.Empty),
            "move_sneak" or "default_sneak" => (CardAction.MoveSneak, string.Empty),
            "stand_ground" or "hold_steady" => (CardAction.Stance, "1"),
            "holdfire" => (CardAction.Stance, "toggle"),
            "gateopen" => (CardAction.Gate, "open"),
            "gateclose" => (CardAction.Gate, "close"),
            "gate_subai_on" => (CardAction.Gate, "auto"),
            "gate_subai_off" => (CardAction.Gate, "manual"),
            "explore" => (CardAction.Explore, string.Empty),
            "die" => (CardAction.Delete, string.Empty),
            "emerge" or "unload" => (CardAction.Unload, string.Empty),
            "become_priest" => (CardAction.Become, "priest"),
            "become_jihad_warrior" => (CardAction.Become, "jihad_warrior"),
            "attack_ground" => (CardAction.AttackGround, string.Empty),
            "next_idle_peon" => (CardAction.NextIdle, string.Empty),
            "view_quests" => (CardAction.Quests, string.Empty),
            "uncloak" => (CardAction.Uncloak, string.Empty),
            "impersonate" => (CardAction.Impersonate, string.Empty),
            "reveal_self" => (CardAction.Reveal, string.Empty),
            "set" when argument.ToLowerInvariant() is "ai_none" or "ai_eco" or "ai_war" or "ai_full" => (CardAction.AiAssist, argument.ToLowerInvariant() switch
            {
                "ai_eco" => "1",
                "ai_war" => "2",
                "ai_full" => "3",
                _ => "0",
            }),
            "spell" when argument.Length > 0 => (CardAction.Spell, argument.ToLowerInvariant()),
            "raisedead" or "cloak" or "murder" => (CardAction.Spell, verb),
            "mine" or "dig" or "chop" or "fishing" => (CardAction.Harvest, string.Empty),
            "loan" when argument.Length > 0 => (CardAction.Loan, argument.ToLowerInvariant()),
            "loan_return" => (CardAction.Loan, "return"),
            "bribe" => (CardAction.Bribe, string.Empty),
            "select_crusaders" => (CardAction.Crusade, string.Empty),
            "pick_item" => (CardAction.PickItem, string.Empty),
            "item_use" => (CardAction.UseItem, string.Empty),
            "item_use_instant" => (CardAction.UseItem, "self"),
            "item_drop" => (CardAction.DropItem, string.Empty),
            "gamble" => (CardAction.Gamble, string.Empty),
            "trade1" => (CardAction.Exchange, string.Empty),
            "trade" => (CardAction.Trade, string.Empty),
            "build_item" => (CardAction.Craft, string.Empty),
            _ => (CardAction.Unsupported, action),
        };
    }

    /// <summary>
    /// The <c>orders/buttons.bmp</c> cell of the ORDERS.INI order whose <c>CLASSNAME</c> is <paramref name="className"/>
    /// (a technology or upgrade key: <c>te_feudalism</c>, <c>to_gate_stone</c>), or −1.
    /// </summary>
    public int ButtonImage(string className)
    {
        if (_imageByClass is null)
        {
            _imageByClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (IniSection o in _orders.Sections)
            {
                if (o.Get("CLASSNAME") is { Length: > 0 } cls && o.Get("IMG") is { } img
                    && int.TryParse(img, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ix))
                {
                    _imageByClass.TryAdd(cls, ix);
                }
            }
        }

        return _imageByClass.TryGetValue(className, out int image) ? image : -1;
    }

    /// <summary>The <c>orders/buttons.bmp</c> cell of the order that trains class <paramref name="cls"/> (<c>create footman</c>), or −1.</summary>
    public int TrainImage(string cls)
    {
        ArgumentNullException.ThrowIfNull(cls);
        foreach (IniSection o in _orders.Sections)
        {
            if (o.Get("ACTION") is { } action && string.Equals(action.Trim(), "create " + cls, StringComparison.OrdinalIgnoreCase)
                && o.Get("IMG") is { } img && int.TryParse(img, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ix))
            {
                return ix;
            }
        }

        return -1;
    }

    /// <inheritdoc/>
    public int TrainImage(ushort typeId) => TrainImage(_types[typeId].Class);

    /// <inheritdoc/>
    public string? Menu(ushort typeId) => _types[typeId].Menu;

    /// <inheritdoc/>
    public string Civilization(ushort typeId) => _types[typeId].Civilization;

    /// <summary>The <c>orders/buttons.bmp</c> cell of ORDERS.INI order <paramref name="order"/> (its IMG), or −1.</summary>
    public int OrderImage(string order)
        => _orders.Section(order)?.Get("IMG") is { } img && int.TryParse(img, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ? index : -1;

    /// <inheritdoc/>
    public IReadOnlyList<CardButton> AbilityButtons(string ability) => ability == "impersonate" ? Card("spy_add") : [];

    /// <inheritdoc/>
    public string? MenuVariant(string? menu, string state)
        => menu is not null && _menus.Section(menu + "_" + state) is not null ? menu + "_" + state : null;

    /// <inheritdoc/>
    public string? KingdomMenu(bool aiAssist) => _menus.Section(aiAssist ? "ownership_ai" : "ownership") is null ? null : aiAssist ? "ownership_ai" : "ownership";

    /// <inheritdoc/>
    public string Name(ushort typeId) => _types[typeId].Name;

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

                if (images.ContainsKey(UnitAnim.Chop) && images.TryGetValue(UnitAnim.Base, out string? walk))
                {
                    foreach ((UnitAnim carry, string ending) in CarrySuffixes)
                    {
                        images[carry] = walk + ending; // a missing entry draws the walk image instead
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
