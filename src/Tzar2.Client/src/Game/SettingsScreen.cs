using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Tzar2.Client.Logic;

namespace Tzar2.Client.Game;

/// <summary>
/// SETTINGS of the main menu (ADR 0177) on the four panels of the campaign art: display (preset, interface scale, window
/// mode and size), controls (edge scroll, drag scroll, scroll speed), sound (volumes) and game (speed, day/night,
/// weather). Each row steps its value with the original's arrow buttons; changes are saved and applied at once.
/// </summary>
public partial class SettingsScreen : ArtScreen
{
    /// <summary>Window sizes offered in windowed mode (those that fit the monitor).</summary>
    public static readonly Vector2I[] WindowSizes =
    [
        new(1024, 768), new(1280, 720), new(1280, 800), new(1366, 768), new(1440, 900), new(1600, 900), new(1680, 1050),
        new(1920, 1080), new(1920, 1200), new(2560, 1440), new(3840, 2160),
    ];

    private static readonly Rect2I Display = new(84, 46, 270, 246);
    private static readonly Rect2I Controls = new(411, 46, 356, 246);
    private static readonly Rect2I Sound = new(84, 355, 272, 140);
    private static readonly Rect2I Game = new(410, 355, 358, 140);
    private const int RowHeight = 30;
    private const int Arrow = 27;

    private readonly GameSettings _settings;
    private readonly List<(TextureRect Value, Func<string> Text)> _rows = [];

    /// <summary>Creates the screen.</summary>
    public SettingsScreen(UiAssets ui, PresentationCatalog? catalog, Vector2I screen, GameSettings settings)
        : base(ui, screen, "art/screens/s_campaign/back.t2img")
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        string T(string section, string key, string fallback) => catalog?.Text(section, key, fallback) ?? fallback;

        Title(Display, "DISPLAY");
        Row(Display, 0, "Screen", () => _settings.DisplayPreset == 0 ? "Wide" : "Authentic 1024x768", step => _settings.DisplayPreset = Wrap(_settings.DisplayPreset + step, 2), display: true);
        Row(Display, 1, "Interface scale", () => _settings.InterfaceScale == 0 ? "Auto" : $"{_settings.InterfaceScale}x", step => _settings.InterfaceScale = Wrap(_settings.InterfaceScale + step, DisplayMath.MaxScale + 1), display: true);
        Row(Display, 2, "Window", () => _settings.WindowMode == 0 ? "Windowed" : "Fullscreen", step => _settings.WindowMode = Wrap(_settings.WindowMode + step, 2), display: true);
        Row(Display, 3, "Window size", WindowSizeText, StepWindowSize, display: true);

        Title(Controls, T("options", "t_uint", "CONTROLS"));
        Row(Controls, 0, "Edge scroll", () => OnOff(_settings.EdgeScroll), _ => _settings.EdgeScroll = !_settings.EdgeScroll);
        Row(Controls, 1, "Drag map with right button", () => OnOff(_settings.DragScroll), _ => _settings.DragScroll = !_settings.DragScroll);
        Row(Controls, 2, T("options", "t_ss", "Scroll Speed"), () => _settings.ScrollSpeed.ToString(CultureInfo.InvariantCulture), step => _settings.ScrollSpeed += step);
        Row(Controls, 3, "Pointer", () => _settings.SystemCursor ? "System" : "Tzar2", _ => _settings.SystemCursor = !_settings.SystemCursor);

        Title(Sound, T("options", "t_sopt", "SOUND"));
        Row(Sound, 0, T("options", "t_svol", "Sound Volume"), () => $"{_settings.SoundVolume} %", step => _settings.SoundVolume += step * 10);
        Row(Sound, 1, T("options", "t_mv", "Music Volume"), () => $"{_settings.MusicVolume} %", step => _settings.MusicVolume += step * 10);

        Title(Game, T("options", "t_gopt", "GAME"));
        Row(Game, 0, T("options", "t_gs", "Game Speed"), () => $"{Tzar2.Game.Session.GameSession.SpeedSteps[_settings.GameSpeed]} %", step => _settings.GameSpeed += step);
        Row(Game, 1, T("options", "t_dn", "Day/Night"), () => OnOff(_settings.DayNight), _ => _settings.DayNight = !_settings.DayNight);
        Row(Game, 2, T("options", "t_we", "Weather Effects"), () => OnOff(_settings.Weather), _ => _settings.Weather = !_settings.Weather);

        const string Btn = "art/screens/s_campaign/btn.t2img";
        Button(new Vector2I(536, 544), new Vector2I(235, 25), Btn, T("options", "ok", "OK"), () => Closed?.Invoke());
        Refresh();
    }

    /// <summary>A setting changed (already saved); true when it is a display setting to apply to the window.</summary>
    public event Action<bool>? Changed;

    /// <summary>OK.</summary>
    public event Action? Closed;

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape or Key.Enter })
        {
            Closed?.Invoke();
        }
    }

    private static int Wrap(int value, int count) => ((value % count) + count) % count;

    private static string OnOff(bool on) => on ? "On" : "Off";

    private string WindowSizeText()
        => _settings.WindowWidth > 0 ? $"{_settings.WindowWidth} x {_settings.WindowHeight}" : "Default";

    private void StepWindowSize(int step)
    {
        Vector2I monitor = DisplayServer.ScreenGetSize();
        var fit = new List<Vector2I>();
        foreach (Vector2I size in WindowSizes)
        {
            if (size.X <= monitor.X && size.Y <= monitor.Y)
            {
                fit.Add(size);
            }
        }

        int at = fit.FindIndex(s => s.X == _settings.WindowWidth && s.Y == _settings.WindowHeight);
        Vector2I next = fit[Wrap((at < 0 ? (step > 0 ? -1 : 0) : at) + step, fit.Count)];
        _settings.WindowWidth = next.X;
        _settings.WindowHeight = next.Y;
    }

    private void Title(Rect2I panel, string text) => Text(panel.Position, text, UiAssets.BoldFont);

    private void Row(Rect2I panel, int index, string label, Func<string> value, Action<int> step, bool display = false)
    {
        Vector2I at = panel.Position + new Vector2I(0, 26 + (index * RowHeight));
        Text(at + new Vector2I(0, 6), label, UiAssets.SmallFont, new Color(0.85f, 0.85f, 0.75f));
        int arrows = panel.Size.X - (2 * Arrow) - 110;
        Vector2I left = at + new Vector2I(arrows, 0), right = left + new Vector2I(Arrow + 110, 0);
        ArrowImage(left, "art/screens/common/btn left.t2img");
        ArrowImage(right, "art/screens/common/btn right.t2img");
        Area(new Rect2I(left, new Vector2I(Arrow, Arrow)), () => Apply(-1));
        Area(new Rect2I(right, new Vector2I(Arrow, Arrow)), () => Apply(+1));
        // The value, centred between the arrows.
        var shown = new TextureRect
        {
            Position = Origin + left + new Vector2I(Arrow, 6),
            Size = new Vector2(110, 16),
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(shown);
        _rows.Add((shown, value));

        void Apply(int direction)
        {
            step(direction);
            Refresh();
            Changed?.Invoke(display);
        }
    }

    private void ArrowImage(Vector2I at, string image)
    {
        if (Ui.Region(image, new Rect2I(0, 0, Arrow, Arrow)) is { } t)
        {
            AddChild(new TextureRect { Texture = t, Position = Origin + at, MouseFilter = MouseFilterEnum.Ignore });
        }
    }

    private void Refresh()
    {
        foreach ((TextureRect shown, Func<string> text) in _rows)
        {
            shown.Texture = Ui.Text(text(), UiAssets.SmallFont, Colors.White);
        }
    }
}
