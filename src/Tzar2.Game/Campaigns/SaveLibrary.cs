using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Core.Serialization;

namespace Tzar2.Game.Campaigns;

/// <summary>A save game in the library, as the save and load screens list it.</summary>
/// <param name="Name">Its name (the file name without <c>.t2sav</c>).</param>
/// <param name="Label">The label it was saved with (the time).</param>
/// <param name="Kind">What it continues.</param>
/// <param name="Map">The campaign map or custom map it continues, or empty.</param>
/// <param name="Tick">The tick it was saved at.</param>
/// <param name="Written">When the file was written (UTC).</param>
public sealed record SaveEntry(string Name, string Label, SaveKind Kind, string Map, int Tick, DateTime Written);

/// <summary>
/// The player's save games (<c>&lt;user&gt;/saves/*.t2sav</c>; SHELL-014, SHELL-022): listed newest first, saved under a
/// name the player types, loaded and deleted. A name is letters, digits, spaces, <c>-</c> and <c>_</c>, at most 40
/// characters, so it is always a plain file name.
/// </summary>
public sealed class SaveLibrary
{
    /// <summary>The file extension.</summary>
    public const string Extension = ".t2sav";

    /// <summary>The longest name.</summary>
    public const int MaxName = 40;

    private readonly string _directory;

    /// <summary>A library in <paramref name="directory"/> (created on the first save).</summary>
    public SaveLibrary(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
    }

    /// <summary>The saves, newest first; unreadable files are left out.</summary>
    public IReadOnlyList<SaveEntry> List()
    {
        var list = new List<SaveEntry>();
        if (!Directory.Exists(_directory))
        {
            return list;
        }

        foreach (string file in Directory.EnumerateFiles(_directory, "*" + Extension))
        {
            try
            {
                SaveGame header = SaveGame.FromBytes(File.ReadAllBytes(file));
                list.Add(new SaveEntry(Path.GetFileNameWithoutExtension(file), header.Label, header.Kind, header.MapId, header.Tick, File.GetLastWriteTimeUtc(file)));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // a broken or foreign file is not listed
            }
        }

        return [.. list.OrderByDescending(e => e.Written).ThenBy(e => e.Name, StringComparer.Ordinal)];
    }

    /// <summary>The name cleaned for a file (trimmed), or null when it is empty, too long or has other characters.</summary>
    public static string? Clean(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length is > 0 and <= MaxName && trimmed.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_') ? trimmed : null;
    }

    /// <summary>Whether a save of that name exists.</summary>
    public bool Exists(string name) => Clean(name) is { } n && File.Exists(PathOf(n));

    /// <summary>Saves (or overwrites) <paramref name="save"/> under <paramref name="name"/>.</summary>
    /// <exception cref="ArgumentException">The name is not valid (<see cref="Clean"/>).</exception>
    public void Save(string name, SaveGame save)
    {
        ArgumentNullException.ThrowIfNull(save);
        string n = Clean(name) ?? throw new ArgumentException($"'{name}' is not a valid save name.", nameof(name));
        Directory.CreateDirectory(_directory);
        string path = PathOf(n);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, save.ToBytes());
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads the save <paramref name="name"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not a valid save.</exception>
    /// <exception cref="FileNotFoundException">There is no such save.</exception>
    public SaveGame Load(string name)
    {
        string n = Clean(name) ?? throw new FileNotFoundException("No such save.", name);
        return SaveGame.FromBytes(File.ReadAllBytes(PathOf(n)));
    }

    /// <summary>Deletes the save <paramref name="name"/> (nothing when there is none).</summary>
    public void Delete(string name)
    {
        if (Clean(name) is { } n && File.Exists(PathOf(n)))
        {
            File.Delete(PathOf(n));
        }
    }

    /// <summary>A free name like "Game 3" for a new save.</summary>
    public string NewName()
    {
        for (int i = 1; ; i++)
        {
            string name = "Game " + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!Exists(name))
            {
                return name;
            }
        }
    }

    private string PathOf(string name) => Path.Combine(_directory, name + Extension);
}
