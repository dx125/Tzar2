using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Game.Campaigns;

namespace Tzar2.Game.Ui;

/// <summary>
/// The save screen (SAVE GAME, F2; SHELL-021, SHELL-022): the saves listed, the name typed or taken from a picked save
/// (overwriting it), NEW for a fresh name, SAVE, DELETE and CANCEL (engine-free model, ADR 0182).
/// </summary>
public sealed class SaveGameScreen
{
    private readonly SaveLibrary _library;
    private List<SaveEntry> _entries;

    /// <summary>Creates the screen over <paramref name="library"/>, with a fresh name.</summary>
    public SaveGameScreen(SaveLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
        _entries = [.. library.List()];
        Name = library.NewName();
    }

    /// <summary>The saves, newest first.</summary>
    public IReadOnlyList<SaveEntry> Entries => _entries;

    /// <summary>The picked save, or −1.</summary>
    public int Selected { get; private set; } = -1;

    /// <summary>The name to save under.</summary>
    public string Name { get; set; }

    /// <summary>Whether <see cref="Name"/> can be saved under.</summary>
    public bool CanSave => SaveLibrary.Clean(Name) is not null;

    /// <summary>Whether saving replaces a save of that name.</summary>
    public bool Overwrites => _library.Exists(Name);

    /// <summary>Picks a save: its name is taken (saving overwrites it).</summary>
    public void Select(int index)
    {
        if ((uint)index < (uint)_entries.Count)
        {
            Selected = index;
            Name = _entries[index].Name;
        }
    }

    /// <summary>NEW: a fresh name, nothing picked.</summary>
    public void New()
    {
        Selected = -1;
        Name = _library.NewName();
    }

    /// <summary>SAVE: writes <paramref name="save"/> under the name; false when the name is not valid.</summary>
    public bool Save(SaveGame save)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (!CanSave)
        {
            return false;
        }

        _library.Save(Name, save);
        _entries = [.. _library.List()];
        return true;
    }

    /// <summary>DELETE: removes the picked save.</summary>
    public void Delete()
    {
        if ((uint)Selected < (uint)_entries.Count)
        {
            _library.Delete(_entries[Selected].Name);
            _entries = [.. _library.List()];
            New();
        }
    }
}

/// <summary>
/// The load screen (LOAD GAME, F3; SHELL-014, manual "Load Game Screen"): the saves of single player games or of
/// campaigns (the control switches; STRINGS <c>[load] type_0</c>, <c>type_1</c>), LOAD, DELETE and CANCEL.
/// </summary>
public sealed class LoadGameScreen
{
    private readonly SaveLibrary _library;
    private List<SaveEntry> _all;

    /// <summary>Creates the screen; <paramref name="campaigns"/> picks the campaign saves first.</summary>
    public LoadGameScreen(SaveLibrary library, bool campaigns = false)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
        _all = [.. library.List()];
        Campaigns = campaigns;
    }

    /// <summary>Whether the campaign saves are shown (else the single player games).</summary>
    public bool Campaigns { get; private set; }

    /// <summary>The saves shown, newest first.</summary>
    public IReadOnlyList<SaveEntry> Entries => [.. _all.Where(e => (e.Kind == SaveKind.Campaign) == Campaigns)];

    /// <summary>The picked save, or −1.</summary>
    public int Selected { get; private set; } = -1;

    /// <summary>The picked save's entry, or null.</summary>
    public SaveEntry? Picked => (uint)Selected < (uint)Entries.Count ? Entries[Selected] : null;

    /// <summary>The control: campaigns or single player games.</summary>
    public void SwitchType()
    {
        Campaigns = !Campaigns;
        Selected = -1;
    }

    /// <summary>Picks a save.</summary>
    public void Select(int index) => Selected = (uint)index < (uint)Entries.Count ? index : -1;

    /// <summary>LOAD: the picked save read from the library, or null.</summary>
    /// <exception cref="System.IO.InvalidDataException">The file is not a valid save.</exception>
    public SaveGame? Load() => Picked is { } e ? _library.Load(e.Name) : null;

    /// <summary>DELETE: removes the picked save.</summary>
    public void Delete()
    {
        if (Picked is { } e)
        {
            _library.Delete(e.Name);
            _all = [.. _library.List()];
            Selected = -1;
        }
    }
}
