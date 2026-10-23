using System;
using System.Collections.Generic;
using Godot;
using Tzar2.Client.Logic;
using Tzar2.Game.Scenario;

namespace Tzar2.Client.Game;

/// <summary>
/// Text helpers shared by the scenario dialogs: word-wrapped lines in the original fonts. The dialog art comes from the
/// imported <c>IMAGES/SCREENS</c> (REQ-UI-02); positions inside them are first estimates (UNVERIFIED until measured
/// from captures, RE-09; ADR 0132).
/// </summary>
public abstract partial class ScenarioDialog : ArtScreen
{
    /// <summary>Creates a dialog over the game (dimmed).</summary>
    protected ScenarioDialog(UiAssets ui, Vector2I screen, string background)
        : base(ui, screen, background, dim: true)
    {
    }

    /// <summary>Splits <paramref name="text"/> into lines no wider than <paramref name="width"/> pixels (also at line breaks).</summary>
    protected List<string> Wrap(string text, int width, string font = UiAssets.SmallFont)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = new System.Text.StringBuilder();
            foreach (string word in paragraph.Split(' '))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Ui.Measure(next, font) > width)
                {
                    lines.Add(line.ToString());
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(next);
                }
            }

            lines.Add(line.ToString());
        }

        return lines;
    }

    /// <summary>Draws wrapped text from <paramref name="at"/>; returns the height used.</summary>
    protected int Paragraph(Vector2I at, string text, int width, int maxLines = 40, string font = UiAssets.SmallFont, Color? colour = null)
    {
        List<string> lines = Wrap(text, width, font);
        int n = Math.Min(lines.Count, maxLines);
        for (int i = 0; i < n; i++)
        {
            Text(at + new Vector2I(0, i * 14), lines[i], font, colour ?? Colors.White);
        }

        return n * 14;
    }

    /// <summary>Raised when the dialog closes.</summary>
    public event Action? Closed;

    /// <summary>A STRINGS.INI text through <paramref name="text"/>, or <paramref name="fallback"/> without it.</summary>
    protected static string Label(Func<string, string, string, string>? text, string section, string key, string fallback)
        => text?.Invoke(section, key, fallback) ?? fallback;

    /// <summary>A box of the art as a rectangle relative to it.</summary>
    protected static Rect2I ToRect(ArtBox box) => new(box.X, box.Y, box.Width, box.Height);

    /// <summary>Closes the dialog.</summary>
    protected void Close() => Closed?.Invoke();

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape or Key.Enter })
        {
            Close();
        }
    }
}

/// <summary>
/// Show Message (<c>D_MESSAGE</c>): the user image in the art's picture box, the title and the text in its text box,
/// CLOSE in its button slot (<see cref="DialogBoxes"/>; UI-A10).
/// </summary>
public partial class ScenarioMessageDialog : ScenarioDialog
{
    /// <summary>Creates the dialog; <paramref name="text"/> looks up STRINGS.INI (section, key, fallback).</summary>
    public ScenarioMessageDialog(UiAssets ui, Vector2I screen, ScenarioNotice notice, Texture2D? image, Func<string, string, string, string>? text = null)
        : base(ui, screen, "art/screens/d_message/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (image is not null)
        {
            ArtBox box = DialogBoxes.MessageImage;
            AddChild(new TextureRect { Texture = image, Position = Origin + new Vector2I(box.X, box.Y), Size = new Vector2(box.Width, box.Height), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore });
        }

        ArtBox t = DialogBoxes.MessageText;
        int y = t.Y;
        if (notice.Title.Length > 0)
        {
            Text(new Vector2I(t.X, y), Fit(notice.Title, t.Width, UiAssets.BoldFont), UiAssets.BoldFont);
            y += 22;
        }

        Paragraph(new Vector2I(t.X, y), notice.Text, t.Width, (t.Bottom - y) / 14);
        ArtBox b = DialogBoxes.MessageButton;
        Button(new Vector2I(b.X, b.Y), new Vector2I(b.Width, b.Height), "art/screens/d_message/btn.t2img", Label(text, "wnd_message", "close", "CLOSE"), Close);
    }
}

/// <summary>
/// The quest log (F8, <c>D_QUESTS</c>): the active quests in the order they were added, two to a page in the art's rows,
/// the page selector and CLOSE (<see cref="DialogBoxes"/>; UI-A11).
/// </summary>
public partial class QuestLogDialog : ScenarioDialog
{
    private readonly IReadOnlyList<QuestEntry> _quests;
    private readonly Func<string, string, string, string>? _text;
    private readonly List<Control> _shown = [];

    /// <summary>Creates the dialog; <paramref name="text"/> looks up STRINGS.INI (section, key, fallback).</summary>
    public QuestLogDialog(UiAssets ui, Vector2I screen, IReadOnlyList<QuestEntry> quests, Func<string, string, string, string>? text = null)
        : base(ui, screen, "art/screens/d_quests/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(quests);
        _quests = quests;
        _text = text;
        Text(new Vector2I(20, 32), Label(text, "wnd_quests", "curquest", "CURRENT QUESTS"), UiAssets.TitleFont, null, DialogBoxes.Quests.Width - 40);
        Area(ToRect(DialogBoxes.QuestPagePrevious), () => Turn(-1));
        Area(ToRect(DialogBoxes.QuestPageNext), () => Turn(+1));
        ArtBox b = DialogBoxes.QuestButton;
        Button(new Vector2I(b.X, b.Y), new Vector2I(b.Width, b.Height), "art/screens/d_quests/btn.t2img", Label(text, "wnd_quests", "close", "CLOSE"), Close);
        ShowPage();
    }

    /// <summary>The page shown (0-based).</summary>
    public int Page { get; private set; }

    /// <summary>Turns the page by <paramref name="step"/> (it wraps).</summary>
    public void Turn(int step)
    {
        int pages = DialogBoxes.QuestPages(_quests.Count);
        Page = (((Page + step) % pages) + pages) % pages;
        ShowPage();
    }

    private void ShowPage()
    {
        foreach (Control c in _shown)
        {
            c.QueueFree();
        }

        _shown.Clear();
        if (_quests.Count == 0)
        {
            ArtBox t0 = DialogBoxes.QuestText(0);
            _shown.Add(Text(new Vector2I(t0.X, t0.Y + 4), Label(_text, "wnd_quests", "none", "None"), UiAssets.SmallFont, Colors.White));
        }

        for (int row = 0; row < DialogBoxes.QuestsPerPage && (Page * DialogBoxes.QuestsPerPage) + row < _quests.Count; row++)
        {
            QuestEntry q = _quests[(Page * DialogBoxes.QuestsPerPage) + row];
            ArtBox t = DialogBoxes.QuestText(row);
            _shown.Add(Text(new Vector2I(t.X + 4, t.Y + 4), Fit(q.Title, t.Width - 8, UiAssets.BoldFont), UiAssets.BoldFont));
            List<string> lines = Wrap(q.Text, t.Width - 8);
            for (int i = 0; i < Math.Min(lines.Count, (t.Height - 26) / 14); i++)
            {
                _shown.Add(Text(new Vector2I(t.X + 4, t.Y + 24 + (i * 14)), lines[i], UiAssets.SmallFont, Colors.White));
            }
        }

        ArtBox f = DialogBoxes.QuestPageField;
        string page = $"{Label(_text, "wnd_quests", "page", "Page")} {Page + 1}/{DialogBoxes.QuestPages(_quests.Count)}";
        _shown.Add(Text(new Vector2I(f.X, f.Y + 7), page, UiAssets.SmallFont, Colors.White, f.Width));
    }
}

/// <summary>
/// A conversation waiting for a choice (<c>D_SAY</c>): the speaker and the candidate phrases; clicking one answers
/// (a <c>ScenarioReply</c> command through the session). A single candidate is shown as a phrase to click on.
/// </summary>
public partial class ConversationDialog : ScenarioDialog
{
    /// <summary>Creates the dialog.</summary>
    public ConversationDialog(UiAssets ui, Vector2I screen, ConversationView view, Func<string?, Texture2D?> image)
        : base(ui, screen, "art/screens/d_say/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(image);
        View = view;
        string speaker = view.Choices.Length > 0 ? view.Choices[0].Speaker : string.Empty;
        Texture2D? portrait = image(view.Choices.Length > 0 ? view.Choices[0].Icon : null);
        if (portrait is not null)
        {
            AddChild(new TextureRect { Texture = portrait, Position = Origin + new Vector2I(20, 20), MouseFilter = MouseFilterEnum.Ignore });
        }

        // The phrases are wrapped to the box (D_SAY, 300 px wide; UI-L4): right of the portrait when there is one.
        int x = portrait is null ? 16 : 130;
        int width = ArtSize.X - x - 16;
        Text(new Vector2I(x, 16), speaker, UiAssets.BoldFont);
        int y = 38;
        foreach (PhraseChoice choice in view.Choices)
        {
            string phrase = (view.Choices.Length > 1 ? $"{choice.Position + 1}. " : string.Empty) + choice.Text;
            List<string> lines = Wrap(phrase, width);
            int height = Math.Max(14, Math.Min(lines.Count, 6) * 14);
            int position = choice.Position;
            Area(new Rect2I(new Vector2I(x - 4, y - 2), new Vector2I(width + 8, height + 4)), () => Chosen?.Invoke(position));
            Paragraph(new Vector2I(x, y), phrase, width, 6, UiAssets.SmallFont, new Color(1f, 0.92f, 0.65f));
            y += height + 6;
        }
    }

    /// <summary>The conversation shown.</summary>
    public ConversationView View { get; }

    /// <summary>Raised with the chosen candidate position.</summary>
    public event Action<int>? Chosen;

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        // Number keys choose; Escape does not close a conversation (the game waits for an answer).
        if (@event is InputEventKey { Pressed: true } key && key.Keycode >= Key.Key1 && key.Keycode <= Key.Key9)
        {
            int position = (int)(key.Keycode - Key.Key1);
            if (position < View.Choices.Length)
            {
                Chosen?.Invoke(position);
            }
        }
        else if (@event is InputEventKey { Pressed: true, Keycode: Key.Enter or Key.Space } && View.Choices.Length == 1)
        {
            Chosen?.Invoke(0);
        }
    }
}

/// <summary>
/// Show Story Screen (<c>S_SCRPLAY</c>): the user image with the text scrolling up over the action's scroll time;
/// closes by itself at the end when Auto Close is set, else with OK. Single player is paused while it is shown.
/// </summary>
public partial class StoryScreen : ScenarioDialog
{
    private readonly Control _clip;
    private readonly Control _text;
    private readonly double _scrollSeconds;
    private readonly bool _autoClose;
    private double _elapsed;

    /// <summary>Creates the screen: the image in the art's picture box, the text scrolling on the parchment, CLOSE in its slot (UI-A12).</summary>
    public StoryScreen(UiAssets ui, Vector2I screen, ScenarioNotice notice, Texture2D? image, Func<string, string, string, string>? text = null)
        : base(ui, screen, "art/screens/s_scrplay/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (image is not null)
        {
            ArtBox box = DialogBoxes.StoryImage;
            AddChild(new TextureRect { Texture = image, Position = Origin + new Vector2I(box.X, box.Y), Size = new Vector2(box.Width, box.Height), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore });
        }

        ArtBox t = DialogBoxes.StoryText;
        _clip = new Control { Position = Origin + new Vector2I(t.X, t.Y), Size = new Vector2(t.Width, t.Height), ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_clip);
        _text = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _clip.AddChild(_text);
        List<string> lines = Wrap(notice.Text, t.Width, UiAssets.BoldFont);
        var ink = new Color(0.25f, 0.16f, 0.08f); // dark ink on the parchment
        for (int i = 0; i < lines.Count; i++)
        {
            if (ui.Text(lines[i], UiAssets.BoldFont, ink) is { } line)
            {
                _text.AddChild(new TextureRect { Texture = line, Position = new Vector2(0, t.Height + (i * 18)), MouseFilter = MouseFilterEnum.Ignore });
            }
        }

        _text.Size = new Vector2(t.Width, t.Height + (lines.Count * 18));
        _scrollSeconds = Math.Max(1, notice.DurationMs) / 1000.0;
        _autoClose = notice.AutoClose;
        ArtBox b = DialogBoxes.StoryButton;
        Button(new Vector2I(b.X, b.Y), new Vector2I(b.Width, b.Height), "art/screens/s_scrplay/ok.t2img", Label(text, "wnd_message", "close", "CLOSE"), Close); // the strip covers the label baked in the back
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        _elapsed += delta;
        double share = Math.Min(1, _elapsed / _scrollSeconds);
        _text.Position = new Vector2(0, -(float)(share * (_text.Size.Y - 120)));
        if (_autoClose && _elapsed >= _scrollSeconds + 1)
        {
            Close();
        }
    }
}

/// <summary>
/// A yes-or-no question (<c>D_MSGWND</c>): the title and the question centred in the art's boxes, Yes and No in its
/// button slots (SHELL-027; <see cref="Tzar2.Game.Ui.Confirmations"/>). Enter answers yes, Esc no.
/// </summary>
public partial class ConfirmDialog : ScenarioDialog
{
    /// <summary>Creates the dialog.</summary>
    public ConfirmDialog(UiAssets ui, Vector2I screen, Tzar2.Game.Ui.Confirmation question)
        : base(ui, screen, "art/screens/d_msgwnd/back.t2img")
    {
        ArtBox title = DialogBoxes.QuestionTitle;
        Text(new Vector2I(title.X + ((title.Width - Ui.Measure(question.Title, UiAssets.BoldFont)) / 2), title.Y), question.Title, UiAssets.BoldFont);
        ArtBox body = DialogBoxes.QuestionText;
        List<string> lines = Wrap(question.Question, body.Width);
        for (int i = 0; i < Math.Min(lines.Count, body.Height / 14); i++)
        {
            Text(new Vector2I(body.X + ((body.Width - Ui.Measure(lines[i], UiAssets.SmallFont)) / 2), body.Y + (i * 14)), lines[i], UiAssets.SmallFont, Colors.White);
        }

        ArtBox yes = DialogBoxes.QuestionYes, no = DialogBoxes.QuestionNo;
        Button(new Vector2I(yes.X, yes.Y), new Vector2I(yes.Width, yes.Height), "art/screens/d_msgwnd/btn.t2img", question.Yes, () => Answer(true));
        Button(new Vector2I(no.X, no.Y), new Vector2I(no.Width, no.Height), "art/screens/d_msgwnd/btn.t2img", question.No, () => Answer(false));
    }

    /// <summary>Raised with the answer, once.</summary>
    public event Action<bool>? Answered;

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape or Key.Enter } key)
        {
            Answer(key.Keycode == Key.Enter);
        }
    }

    private void Answer(bool yes)
    {
        Answered?.Invoke(yes);
        Answered = null;
        Close();
    }
}
