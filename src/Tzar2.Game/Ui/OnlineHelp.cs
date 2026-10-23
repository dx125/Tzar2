using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Tzar2.Game.Ui;

/// <summary>The chapters of the online help, in the order of its buttons (<c>HELP_STR.INI [help]</c>).</summary>
public enum HelpCategory
{
    /// <summary>The technology trees (<c>T_</c> topics).</summary>
    Technologies,

    /// <summary>The units (<c>U_</c>).</summary>
    Units,

    /// <summary>The buildings (<c>B_</c>).</summary>
    Buildings,

    /// <summary>The items (<c>I_</c>).</summary>
    Items,

    /// <summary>The commands (<c>C_</c>).</summary>
    Commands,

    /// <summary>How to? (<c>HT_</c>).</summary>
    HowTo,

    /// <summary>What is? (<c>WI_</c>).</summary>
    WhatIs,

    /// <summary>The hot keys (<c>HK.TXT</c>).</summary>
    HotKeys,
}

/// <summary>A group of links on a topic page, captioned by <c>HELP_STR.INI [help_str]</c>.</summary>
public enum HelpLinkKind
{
    /// <summary>What this building makes or researches (its <c>row1</c>-<c>rowN</c>; <c>info</c>).</summary>
    Makes,

    /// <summary>Where a unit is made (<c>related</c> of a unit; <c>built</c>, "Built in").</summary>
    BuiltIn,

    /// <summary>Where a technology is researched (<c>related</c> of a technology; <c>res</c>, "Researched in").</summary>
    ResearchedIn,

    /// <summary>What it needs (<c>requires</c>; <c>requ</c>).</summary>
    Requires,

    /// <summary>What it allows (<c>allows</c>; <c>allow</c>).</summary>
    Allows,

    /// <summary>Other topics (<c>also</c>; "See also").</summary>
    SeeAlso,
}

/// <summary>A topic of the online help (a section of <c>HELP/ITEMS.TXT</c>).</summary>
/// <param name="Key">Its section, e.g. <c>U_E_PEASANT</c>.</param>
/// <param name="Category">Its chapter (from the key's prefix).</param>
/// <param name="Race">Its civilization (<c>euro</c>, <c>arab</c>, <c>asia</c>), or empty for a common one.</param>
/// <param name="Brief">Its title.</param>
/// <param name="Rollover">Its hint.</param>
/// <param name="Paragraphs">Its text, split at the original's <c>~</c>.</param>
/// <param name="IconFile">The <c>HELP/BMP/N.BMP</c> strip of its picture, or 0.</param>
/// <param name="IconIndex">The 48 × 48 cell in the strip, from 0.</param>
/// <param name="Links">Its groups of links to other topics.</param>
public sealed record HelpTopic(string Key, HelpCategory Category, string Race, string Brief, string Rollover, ImmutableArray<string> Paragraphs, int IconFile, int IconIndex,
    ImmutableArray<(HelpLinkKind Kind, ImmutableArray<string> Topics)> Links);

/// <summary>
/// The online help (HUD-020, HUD-033): the encyclopedia of <c>HELP/ITEMS.TXT</c> in chapters (technologies, units,
/// buildings and items by civilization, commands, how to, what is) and the hot keys of <c>HELP/HK.TXT</c>; a topic's page
/// with its text and links; BACK through the pages seen. Links to topics the file does not have are left out.
/// </summary>
public sealed class OnlineHelp
{
    /// <summary>The civilizations of the race selector, in <c>HELP_STR.INI</c> order (common first).</summary>
    public static readonly ImmutableArray<string> Races = ["", "euro", "arab", "asia"];

    private readonly Dictionary<string, HelpTopic> _byKey;
    private readonly Stack<(HelpCategory, string, string?)> _history = new();

    /// <summary>
    /// The help of <paramref name="sections"/> (the sections of ITEMS.TXT in file order: name and keys) and the lines of
    /// HK.TXT (<paramref name="hotKeys"/>).
    /// </summary>
    public OnlineHelp(IEnumerable<(string Name, IReadOnlyDictionary<string, string> Keys)> sections, IReadOnlyList<string> hotKeys)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(hotKeys);
        HotKeys = [.. hotKeys];
        var raw = new List<(string, IReadOnlyDictionary<string, string>, HelpCategory)>();
        foreach ((string name, IReadOnlyDictionary<string, string> keys) in sections)
        {
            if (CategoryOf(name) is { } category && !raw.Exists(r => string.Equals(r.Item1, name, StringComparison.OrdinalIgnoreCase)))
            {
                raw.Add((name.ToUpperInvariant(), keys, category));
            }
        }

        var known = raw.Select(r => r.Item1).ToHashSet(StringComparer.Ordinal);
        Topics = [.. raw.Select(r => Parse(r.Item1, r.Item2, r.Item3, known))];
        _byKey = Topics.ToDictionary(t => t.Key, StringComparer.Ordinal);
    }

    /// <summary>Every topic, in file order.</summary>
    public ImmutableArray<HelpTopic> Topics { get; }

    /// <summary>The lines of the hot keys page (<c>@1</c> starts the highlighted part of a line, <c>@2</c> ends it).</summary>
    public ImmutableArray<string> HotKeys { get; }

    /// <summary>The chapter shown (<see cref="HelpCategory.HotKeys"/>: the hot keys page).</summary>
    public HelpCategory Category { get; private set; } = HelpCategory.Units;

    /// <summary>The civilization the chapter lists (empty: the common topics).</summary>
    public string Race { get; private set; } = "euro";

    /// <summary>The topic shown, or null for the chapter's list.</summary>
    public HelpTopic? Topic { get; private set; }

    /// <summary>Whether BACK has a page to go back to.</summary>
    public bool CanGoBack => _history.Count > 0;

    /// <summary>Whether the chapter shown is listed by civilization (technologies, units, buildings).</summary>
    public bool HasRaces => Category is HelpCategory.Technologies or HelpCategory.Units or HelpCategory.Buildings;

    /// <summary>The topics of the chapter shown (and its civilization), in file order.</summary>
    public IReadOnlyList<HelpTopic> Entries => Category == HelpCategory.HotKeys
        ? []
        : [.. Topics.Where(t => t.Category == Category && (!HasRaces || t.Race == Race))];

    /// <summary>The topic <paramref name="key"/>, or null.</summary>
    public HelpTopic? Find(string key) => key is not null && _byKey.TryGetValue(key.ToUpperInvariant(), out HelpTopic? t) ? t : null;

    /// <summary>Opens chapter <paramref name="category"/>'s list (keeping the civilization).</summary>
    public void Open(HelpCategory category)
    {
        Remember();
        Category = category;
        Topic = null;
    }

    /// <summary>Opens topic <paramref name="key"/> (a link); nothing for a topic the help does not have.</summary>
    public void Show(string key)
    {
        if (Find(key) is not { } topic)
        {
            return;
        }

        Remember();
        Category = topic.Category;
        if (topic.Category is HelpCategory.Technologies or HelpCategory.Units or HelpCategory.Buildings)
        {
            Race = topic.Race;
        }

        Topic = topic;
    }

    /// <summary>Shows the chapter's topics of the next civilization (the race selector).</summary>
    public void NextRace(int step)
    {
        if (!HasRaces)
        {
            return;
        }

        int i = Races.IndexOf(Race);
        Race = Races[(((i + step) % Races.Length) + Races.Length) % Races.Length];
        Topic = null;
    }

    /// <summary>BACK: the page seen before.</summary>
    public void Back()
    {
        if (_history.TryPop(out (HelpCategory Category, string Race, string? Topic) page))
        {
            (Category, Race) = (page.Category, page.Race);
            Topic = page.Topic is null ? null : Find(page.Topic);
        }
    }

    /// <summary>The chapter of a topic key (by its prefix), or null for a section that is no topic.</summary>
    public static HelpCategory? CategoryOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string k = key.ToUpperInvariant();
        return k switch
        {
            _ when k.StartsWith("HT_", StringComparison.Ordinal) => HelpCategory.HowTo,
            _ when k.StartsWith("WI_", StringComparison.Ordinal) => HelpCategory.WhatIs,
            _ when k.StartsWith("U_", StringComparison.Ordinal) => HelpCategory.Units,
            _ when k.StartsWith("B_", StringComparison.Ordinal) => HelpCategory.Buildings,
            _ when k.StartsWith("T_", StringComparison.Ordinal) => HelpCategory.Technologies,
            _ when k.StartsWith("I_", StringComparison.Ordinal) => HelpCategory.Items,
            _ when k.StartsWith("C_", StringComparison.Ordinal) => HelpCategory.Commands,
            _ => null,
        };
    }

    private void Remember() => _history.Push((Category, Race, Topic?.Key));

    private static HelpTopic Parse(string key, IReadOnlyDictionary<string, string> keys, HelpCategory category, HashSet<string> known)
    {
        string Get(string name) => keys.TryGetValue(name, out string? v) ? v.Trim() : string.Empty;
        ImmutableArray<string> List(string text) => [.. text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant()).Where(s => known.Contains(s) && s != key).Distinct()];

        // The civilization: the second part of U_, B_ and T_ keys (E, R the Arabian, A; C or none: common).
        string race = string.Empty;
        if (category is HelpCategory.Units or HelpCategory.Buildings or HelpCategory.Technologies)
        {
            string[] parts = key.Split('_');
            race = parts.Length > 2 ? parts[1] switch { "E" => "euro", "R" => "arab", "A" => "asia", _ => string.Empty } : string.Empty;
        }

        var links = ImmutableArray.CreateBuilder<(HelpLinkKind, ImmutableArray<string>)>();
        void Add(HelpLinkKind kind, ImmutableArray<string> topics)
        {
            if (!topics.IsEmpty)
            {
                links.Add((kind, topics));
            }
        }

        Add(HelpLinkKind.Makes, [.. Enumerable.Range(1, 16).SelectMany(i => List(Get("row" + i))).Distinct()]);
        Add(category == HelpCategory.Technologies ? HelpLinkKind.ResearchedIn : HelpLinkKind.BuiltIn, List(Get("related")));
        Add(HelpLinkKind.Requires, List(Get("requires")));
        Add(HelpLinkKind.Allows, List(Get("allows")));
        Add(HelpLinkKind.SeeAlso, List(Get("also")));

        string[] icon = Get("icon").Split(',', StringSplitOptions.TrimEntries);
        int file = icon.Length > 0 && int.TryParse(icon[0], out int f) && f > 0 ? f : 0;
        int index = icon.Length > 1 && int.TryParse(icon[1], out int ix) && ix > 0 ? ix - 1 : 0;
        ImmutableArray<string> paragraphs = [.. Get("detail").Split('~', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        return new HelpTopic(key, category, race, Get("brief"), Get("rollover"), paragraphs, file, index, links.ToImmutable());
    }
}
