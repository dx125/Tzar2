using System;
using Godot;

namespace Tzar2.Client.Game;

/// <summary>
/// The player's game preferences (the original's options screen, Appendix E.7), kept in a Godot <c>ConfigFile</c>
/// (default <c>user://game.cfg</c>): volumes, sound categories, game and scroll speed, day/night, weather, fog.
/// </summary>
public sealed class GameSettings
{
    private const string Section = "game";
    private readonly ConfigFile _file = new();

    /// <summary>Loads the file (missing = defaults).</summary>
    public GameSettings(string? path = null)
    {
        FilePath = string.IsNullOrEmpty(path) ? "user://game.cfg" : path;
        _file.Load(FilePath);
    }

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <summary>Sound effect volume 0-100.</summary>
    public int SoundVolume { get => Get("sound_volume", 80); set => Set("sound_volume", Math.Clamp(value, 0, 100)); }

    /// <summary>Music volume 0-100.</summary>
    public int MusicVolume { get => Get("music_volume", 60); set => Set("music_volume", Math.Clamp(value, 0, 100)); }

    /// <summary>Unit speech on.</summary>
    public bool UnitSpeech { get => Get("unit_speech", 1) != 0; set => Set("unit_speech", value ? 1 : 0); }

    /// <summary>Building sounds on.</summary>
    public bool BuildingSounds { get => Get("building_sounds", 1) != 0; set => Set("building_sounds", value ? 1 : 0); }

    /// <summary>Nature and work sounds on.</summary>
    public bool NatureSounds { get => Get("nature_sounds", 1) != 0; set => Set("nature_sounds", value ? 1 : 0); }

    /// <summary>Game speed step (index into the session's speeds).</summary>
    public int GameSpeed { get => Get("game_speed", 3); set => Set("game_speed", Math.Clamp(value, 0, 6)); }

    /// <summary>Scroll speed step 1-10.</summary>
    public int ScrollSpeed { get => Get("scroll_speed", 5); set => Set("scroll_speed", Math.Clamp(value, 1, 10)); }

    /// <summary>Holding the right mouse button and moving drags the map (a right click without moving still gives orders).</summary>
    public bool DragScroll { get => Get("drag_scroll", 1) != 0; set => Set("drag_scroll", value ? 1 : 0); }

    /// <summary>Day/night visualisation.</summary>
    public bool DayNight { get => Get("day_night", 1) != 0; set => Set("day_night", value ? 1 : 0); }

    /// <summary>Weather effects.</summary>
    public bool Weather { get => Get("weather", 1) != 0; set => Set("weather", value ? 1 : 0); }

    private int Get(string key, int fallback) => (int)_file.GetValue(Section, key, fallback);

    private void Set(string key, int value)
    {
        _file.SetValue(Section, key, value);
        if (_file.Save(FilePath) != Error.Ok)
        {
            GD.PushWarning($"Could not save {FilePath}");
        }
    }
}

/// <summary>The options dialog (<c>D_OPTIONS</c>): sound, music, game and interface preferences.</summary>
public partial class OptionsScreen : ArtScreen
{
    private readonly GameSettings _settings;
    private readonly TextureRect[] _values = new TextureRect[4];
    private readonly TextureRect[] _checks = new TextureRect[6];

    /// <summary>Creates the dialog.</summary>
    public OptionsScreen(UiAssets ui, Logic.PresentationCatalog? catalog, Vector2I screen, GameSettings settings)
        : base(ui, screen, "art/screens/d_options/back.t2img", dim: true)
    {
        _settings = settings;
        string T(string key, string fallback) => catalog?.Text("options", key, fallback) ?? fallback;
        Text(new Vector2I(92, 56), T("t_sopt", "SOUND OPTIONS"));
        Text(new Vector2I(462, 56), T("t_gopt", "GAME OPTIONS"));
        Text(new Vector2I(92, 302), T("t_mopt", "MUSIC OPTIONS"));
        Text(new Vector2I(462, 302), T("t_uint", "USER INTERFACE"));

        Slider(0, new Vector2I(222, 82), T("t_svol", "Sound Volume"), new Vector2I(130, 88));
        Slider(1, new Vector2I(550, 82), T("t_gs", "Game Speed"), new Vector2I(462, 88));
        Slider(2, new Vector2I(550, 128), T("t_ss", "Scroll Speed"), new Vector2I(462, 134));
        Slider(3, new Vector2I(222, 330), T("t_mv", "Music Volume"), new Vector2I(130, 336));
        Check(0, new Vector2I(94, 132), T("t_usp", "Unit Speech"));
        Check(1, new Vector2I(94, 175), T("t_bs", "Building Sounds"));
        Check(2, new Vector2I(94, 221), T("t_ns", "Nature Sounds"));
        Check(3, new Vector2I(94, 380), T("t_we", "Weather Effects"));
        Check(4, new Vector2I(464, 332), T("t_dn", "Day/Night Visualize"));
        Button(new Vector2I(539, 541), new Vector2I(210, 27), "art/screens/d_options/btn.t2img", T("ok", "OK"), () => Closed?.Invoke());
        Refresh();
    }

    /// <summary>Raised when the dialog closes (settings are already saved).</summary>
    public event Action? Closed;

    /// <inheritdoc/>
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape or Key.Enter })
        {
            Closed?.Invoke();
        }
    }

    private void Slider(int slot, Vector2I at, string label, Vector2I labelAt)
    {
        Text(labelAt, label, UiAssets.SmallFont, Colors.White);
        Area(new Rect2I(at, new Vector2I(26, 28)), () => Step(slot, -1));
        Area(new Rect2I(at + new Vector2I(156, 0), new Vector2I(26, 28)), () => Step(slot, +1));
        _values[slot] = Text(at + new Vector2I(60, 7), string.Empty, UiAssets.SmallFont, Colors.White);
    }

    private void Check(int slot, Vector2I at, string label)
    {
        Text(at + new Vector2I(36, 4), label, UiAssets.SmallFont, Colors.White);
        _checks[slot] = new TextureRect { Position = Origin + at, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_checks[slot]);
        Area(new Rect2I(at - new Vector2I(4, 4), new Vector2I(28, 28)), () => Toggle(slot));
    }

    private void Step(int slot, int step)
    {
        switch (slot)
        {
            case 0: _settings.SoundVolume += step * 10; break;
            case 1: _settings.GameSpeed += step; break;
            case 2: _settings.ScrollSpeed += step; break;
            case 3: _settings.MusicVolume += step * 10; break;
        }

        Refresh();
        Changed?.Invoke();
    }

    private void Toggle(int slot)
    {
        switch (slot)
        {
            case 0: _settings.UnitSpeech = !_settings.UnitSpeech; break;
            case 1: _settings.BuildingSounds = !_settings.BuildingSounds; break;
            case 2: _settings.NatureSounds = !_settings.NatureSounds; break;
            case 3: _settings.Weather = !_settings.Weather; break;
            case 4: _settings.DayNight = !_settings.DayNight; break;
        }

        Refresh();
        Changed?.Invoke();
    }

    /// <summary>Raised after every change (apply at once).</summary>
    public event Action? Changed;

    private void Refresh()
    {
        string[] values =
        [
            $"{_settings.SoundVolume} %",
            $"{Tzar2.Game.Session.GameSession.SpeedSteps[_settings.GameSpeed]} %",
            _settings.ScrollSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{_settings.MusicVolume} %",
        ];
        for (int i = 0; i < values.Length; i++)
        {
            _values[i].Texture = Ui.Text(values[i], UiAssets.SmallFont, Colors.White);
        }

        bool[] on = [_settings.UnitSpeech, _settings.BuildingSounds, _settings.NatureSounds, _settings.Weather, _settings.DayNight];
        for (int i = 0; i < on.Length; i++)
        {
            _checks[i].Texture = Ui.Region("art/screens/d_options/check.t2img", new Rect2I(0, on[i] ? 19 : 0, 19, 19));
        }
    }
}
