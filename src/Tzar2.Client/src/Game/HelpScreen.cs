using System;
using System.Collections.Generic;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Game.Ui;

namespace Tzar2.Client.Game;

/// <summary>
/// The online help (HUD-020; <c>HELP/HELP_SCR</c>) drawing <see cref="OnlineHelp"/> on <c>BACKGR</c>:
/// <list type="bullet">
/// <item>the chapter buttons down the left (45 × 45 states of <c>TECHTREE</c> … <c>HOTKEYS</c>), then BACK and CLOSE;</item>
/// <item>the topic (its picture from <c>HELP/BMP</c>, title and text) or the chapter's list in the large panel;</item>
/// <item>the topic's links in the middle panel;</item>
/// <item>the civilization selector (<c>BUT_RACE</c>) and the hint of what the pointer is on in the bottom one.</item>
/// </list>
/// The hot keys page is <c>HK.TXT</c> on <c>BACKGR_HKEYS</c>. The original's places on these pictures are not known
/// (no screenshot of its help yet): the panels follow the frames of the art, the rest is ours (UNVERIFIED).
/// </summary>
public partial class HelpScreen : ArtScreen
{
    private const string Art = "art-extra/help/help_scr/";
    private const int Line = 14;
    private static readonly Rect2I TopPanel = new(126, 76, 616, 246);
    private static readonly Rect2I LinkPanel = new(126, 358, 616, 98);
    private static readonly Rect2I HintPanel = new(126, 492, 616, 46);
    private static readonly Color Gold = new(1f, 0.92f, 0.65f);
    private static readonly Color Caption = new(0.85f, 0.85f, 0.75f);

    private static readonly (HelpCategory Category, string Image, string Hint)[] Chapters =
    [
        (HelpCategory.Technologies, "techtree", "tech"),
        (HelpCategory.Units, "units", "unit"),
        (HelpCategory.Buildings, "buildings", "building"),
        (HelpCategory.Items, "items", "item"),
        (HelpCategory.Commands, "commands", "order"),
        (HelpCategory.HowTo, "howto", "howto"),
        (HelpCategory.WhatIs, "whatis", "whatis"),
        (HelpCategory.HotKeys, "hotkeys", "hotkey"),
    ];

    private readonly OnlineHelp _help;
    private readonly IniTable _labels;
    private readonly Control _layer;
    private readonly TextureRect _hint;
    private int _scroll;

    /// <summary>Creates the help on <paramref name="help"/> with the labels of <c>HELP_STR.INI</c>.</summary>
    public HelpScreen(UiAssets ui, Vector2I screen, OnlineHelp help, IniTable labels)
        : base(ui, screen, Art + "backgr.t2img", dim: true, missing: new Vector2I(800, 600))
    {
        ArgumentNullException.ThrowIfNull(help);
        ArgumentNullException.ThrowIfNull(labels);
        _help = help;
        _labels = labels;
        _layer = new Control { MouseFilter = MouseFilterEnum.Ignore, Position = Origin }; // moves with the art (Resize)
        AddChild(_layer);
        _hint = new TextureRect { Position = Origin + HintPanel.Position + new Vector2I(222, 8), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_hint);
        Refresh();
    }

    /// <summary>CLOSE (or Esc).</summary>
    public event Action? Closed;

    /// <summary>The model (test drivers).</summary>
    public OnlineHelp Help => _help;

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Closed?.Invoke();
        }
        else if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel)
        {
            _scroll = Math.Max(0, _scroll + (wheel.ButtonIndex == MouseButton.WheelDown ? 3 : -3));
            Refresh();
        }
    }

    private string L(string section, string key, string fallback) => _labels.Get(section, key) is { Length: > 0 } v ? v : fallback;

    private void Refresh()
    {
        foreach (Node child in _layer.GetChildren())
        {
            child.QueueFree();
        }

        // The chapters down the left, BACK and CLOSE under them.
        for (int i = 0; i < Chapters.Length; i++)
        {
            (HelpCategory category, string image, string hint) = Chapters[i];
            bool on = _help.Category == category && _help.Topic is null;
            IconButton(new Vector2I(34, 40 + (i * 52)), image, on ? 2 : 0, L("help", hint, category.ToString()), () => Go(() => _help.Open(category)));
        }

        IconButton(new Vector2I(34, 40 + (8 * 52) + 12), "back", _help.CanGoBack ? 0 : 3, L("help", "back", "BACK"), () => Go(_help.Back), _help.CanGoBack);
        IconButton(new Vector2I(34, 40 + (9 * 52) + 12), "close", 0, L("help", "close", "CLOSE"), () => Closed?.Invoke());

        if (_help.Category == HelpCategory.HotKeys)
        {
            HotKeys();
        }
        else if (_help.Topic is { } topic)
        {
            TopicPage(topic);
        }
        else
        {
            Chapter();
        }

    }

    private void Go(Action change)
    {
        change();
        _scroll = 0;
        Refresh();
    }

    private void HotKeys()
    {
        Picture(new Vector2I(126, 66), Ui.Texture(Art + "backgr_hkeys.t2img"));
        const int Width = 643 - 40;

        // Each line's words in their colours, wrapped to the panel.
        var rowsOfParts = new List<List<(bool Highlight, string Text)>>();
        foreach (string line in _help.HotKeys)
        {
            var row = new List<(bool, string)>();
            int width = 0;
            foreach ((bool highlight, string text) in HelpText.Parts(line))
            {
                foreach (string word in text.Split(' '))
                {
                    string piece = (width > 0 ? " " : string.Empty) + word;
                    int w = Ui.Measure(piece, UiAssets.SmallFont);
                    if (width + w > Width && row.Count > 0)
                    {
                        rowsOfParts.Add(row);
                        row = [];
                        piece = "    " + word;
                        w = Ui.Measure(piece, UiAssets.SmallFont);
                        width = 0;
                    }

                    row.Add((highlight, piece));
                    width += w;
                }
            }

            rowsOfParts.Add(row);
        }

        int rows = (487 - 40) / Line;
        _scroll = Math.Min(_scroll, Math.Max(0, rowsOfParts.Count - rows));
        for (int i = 0; i < rows && _scroll + i < rowsOfParts.Count; i++)
        {
            int x = 146;
            foreach ((bool highlight, string text) in rowsOfParts[_scroll + i])
            {
                if (Ui.Text(text, UiAssets.SmallFont, highlight ? Gold : Colors.White) is { } t)
                {
                    Picture(new Vector2I(x, 86 + (i * Line)), t);
                    x += t.GetWidth();
                }
            }
        }
    }

    private void Chapter()
    {
        IReadOnlyList<HelpTopic> entries = _help.Entries;
        Label(TopPanel.Position, Title(_help.Category), Gold);
        const int Columns = 3, Row = 34;
        int columnWidth = TopPanel.Size.X / Columns;
        int rows = (TopPanel.Size.Y - 22) / Row;
        _scroll = Math.Min(_scroll, Math.Max(0, ((entries.Count + Columns - 1) / Columns) - rows));
        for (int i = _scroll * Columns; i < entries.Count && i < (_scroll + rows) * Columns; i++)
        {
            HelpTopic t = entries[i];
            int k = i - (_scroll * Columns);
            var at = TopPanel.Position + new Vector2I((k % Columns) * columnWidth, 22 + ((k / Columns) * Row));
            if (t.IconFile > 0)
            {
                Picture(at, Ui.Region($"art-extra/help/bmp/{t.IconFile}s.t2img", new Rect2I(0, t.IconFile is 6 or 7 ? 0 : t.IconIndex * 32, 32, 32)));
            }

            Label(at + new Vector2I(38, 9), Fit(t.Brief, columnWidth - 44, UiAssets.SmallFont), Colors.White);
            string key = t.Key;
            Click(new Rect2I(at, new Vector2I(columnWidth - 4, 32)), () => Go(() => _help.Show(key)), t.Rollover);
        }

        if (_help.HasRaces)
        {
            // The civilization selector (BUT_RACE) and its name, in the bottom panel.
            var at = HintPanel.Position + new Vector2I(0, 2);
            Picture(at, Ui.Region(Art + "but_race.t2img", new Rect2I(0, 0, 210, 27)));
            string race = _help.Race.Length == 0 ? "common" : _help.Race;
            if (Ui.Text(L("help_str", race, race.ToUpperInvariant()), UiAssets.BoldFont, Gold) is { } name)
            {
                Picture(at + new Vector2I((210 - name.GetWidth()) / 2, (27 - name.GetHeight()) / 2), name);
            }

            Click(new Rect2I(at, new Vector2I(210, 27)), () => Go(() => _help.NextRace(+1)), string.Empty);
        }
    }

    private void TopicPage(HelpTopic topic)
    {
        if (topic.IconFile > 0)
        {
            Picture(TopPanel.Position, Ui.Region($"art-extra/help/bmp/{topic.IconFile}.t2img", new Rect2I(0, topic.IconFile is 6 or 7 ? 0 : topic.IconIndex * 48, 48, 48)));
        }

        Picture(TopPanel.Position + new Vector2I(60, 4), Ui.Text(topic.Brief, UiAssets.TitleFont, Gold));
        var lines = new List<string>();
        foreach (string paragraph in topic.Paragraphs)
        {
            lines.AddRange(Wrap(paragraph, TopPanel.Size.X - 64));
            lines.Add(string.Empty);
        }

        int rows = (TopPanel.Size.Y - 40) / Line;
        _scroll = Math.Min(_scroll, Math.Max(0, lines.Count - rows));
        for (int i = 0; i < rows && _scroll + i < lines.Count; i++)
        {
            Label(TopPanel.Position + new Vector2I(60, 40 + (i * Line)), lines[_scroll + i], Colors.White);
        }

        // The links: one line per group, each topic a link.
        int y = LinkPanel.Position.Y;
        foreach ((HelpLinkKind kind, System.Collections.Immutable.ImmutableArray<string> topics) in topic.Links)
        {
            if (y + Line > LinkPanel.End.Y)
            {
                break;
            }

            string caption = kind switch
            {
                HelpLinkKind.Makes => L("help_str", "info", "Info"),
                HelpLinkKind.BuiltIn => L("help_str", "built", "Built in"),
                HelpLinkKind.ResearchedIn => L("help_str", "res", "Researched in"),
                HelpLinkKind.Requires => L("help_str", "requ", "Requires"),
                HelpLinkKind.Allows => L("help_str", "allow", "Allows"),
                _ => L("help_str", "also", "See also"),
            } + ":";
            int x = LinkPanel.Position.X;
            if (Ui.Text(caption, UiAssets.SmallFont, Caption) is { } c)
            {
                Picture(new Vector2I(x, y), c);
                x += c.GetWidth() + 8;
            }

            foreach (string key in topics)
            {
                if (_help.Find(key) is not { } target || Ui.Text(target.Brief, UiAssets.SmallFont, Gold) is not { } t)
                {
                    continue;
                }

                if (x + t.GetWidth() > LinkPanel.End.X)
                {
                    break;
                }

                Picture(new Vector2I(x, y), t);
                Click(new Rect2I(x, y, t.GetWidth(), Line), () => Go(() => _help.Show(key)), target.Rollover);
                x += t.GetWidth() + 12;
            }

            y += Line + 4;
        }
    }

    private string Title(HelpCategory category)
    {
        foreach ((HelpCategory c, _, string hint) in Chapters)
        {
            if (c == category)
            {
                return L("help", hint, category.ToString());
            }
        }

        return category.ToString();
    }

    // A 45 × 45 button of a 180 × 45 strip (normal, lit, pressed, disabled; UNVERIFIED order).
    private void IconButton(Vector2I at, string image, int state, string hint, Action pressed, bool enabled = true)
    {
        Picture(at, Ui.Region(Art + image + ".t2img", new Rect2I(state * 45, 0, 45, 45)));
        if (enabled)
        {
            Click(new Rect2I(at, new Vector2I(45, 45)), pressed, hint);
        }
    }

    private void Label(Vector2I at, string text, Color colour)
    {
        if (text.Length > 0 && Ui.Text(text, UiAssets.SmallFont, colour) is { } t)
        {
            Picture(at, t);
        }
    }

    private void Picture(Vector2I at, Texture2D? texture)
    {
        if (texture is not null)
        {
            _layer.AddChild(new TextureRect { Texture = texture, Position = at, MouseFilter = MouseFilterEnum.Ignore });
        }
    }

    private void Click(Rect2I rect, Action pressed, string hint)
    {
        var button = new Button { Position = rect.Position, Size = rect.Size, Flat = true, Modulate = new Color(1, 1, 1, 0.02f) };
        button.Pressed += () => Callable.From(pressed).CallDeferred();
        if (hint.Length > 0)
        {
            // The hint of what the pointer is on, in the bottom panel (right of the civilization selector).
            button.MouseEntered += () => _hint.Texture = Ui.Text(Fit(hint, HintPanel.Size.X - 222, UiAssets.SmallFont), UiAssets.SmallFont, Caption);
            button.MouseExited += () => _hint.Texture = null;
        }

        _layer.AddChild(button);
    }
}
