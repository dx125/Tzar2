using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using Tzar2.Content.Compiled;
using Tzar2.Content.Editing;

namespace Tzar2.Client.Editor;

/// <summary>
/// The prefab library dock (ADR 0292): the ready-made groups of a civilization (a village, a walled yard, a watch post, a
/// war band, a mining camp) and the player's own prefabs from the user folder, given to a kingdom and placed with one
/// click on the map; the selection saved as a new prefab.
/// </summary>
public partial class PrefabPanel : VBoxContainer
{
    private readonly EditorScreen _screen;
    private readonly ItemList _list = new() { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 220) };
    private readonly Label _about = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly string[] _civilizations;
    private List<Prefab> _prefabs = [];
    private int _civilization;
    private int _kingdom;

    /// <summary>Creates the dock.</summary>
    public PrefabPanel(EditorScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        _screen = screen;
        Name = "Prefabs";
        Ruleset rules = screen.Controller.Ruleset;
        _civilizations = [.. rules.Civilizations.Where(rules.IsPlayableCivilization)];
        GridContainer g = Forms.Grid(this);
        Forms.Choice(g, "Civilization", _civilizations.Length > 0 ? _civilizations : ["(none)"], 0, i =>
        {
            _civilization = i;
            Refresh();
        });
        Forms.Choice(g, "Kingdom", [.. Enumerable.Range(1, 8).Select(k => "Kingdom " + k.ToString(CultureInfo.InvariantCulture))], 0, i => _kingdom = i);
        AddChild(_list);
        AddChild(_about);
        var row = new HFlowContainer();
        Forms.Button(row, "Place on map", Place).TooltipText = "Then click the tile its first object goes to";
        Forms.Button(row, "Save selection…", SaveSelection);
        Forms.Button(row, "Delete", Delete).TooltipText = "Deletes one of your own prefabs";
        Forms.Button(row, "Refresh", Refresh);
        AddChild(row);
        _list.ItemSelected += i => _about.Text = (uint)i < (uint)_prefabs.Count ? $"{_prefabs[(int)i].Description} ({_prefabs[(int)i].Count} objects)" : string.Empty;
        _list.ItemActivated += _ => Place();
        Refresh();
    }

    /// <summary>The prefabs shown, the ready-made ones first.</summary>
    public IReadOnlyList<Prefab> Prefabs => _prefabs;

    /// <summary>Re-reads the ready-made prefabs and the user folder.</summary>
    public void Refresh()
    {
        string civ = _civilizations.Length > 0 ? _civilizations[_civilization] : string.Empty;
        _prefabs = [.. PrefabLibrary.BuiltIn(_screen.Controller.Ruleset, civ, (byte)_kingdom)];
        string folder = Files.UserFolder(PrefabLibrary.Folder);
        try
        {
            foreach (string path in Directory.GetFiles(folder, "*" + PrefabLibrary.Extension).Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    _prefabs.Add(PrefabLibrary.FromFile(path, File.ReadAllText(path)));
                }
                catch (FormatException e)
                {
                    GD.PrintErr($"Editor: prefab {path}: {e.Message}");
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PrintErr("Editor: cannot read the prefabs: " + e.Message);
        }

        _list.Clear();
        foreach (Prefab p in _prefabs)
        {
            _list.AddItem(p.Path is null ? $"{p.Name}  ({p.Count})" : $"★ {p.Name}  ({p.Count})");
        }

        _about.Text = _prefabs.Count == 0 ? "Nothing here yet." : "Pick one, then Place on map. ★ marks your own.";
    }

    /// <summary>Places prefab <paramref name="index"/> for the chosen kingdom with its first object at a tile; returns how many objects went in.</summary>
    public int PlaceAt(int index, int tileX, int tileY)
    {
        Prefab p = _prefabs[index];
        int placed = _screen.Controller.PasteAt(PrefabLibrary.WithOwner(p.Text, (byte)_kingdom), tileX, tileY, "Place prefab " + p.Name);
        _screen.Status($"Placed {placed} of the {p.Count} objects of {p.Name}{(placed < p.Count ? " (the rest did not fit there)" : string.Empty)}.");
        return placed;
    }

    private int Selected => _list.GetSelectedItems() is { Length: > 0 } s ? s[0] : -1;

    private void Place()
    {
        int index = Selected;
        if (index < 0)
        {
            _screen.Status("Pick a prefab first.");
            return;
        }

        _screen.PickPoint((x, y) => PlaceAt(index, x, y));
    }

    private void SaveSelection()
    {
        if (_screen.Controller.Selection.IsEmpty)
        {
            _screen.Status("Select the objects of the prefab on the map first.");
            return;
        }

        Files.Save(_screen, "prefab" + PrefabLibrary.Extension, path =>
        {
            string file = path.EndsWith(PrefabLibrary.Extension, StringComparison.OrdinalIgnoreCase) ? path : Path.ChangeExtension(path, null) + PrefabLibrary.Extension;
            try
            {
                File.WriteAllText(file, ObjectClipboard.Copy(_screen.Document.State, _screen.Controller.Selection, PrefabLibrary.NameOf(file)));
                _screen.Status("Saved the prefab " + file + ".");
                Refresh();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _screen.Status("Saving the prefab failed: " + e.Message);
            }
        }, ["*" + PrefabLibrary.Extension + " ; Tzar2 prefab"], Files.UserFolder(PrefabLibrary.Folder));
    }

    private void Delete()
    {
        int index = Selected;
        if (index < 0 || _prefabs[index].Path is not { } path)
        {
            _screen.Status("Only your own prefabs (★) can be deleted.");
            return;
        }

        var ask = new ConfirmationDialog { Title = "Delete prefab", DialogText = $"Delete the prefab file {path}?" };
        ask.Confirmed += () =>
        {
            try
            {
                File.Delete(path);
                Refresh();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _screen.Status("Deleting the prefab failed: " + e.Message);
            }

            ask.QueueFree();
        };
        ask.Canceled += ask.QueueFree;
        _screen.AddChild(ask);
        Forms.ShowCentered(ask);
    }
}
