using Godot;

namespace Tzar2.Client.Viewer;

/// <summary>Persistent viewer settings (package and SoundFont paths) in a Godot <c>ConfigFile</c>, default <c>user://viewer.cfg</c>.</summary>
public sealed class ViewerSettings
{
    private const string Section = "viewer";
    private readonly ConfigFile _file = new();

    /// <summary>Loads the settings file (a missing file means defaults).</summary>
    public ViewerSettings(string? path)
    {
        FilePath = string.IsNullOrEmpty(path) ? "user://viewer.cfg" : path;
        _file.Load(FilePath);
    }

    /// <summary>The settings file.</summary>
    public string FilePath { get; }

    /// <summary>Last opened package, or empty.</summary>
    public string Package
    {
        get => (string)_file.GetValue(Section, "package", string.Empty);
        set => Set("package", value);
    }

    /// <summary>User-chosen SoundFont, or empty.</summary>
    public string SoundFont
    {
        get => (string)_file.GetValue(Section, "soundfont", string.Empty);
        set => Set("soundfont", value);
    }

    private void Set(string key, string value)
    {
        _file.SetValue(Section, key, value);
        Error error = _file.Save(FilePath);
        if (error != Error.Ok)
        {
            GD.PushWarning($"Could not save {FilePath}: {error}");
        }
    }
}
