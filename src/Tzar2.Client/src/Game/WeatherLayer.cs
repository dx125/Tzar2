using System;
using Godot;

namespace Tzar2.Client.Game;

/// <summary>
/// Simple weather (TDD §13.8): rain from the original <c>ANIMS/RAIN.BMP</c> streaks (6 cells) falling over the world
/// view, with <c>RAINDROPS.BMP</c> splashes. Screen-space particles, presentation only; the original's rain density,
/// speed and the cloud-shadow drift are not measured yet (⚠ placeholders).
/// </summary>
public partial class WeatherLayer : Node2D
{
    private const int Drops = 700;
    private readonly GameScreen _screen;
    private readonly Rect2I _view;
    private readonly Texture2D?[] _streaks = new Texture2D?[6];
    private readonly Texture2D?[] _splashes = new Texture2D?[5];
    private readonly Vector2[] _drops = new Vector2[Drops];
    private readonly float[] _speed = new float[Drops];
    private readonly int[] _kind = new int[Drops];
    private readonly Random _random = new(77);

    /// <summary>Creates the layer for <paramref name="screen"/>.</summary>
    public WeatherLayer(GameScreen screen, UiAssets ui, Rect2I view)
    {
        ArgumentNullException.ThrowIfNull(ui);
        _screen = screen;
        _view = view;
        if (ui.Image("art/anims/rain.t2img") is { } rain)
        {
            int w = rain.GetWidth() / 2, h = rain.GetHeight() / 3;
            for (int i = 0; i < 6; i++)
            {
                _streaks[i] = ui.Region("art/anims/rain.t2img", new Rect2I((i % 2) * w, (i / 2) * h, w, h));
            }
        }

        if (ui.Image("art/anims/raindrops.t2img") is { } drops)
        {
            int w = drops.GetWidth() / 5;
            for (int i = 0; i < 5; i++)
            {
                _splashes[i] = ui.Region("art/anims/raindrops.t2img", new Rect2I(i * w, 0, w, drops.GetHeight()));
            }
        }

        for (int i = 0; i < Drops; i++)
        {
            Respawn(i, anywhere: true);
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (!_screen.Rain)
        {
            if (Visible)
            {
                Visible = false;
            }

            return;
        }

        Visible = true;
        for (int i = 0; i < Drops; i++)
        {
            _drops[i] += new Vector2(-0.25f, 1f) * _speed[i] * (float)delta;
            if (_drops[i].Y > _view.End.Y || _drops[i].X < _view.Position.X)
            {
                Respawn(i, anywhere: false);
            }
        }

        QueueRedraw();
    }

    /// <inheritdoc/>
    public override void _Draw()
    {
        for (int i = 0; i < Drops; i++)
        {
            Texture2D? t = _streaks[_kind[i] % 6];
            if (t is not null)
            {
                DrawTexture(t, _drops[i].Round(), new Color(1, 1, 1, 0.7f));
            }
            else
            {
                DrawLine(_drops[i], _drops[i] + new Vector2(-3, 12), new Color(0.75f, 0.8f, 0.9f, 0.6f));
            }

            if (_kind[i] % 7 == 0 && _splashes[(_kind[i] / 7) % 5] is { } s)
            {
                DrawTexture(s, new Vector2(_drops[i].X + 20, _view.Position.Y + ((_drops[i].Y * 7) % _view.Size.Y)).Round(), new Color(1, 1, 1, 0.5f));
            }
        }
    }

    private void Respawn(int i, bool anywhere)
    {
        _drops[i] = new Vector2(
            _view.Position.X + _random.Next(_view.Size.X + 120),
            anywhere ? _view.Position.Y + _random.Next(_view.Size.Y) : _view.Position.Y - _random.Next(40));
        _speed[i] = 380 + _random.Next(160);
        _kind[i] = _random.Next(42);
    }
}
