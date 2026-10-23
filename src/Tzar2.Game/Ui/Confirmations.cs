using System;

namespace Tzar2.Game.Ui;

/// <summary>What a confirmation asks before it is done (SHELL-027).</summary>
public enum ConfirmKind
{
    /// <summary>Leaving Tzar from the main menu.</summary>
    Quit,

    /// <summary>Leaving a single player game from its menu: the kingdom resigns.</summary>
    Resign,

    /// <summary>Leaving a multiplayer game from its menu.</summary>
    Disconnect,
}

/// <summary>A yes-or-no question in the original's words (<c>STRINGS.INI [q]</c>, <c>[general]</c>, <c>[menu]</c>).</summary>
/// <param name="Kind">What it asks.</param>
/// <param name="Title">The window's title.</param>
/// <param name="Question">The question.</param>
/// <param name="Yes">The button that does it.</param>
/// <param name="No">The button that goes back.</param>
public readonly record struct Confirmation(ConfirmKind Kind, string Title, string Question, string Yes, string No);

/// <summary>
/// The confirmations of the original (SHELL-027): leaving Tzar asks "Are you sure you want to quit the game?" under
/// "Exit Tzar"; the game menu's QUIT asks "Are you sure you want to resign?" in a single player game and "Are you sure you
/// want to disconnect?" in a multiplayer one (ReTzar's reading of the original's menus).
/// </summary>
public static class Confirmations
{
    /// <summary>The kind of the game menu's QUIT.</summary>
    public static ConfirmKind GameMenuQuit(bool multiplayer) => multiplayer ? ConfirmKind.Disconnect : ConfirmKind.Resign;

    /// <summary>The confirmation of <paramref name="kind"/>; <paramref name="text"/> looks up STRINGS.INI (section, key, fallback).</summary>
    public static Confirmation For(ConfirmKind kind, Func<string, string, string, string>? text)
    {
        string T(string section, string key, string fallback) => text?.Invoke(section, key, fallback) ?? fallback;
        (string title, string question) = kind switch
        {
            ConfirmKind.Quit => (T("q", "quit_title", "Exit Tzar"), T("q", "quit", "Are you sure you want to quit the game?")),
            ConfirmKind.Resign => (T("menu", "quit", "QUIT"), T("q", "resign", "Are you sure you want to resign?")),
            _ => (T("menu", "disconnect", "DISCONNECT"), T("q", "disconnect", "Are you sure you want to disconnect?")),
        };
        return new Confirmation(kind, title, question, T("general", "yes", "Yes"), T("general", "no", "No"));
    }
}
