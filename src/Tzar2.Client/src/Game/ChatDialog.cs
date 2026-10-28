using System;
using Godot;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;

namespace Tzar2.Client.Game;

/// <summary>
/// The chat screen (<c>D_CHAT</c>; HUD-011) drawing <see cref="ChatScreen"/> as ReTzar reads the original's window
/// (0x422f00): <c>BACK_SMALL</c>, or <c>BACK_BIG</c> with the kingdoms' rows for a custom message; the title, the line
/// at (45, 58), the recipients ALL, ALLY, ENEMY at x 37, 93, 149 and CUSTOM at 425 (y 87); each row a check, the
/// kingdom's colour and name, and a shield of the player's stance (its place on the row is ours: UNVERIFIED). Enter sends,
/// Esc closes.
/// </summary>
public partial class ChatDialog : ArtScreen
{
    private const string Art = "art/screens/d_chat/";
    private readonly ChatScreen _model;
    private readonly TextureRect _line;
    private readonly System.Collections.Generic.List<(TextureRect Icon, ChatTarget To)> _recipients = [];

    /// <summary>Creates the window for <paramref name="model"/>.</summary>
    public ChatDialog(UiAssets ui, Vector2I screen, ChatScreen model, Func<string, string, string, string> text, Func<int, string> name, Func<int, Color> colour)
        : base(ui, screen, Art + (model.Large ? "back_big.t2img" : "back_small.t2img"), dim: true, missing: new Vector2I(500, model.Large ? 320 : 143))
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(colour);
        _model = model;
        Text(new Vector2I(38, 32), text("chat", "t_chat", "CHAT MESSAGE"), UiAssets.BoldFont);
        // The line in the small font with its caret, typed by the keys (the original's edit box: the art's slot).
        _line = new TextureRect { Position = Origin + new Vector2I(45, 60), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_line);
        ShowLine();

        (int X, string Image, ChatTarget To)[] recipients = [(37, "all", ChatTarget.All), (93, "ally", ChatTarget.Allies), (149, "enemy", ChatTarget.Enemies), (425, "custom", ChatTarget.Custom)];
        foreach ((int x, string image, ChatTarget to) in recipients)
        {
            // The size is set after the expand mode: with the 48 x 48 picture set first the control kept 48 x 48 and the
            // buttons crossed the window's frame (the sixth play-test).
            var icon = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore,
                Position = Origin + new Vector2I(x, 87),
                Size = new Vector2(28, 28),
                Texture = Ui.Texture(Art + image + ".t2img"),
            };
            AddChild(icon);
            _recipients.Add((icon, to));
            Area(new Rect2I(x, 87, 28, 28), () =>
            {
                bool resize = (to == ChatTarget.Custom) != _model.Large;
                _model.Address(to);
                if (resize)
                {
                    Reopen?.Invoke(); // the window changes size
                }
                else
                {
                    Modulates();
                }
            });
        }

        if (model.Large)
        {
            for (int i = 0; i < model.Rows.Count; i++)
            {
                ChatRow row = model.Rows[i];
                var at = new Vector2I(37 + (230 * (i / 4)), 152 + (32 * (i % 4)));
                AddChild(new TextureRect { Texture = Ui.Texture(Art + "row.t2img"), Position = Origin + at, MouseFilter = MouseFilterEnum.Ignore });
                var check = new TextureRect { Texture = Check(row.Picked), Position = Origin + at, MouseFilter = MouseFilterEnum.Ignore };
                AddChild(check);
                AddChild(new ColorRect { Position = Origin + at + new Vector2I(38, 8), Size = new Vector2(13, 13), Color = colour(row.Kingdom), MouseFilter = MouseFilterEnum.Ignore });
                Text(at + new Vector2I(66, 8), Fit(name(row.Kingdom), 108, UiAssets.SmallFont), UiAssets.SmallFont, new Color(0.85f, 0.75f, 0.45f));
                string shield = row.Shield switch { ChatShield.Ally => "shield_green", ChatShield.Neutral => "shield_neutral", _ => "shield_red" };
                AddChild(new TextureRect { Texture = Ui.Texture(Art + shield + ".t2img"), Position = Origin + at + new Vector2I(180, 5), MouseFilter = MouseFilterEnum.Ignore });
                int kingdom = row.Kingdom;
                Area(new Rect2I(at, new Vector2I(31, 31)), () =>
                {
                    _model.Toggle(kingdom);
                    check.Texture = Check(_model.IsPicked(kingdom));
                });
            }
        }

        // Who it goes to, and the keys (the window had no word of either).
        _to = new TextureRect { Position = Origin + new Vector2I(190, 95), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_to);
        Text(new Vector2I(250, 32), "Enter: send   Esc: close", UiAssets.SmallFont, new Color(0.7f, 0.65f, 0.5f), 210);
        Modulates();
    }

    private readonly TextureRect _to;

    /// <summary>SEND: the line and its recipients.</summary>
    public event Action<string, ChatTarget, byte>? Sent;

    /// <summary>Esc.</summary>
    public event Action? Closed;

    /// <summary>The window must be built again at its other size (to CUSTOM or back).</summary>
    public event Action? Reopen;

    /// <summary>The model.</summary>
    public ChatScreen Model => _model;

    /// <inheritdoc/>
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key)
        {
            return;
        }

        switch (key.Keycode)
        {
            case Key.Escape:
                Closed?.Invoke();
                break;
            case Key.Enter or Key.KpEnter:
                Send();
                break;
            case Key.Backspace when _model.Text.Length > 0:
                _model.Write(_model.Text[..^1]);
                ShowLine();
                break;
            default:
                if (key.Unicode >= 32 && !key.CtrlPressed && !key.AltPressed)
                {
                    _model.Write(_model.Text + char.ConvertFromUtf32((int)key.Unicode));
                    ShowLine();
                }

                break;
        }

        GetViewport().SetInputAsHandled(); // the chat takes every key while it is open
    }

    private void ShowLine() => _line.Texture = Ui.Text(Fit(_model.Text, 396, UiAssets.SmallFont) + "|", UiAssets.SmallFont, new Color(1f, 0.92f, 0.65f));

    private Texture2D? Check(bool on) => Ui.Region("art/screens/common/chk.t2img", new Rect2I(0, on ? 0 : 31, 31, 31));

    // The chosen recipients bright, the others dimmed, and their name.
    private void Modulates()
    {
        foreach ((TextureRect icon, ChatTarget to) in _recipients)
        {
            icon.Modulate = _model.To == to ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
        }

        string who = _model.To switch
        {
            ChatTarget.All => "To everyone",
            ChatTarget.Allies => "To the allies",
            ChatTarget.Enemies => "To the enemies",
            _ => "To the kingdoms ticked",
        };
        _to.Texture = Ui.Text(who, UiAssets.SmallFont, new Color(1f, 0.92f, 0.65f));
    }

    private void Send()
    {
        if (_model.Send() is { } message)
        {
            Sent?.Invoke(message.Text, message.To, message.Mask);
        }
        else
        {
            Closed?.Invoke();
        }
    }
}
