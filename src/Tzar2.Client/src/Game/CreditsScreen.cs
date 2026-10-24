using System;
using System.Collections.Generic;
using Godot;
using Tzar2.Client.Logic;

namespace Tzar2.Client.Game;

/// <summary>
/// The credits (<c>S_CREDITS</c>, SHELL-020): the package's credits text scrolls up the book's right page, each line in
/// its <c>CREDITSFNT.INI</c> font, in dark ink; a click or a key goes back. Where the original shows its makers' pictures
/// (the <c>S_CREDITS</c> portraits) and how fast it scrolls are not in the data (UI review question 6); Tzar2 scrolls
/// 30 px a second and starts again at the end.
/// </summary>
public partial class CreditsScreen : ArtScreen
{
    private const float Speed = 30f;
    private readonly Control _text;
    private readonly float _height;
    private float _offset;

    /// <summary>Creates the screen over <paramref name="lines"/>, drawn in <paramref name="fonts"/> (by index; missing: the bold UI font).</summary>
    public CreditsScreen(UiAssets ui, Vector2I screen, IReadOnlyList<CreditsLine> lines, IReadOnlyDictionary<int, string> fonts)
        : base(ui, screen, "art/screens/s_credits/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(fonts);
        ArtBox page = DialogBoxes.CreditsPage;
        var clip = new Control { Position = Origin + new Vector2I(page.X, page.Y), Size = new Vector2(page.Width, page.Height), ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(clip);
        _text = new Control { MouseFilter = MouseFilterEnum.Ignore };
        clip.AddChild(_text);
        var ink = new Color(0.22f, 0.14f, 0.07f);
        float y = 0; // the first lines show at once; after the end the text comes in from below
        foreach (CreditsLine line in lines)
        {
            string font = fonts.TryGetValue(line.Font, out string? f) ? f : UiAssets.BoldFont;
            if (line.Text.Length == 0)
            {
                y += 12;
                continue;
            }

            foreach (string part in WrapIn(line.Text, page.Width - 8, font))
            {
                if (ui.Text(part, font, ink) is { } t)
                {
                    _text.AddChild(new TextureRect { Texture = t, Position = new Vector2(Math.Max(0, (page.Width - t.GetWidth()) / 2), y), MouseFilter = MouseFilterEnum.Ignore });
                    y += t.GetHeight() + 2;
                }
            }
        }

        _height = y;
        _text.Size = new Vector2(page.Width, y);
    }

    /// <summary>Raised when the player leaves the credits.</summary>
    public event Action? Closed;

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        _offset += (float)delta * Speed;
        if (_offset > _height)
        {
            _offset = -DialogBoxes.CreditsPage.Height;
        }

        _text.Position = new Vector2(0, -_offset);
    }

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } or InputEventKey { Pressed: true })
        {
            Closed?.Invoke();
        }
    }
}
