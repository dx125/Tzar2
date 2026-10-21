using System;
using System.Collections.Generic;

namespace Tzar2.Game.Input;

/// <summary>Keys the game binds, independent of the engine's key codes (the client maps its key events onto these).</summary>
public enum GameKey
{
    /// <summary>No key.</summary>
    None = 0,
#pragma warning disable CS1591 // Self-describing key names.
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Space, Enter, Escape, Backspace, Tab, Delete, Home, End, PageUp, PageDown,
    Left, Right, Up, Down,
    Plus, Minus, Backquote, Slash, Backslash, BracketLeft, BracketRight, Comma, Period, Pause,
#pragma warning restore CS1591
}

/// <summary>Modifier keys.</summary>
[Flags]
public enum KeyMods
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Shift.</summary>
    Shift = 1,

    /// <summary>Ctrl.</summary>
    Ctrl = 2,

    /// <summary>Alt.</summary>
    Alt = 4,
}

/// <summary>What a key does (TDD §15.2, Appendix E.6, <c>HELP/HK.TXT</c>).</summary>
public enum GameAction
{
    /// <summary>Nothing.</summary>
    None = 0,
#pragma warning disable CS1591 // Documented by the binding table and docs/mechanics/hotkey-parity.md.
    MenuScreen, SaveDialog, LoadDialog, Scores, Diplomacy, Tribute, Objectives, Options, Screenshot,
    Chat, SendLocation, CenterSelection, UseDefaultItem, DeselectOrBack,
    LastEvent, NextEvent, PreviousEvent, LastLocation, NextLocation, PreviousLocation, LastEventAndSelect,
    ShowRallyPoint, DeleteSelected, SpeedUp, SpeedDown, TranslucentTrees, ShowInventory, InventoryPrevious, InventoryNext,
    CycleSelection, HealthBarsHold, HealthBarsToggle,
    AssignGroup, SelectGroup, ClearGroup, SelectBuildingClass,
    SelectAboveAverageExperience, SelectBelowAverageExperience, SelectAboveAverageHealth, SelectBelowAverageHealth, SelectUngrouped,
    ScrollLeft, ScrollRight, ScrollUp, ScrollDown, Pause,
#pragma warning restore CS1591
}

/// <summary>How far an action is implemented in this build (the parity checklist reports it).</summary>
public enum ParityStatus
{
    /// <summary>Works as in the original.</summary>
    Done = 0,

    /// <summary>Works, with a documented difference.</summary>
    Partial = 1,

    /// <summary>Bound, but the feature behind it does not exist yet (the key shows a message).</summary>
    Blocked = 2,
}

/// <summary>One key binding.</summary>
/// <param name="Id">Stable id (rebinding, the parity checklist).</param>
/// <param name="Key">The key.</param>
/// <param name="Mods">Required modifiers (exact match).</param>
/// <param name="Action">The action.</param>
/// <param name="Argument">Action argument (group digit, letter of a building class).</param>
/// <param name="Status">Implementation status.</param>
/// <param name="Note">What differs or blocks it.</param>
public sealed record HotkeyBinding(string Id, GameKey Key, KeyMods Mods, GameAction Action, int Argument, ParityStatus Status, string Note);

/// <summary>
/// The original keyboard map (Appendix E.6 and <c>HELP/HK.TXT</c>), with rebinding (REQ-UI-03). Command-card letters
/// (<c>MENU.INI</c>) and the Ctrl+letter building classes (<c>KEYS.INI</c>) come from the imported tables; this table
/// holds the fixed keys. Engine-free so it is unit-tested.
/// </summary>
public sealed class HotkeyTable
{
    private readonly List<HotkeyBinding> _bindings;

    /// <summary>Creates a table (default: <see cref="Original"/>).</summary>
    public HotkeyTable(IEnumerable<HotkeyBinding>? bindings = null) => _bindings = [.. bindings ?? Original()];

    /// <summary>All bindings.</summary>
    public IReadOnlyList<HotkeyBinding> Bindings => _bindings;

    /// <summary>The original bindings.</summary>
    public static IReadOnlyList<HotkeyBinding> Original()
    {
        const ParityStatus Done = ParityStatus.Done, Partial = ParityStatus.Partial, Blocked = ParityStatus.Blocked;
        var list = new List<HotkeyBinding>
        {
            new("menu", GameKey.F1, KeyMods.None, GameAction.MenuScreen, 0, Done, string.Empty),
            new("menu-f10", GameKey.F10, KeyMods.None, GameAction.MenuScreen, 0, Done, string.Empty),
            new("save", GameKey.F2, KeyMods.None, GameAction.SaveDialog, 0, Partial, "quick save of campaign missions (ADR 0133); the save dialog and skirmish/MP saves are M6"),
            new("load", GameKey.F3, KeyMods.None, GameAction.LoadDialog, 0, Partial, "quick load of campaign missions (ADR 0133); the load dialog is M6"),
            new("scores", GameKey.F4, KeyMods.None, GameAction.Scores, 0, Partial, "every kingdom's sim score and statistics as messages (ADR 0106)"),
            new("diplomacy", GameKey.F5, KeyMods.None, GameAction.Diplomacy, 0, Blocked, "the sim has diplomacy (SetDiplomacy, ADR 0102); the dialog is not built yet"),
            new("tribute", GameKey.F6, KeyMods.None, GameAction.Tribute, 0, Blocked, "the sim has tribute (ADR 0102); the dialog is not built yet"),
            new("objectives", GameKey.F8, KeyMods.None, GameAction.Objectives, 0, Done, "the quest log of scenario maps (ADR 0132); the objective message in skirmishes"),
            new("options", GameKey.F9, KeyMods.None, GameAction.Options, 0, Partial, "volume and game-speed options only"),
            new("screenshot", GameKey.F11, KeyMods.None, GameAction.Screenshot, 0, Done, "PNG into the user folder"),
            new("chat", GameKey.Enter, KeyMods.None, GameAction.Chat, 0, Partial, "multiplayer: the chat line (ADR 0168); single player has none, so its cheat codes cannot be typed yet"),
            new("send-location", GameKey.Enter, KeyMods.Ctrl, GameAction.SendLocation, 0, Blocked, "multiplayer chat has no location messages yet"),
            new("center", GameKey.Space, KeyMods.None, GameAction.CenterSelection, 0, Done, string.Empty),
            new("default-item", GameKey.Backspace, KeyMods.None, GameAction.UseDefaultItem, 0, Done, string.Empty),
            new("escape", GameKey.Escape, KeyMods.None, GameAction.DeselectOrBack, 0, Done, "cancels a pending order or placement first"),
            new("event-last", GameKey.Home, KeyMods.None, GameAction.LastEvent, 0, Done, "unit trained / building completed events"),
            new("event-next", GameKey.PageDown, KeyMods.None, GameAction.NextEvent, 0, Done, string.Empty),
            new("event-prev", GameKey.PageUp, KeyMods.None, GameAction.PreviousEvent, 0, Done, string.Empty),
            new("location-last", GameKey.Home, KeyMods.Shift, GameAction.LastLocation, 0, Partial, "locations are 'under attack' alerts until players can send them (M6)"),
            new("location-next", GameKey.PageDown, KeyMods.Shift, GameAction.NextLocation, 0, Partial, "as above"),
            new("location-prev", GameKey.PageUp, KeyMods.Shift, GameAction.PreviousLocation, 0, Partial, "as above"),
            new("event-select", GameKey.Home, KeyMods.Ctrl, GameAction.LastEventAndSelect, 0, Done, string.Empty),
            new("rally", GameKey.End, KeyMods.None, GameAction.ShowRallyPoint, 0, Done, string.Empty),
            new("delete", GameKey.Delete, KeyMods.None, GameAction.DeleteSelected, 0, Partial, "castles are deleted with Shift+Delete only (Appendix E.6)"),
            new("delete-important", GameKey.Delete, KeyMods.Shift, GameAction.DeleteSelected, 1, Done, string.Empty),
            new("speed-up", GameKey.Plus, KeyMods.None, GameAction.SpeedUp, 0, Done, string.Empty),
            new("speed-down", GameKey.Minus, KeyMods.None, GameAction.SpeedDown, 0, Done, string.Empty),
            new("trees", GameKey.Backslash, KeyMods.None, GameAction.TranslucentTrees, 0, Partial, "trees are drawn half transparent, not with the original's stencil look (§13.3)"),
            new("inventory", GameKey.Slash, KeyMods.None, GameAction.ShowInventory, 0, Blocked, "the inventory view is not built yet (HUD-017); HK.TXT swaps the slash names, verify (Appendix E.6)"),
            new("inventory-prev", GameKey.Comma, KeyMods.Shift, GameAction.InventoryPrevious, 0, Blocked, "the inventory view (HUD-017)"),
            new("inventory-next", GameKey.Period, KeyMods.Shift, GameAction.InventoryNext, 0, Blocked, "the inventory view (HUD-017)"),
            new("tab", GameKey.Tab, KeyMods.None, GameAction.CycleSelection, 0, Done, string.Empty),
            new("bars-hold", GameKey.Backquote, KeyMods.None, GameAction.HealthBarsHold, 0, Done, string.Empty),
            new("bars-toggle", GameKey.Backquote, KeyMods.Ctrl, GameAction.HealthBarsToggle, 0, Done, string.Empty),
            new("group-clear", GameKey.D0, KeyMods.Ctrl, GameAction.ClearGroup, 0, Done, string.Empty),
            new("xp-above", GameKey.BracketRight, KeyMods.Ctrl, GameAction.SelectAboveAverageExperience, 0, Done, string.Empty),
            new("xp-below", GameKey.BracketLeft, KeyMods.Ctrl, GameAction.SelectBelowAverageExperience, 0, Done, string.Empty),
            new("hp-above", GameKey.BracketRight, KeyMods.Alt, GameAction.SelectAboveAverageHealth, 0, Done, string.Empty),
            new("hp-below", GameKey.BracketLeft, KeyMods.Alt, GameAction.SelectBelowAverageHealth, 0, Done, string.Empty),
            new("ungrouped", GameKey.Backslash, KeyMods.Alt, GameAction.SelectUngrouped, 0, Done, string.Empty),
            new("scroll-left", GameKey.Left, KeyMods.None, GameAction.ScrollLeft, 0, Done, string.Empty),
            new("scroll-right", GameKey.Right, KeyMods.None, GameAction.ScrollRight, 0, Done, string.Empty),
            new("scroll-up", GameKey.Up, KeyMods.None, GameAction.ScrollUp, 0, Done, string.Empty),
            new("scroll-down", GameKey.Down, KeyMods.None, GameAction.ScrollDown, 0, Done, string.Empty),
            new("pause", GameKey.Pause, KeyMods.None, GameAction.Pause, 0, Partial, "key assumed; the original's pause key is not in HK.TXT"),
        };

        for (int d = 1; d <= 9; d++)
        {
            list.Add(new($"group-assign-{d}", GameKey.D0 + d, KeyMods.Ctrl, GameAction.AssignGroup, d, Done, string.Empty));
            list.Add(new($"group-select-{d}", GameKey.D0 + d, KeyMods.None, GameAction.SelectGroup, d, Partial, "pressing twice centres on the group (assumed)"));
        }

        for (GameKey letter = GameKey.A; letter <= GameKey.Z; letter++)
        {
            list.Add(new($"building-{(char)('a' + (letter - GameKey.A))}", letter, KeyMods.Ctrl, GameAction.SelectBuildingClass, letter - GameKey.A, Done, "classes from KEYS.INI"));
        }

        return list;
    }

    /// <summary>The binding of a key press, or null. Shift is ignored for keys that have no Shift binding.</summary>
    public HotkeyBinding? Find(GameKey key, KeyMods mods)
    {
        HotkeyBinding? relaxed = null;
        foreach (HotkeyBinding b in _bindings)
        {
            if (b.Key != key)
            {
                continue;
            }

            if (b.Mods == mods)
            {
                return b;
            }

            if (b.Mods == (mods & ~KeyMods.Shift))
            {
                relaxed = b;
            }
        }

        return relaxed;
    }

    /// <summary>Rebinds <paramref name="id"/> (REQ-UI-03: all rebindable). Another binding on the same chord is removed.</summary>
    /// <exception cref="ArgumentException">Unknown id.</exception>
    public void Rebind(string id, GameKey key, KeyMods mods)
    {
        int index = _bindings.FindIndex(b => b.Id == id);
        if (index < 0)
        {
            throw new ArgumentException($"Unknown binding '{id}'.", nameof(id));
        }

        _bindings.RemoveAll(b => b.Id != id && b.Key == key && b.Mods == mods);
        index = _bindings.FindIndex(b => b.Id == id);
        _bindings[index] = _bindings[index] with { Key = key, Mods = mods };
    }
}
