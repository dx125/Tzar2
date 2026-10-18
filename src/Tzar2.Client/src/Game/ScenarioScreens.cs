using System;
using System.Collections.Generic;
using Godot;
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

/// <summary>Show Message (<c>D_MESSAGE</c>): title, text, optional user image, OK.</summary>
public partial class ScenarioMessageDialog : ScenarioDialog
{
    /// <summary>Creates the dialog.</summary>
    public ScenarioMessageDialog(UiAssets ui, Vector2I screen, ScenarioNotice notice, Texture2D? image)
        : base(ui, screen, "art/screens/d_message/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(notice);
        Text(new Vector2I(20, 16), notice.Title, UiAssets.TitleFont, null, 460);
        int x = 24;
        if (image is not null)
        {
            AddChild(new TextureRect { Texture = image, Position = Origin + new Vector2I(24, 44), MouseFilter = MouseFilterEnum.Ignore });
            x += image.GetWidth() + 12;
        }

        Paragraph(new Vector2I(x, 48), notice.Text, 460 - x, 14);
        Button(new Vector2I(190, 262), new Vector2I(120, 25), "art/screens/d_message/btn.t2img", "OK", Close);
    }
}

/// <summary>The quest log (F8, <c>D_QUESTS</c>): the active quests in the order they were added.</summary>
public partial class QuestLogDialog : ScenarioDialog
{
    /// <summary>Creates the dialog.</summary>
    public QuestLogDialog(UiAssets ui, Vector2I screen, IReadOnlyList<QuestEntry> quests)
        : base(ui, screen, "art/screens/d_quests/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(quests);
        Text(new Vector2I(20, 16), "QUESTS", UiAssets.TitleFont, null, 460);
        int y = 50;
        if (quests.Count == 0)
        {
            Text(new Vector2I(30, y), "No quests.", UiAssets.SmallFont, Colors.White);
        }

        foreach (QuestEntry q in quests)
        {
            Text(new Vector2I(30, y), q.Title, UiAssets.BoldFont);
            y += 18 + Paragraph(new Vector2I(40, y + 18), q.Text, 420, 6);
            if (y > 260)
            {
                break;
            }
        }

        Button(new Vector2I(190, 282), new Vector2I(120, 25), "art/screens/d_quests/btn.t2img", "OK", Close);
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
        if (image(view.Choices.Length > 0 ? view.Choices[0].Icon : null) is { } portrait)
        {
            AddChild(new TextureRect { Texture = portrait, Position = Origin + new Vector2I(20, 20), MouseFilter = MouseFilterEnum.Ignore });
        }

        Text(new Vector2I(130, 20), speaker, UiAssets.BoldFont);
        int y = 48;
        foreach (PhraseChoice choice in view.Choices)
        {
            List<string> lines = Wrap(choice.Text, 330);
            int height = Math.Max(16, Math.Min(lines.Count, 6) * 14);
            int position = choice.Position;
            Area(new Rect2I(new Vector2I(126, y - 2), new Vector2I(340, height + 4)), () => Chosen?.Invoke(position));
            Paragraph(new Vector2I(130, y), (view.Choices.Length > 1 ? $"{position + 1}. " : string.Empty) + choice.Text, 330, 6, UiAssets.SmallFont, new Color(1f, 0.92f, 0.65f));
            y += height + 10;
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

    /// <summary>Creates the screen.</summary>
    public StoryScreen(UiAssets ui, Vector2I screen, ScenarioNotice notice, Texture2D? image)
        : base(ui, screen, "art/screens/s_scrplay/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (image is not null)
        {
            AddChild(new TextureRect { Texture = image, Position = Origin + new Vector2I(20, 20), MouseFilter = MouseFilterEnum.Ignore });
        }

        _clip = new Control { Position = Origin + new Vector2I(330, 30), Size = new Vector2(420, 480), ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_clip);
        _text = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _clip.AddChild(_text);
        List<string> lines = Wrap(notice.Text, 400, UiAssets.BoldFont);
        for (int i = 0; i < lines.Count; i++)
        {
            if (ui.Text(lines[i], UiAssets.BoldFont, new Color(1f, 0.92f, 0.65f)) is { } t)
            {
                _text.AddChild(new TextureRect { Texture = t, Position = new Vector2(0, 480 + (i * 18)), MouseFilter = MouseFilterEnum.Ignore });
            }
        }

        _text.Size = new Vector2(420, 480 + (lines.Count * 18));
        _scrollSeconds = Math.Max(1, notice.DurationMs) / 1000.0;
        _autoClose = notice.AutoClose;
        Button(new Vector2I(660, 540), new Vector2I(100, 30), "art/screens/s_scrplay/ok.t2img", string.Empty, Close);
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
