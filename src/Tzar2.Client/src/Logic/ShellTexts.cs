using System;
using Tzar2.Content.MapGen;

namespace Tzar2.Client.Logic;

/// <summary>
/// A text of the interface: the <c>STRINGS.INI</c> entry the original shows (section and key), and the words shown when
/// the package has none.
/// </summary>
/// <param name="Section">The STRINGS.INI section, or empty for a text of Tzar2's own.</param>
/// <param name="Key">The key in the section.</param>
/// <param name="Fallback">The text without the package.</param>
public readonly record struct UiText(string Section, string Key, string Fallback)
{
    /// <summary>The text from <paramref name="lookup"/> (section, key, fallback), e.g. <see cref="PresentationCatalog.Text"/>.</summary>
    public string In(Func<string, string, string, string>? lookup) => lookup is null || Section.Length == 0 ? Fallback : lookup(Section, Key, Fallback);
}

/// <summary>
/// Which original text each shell label shows (UI review findings UI-T1, UI-T2, UI-T3, UI-T5; ADR 0200). Our words
/// are left only where STRINGS.INI has no text.
/// </summary>
public static class ShellTexts
{
    /// <summary>The main menu's settings item: the original's preferences (UI-T1).</summary>
    public static UiText Preferences => new("mainscr", "pref", "PREFERENCES");

    /// <summary>A random map type: <c>sp_setuprnd.m_type_N</c>, N the original's index, the same as ours (UI-T2).</summary>
    public static UiText MapType(RandomMapType type) => new("sp_setuprnd", $"m_type_{(int)type}", Capitalized(RandomMapNames.Name(type)));

    /// <summary>A random map size: <c>sp_setuprnd.m_size_N</c> (UI-T2).</summary>
    public static UiText MapSize(RandomMapSize size) => new("sp_setuprnd", $"m_size_{(int)size}", size == RandomMapSize.RealWorld ? "Real World" : Capitalized(RandomMapNames.Name(size)));

    /// <summary>A terrain set: STRINGS.INI has no names for them, so ours, capitalised like the others (UI-T3).</summary>
    public static UiText Terrain(MapTerrainSet set) => new(string.Empty, string.Empty, Capitalized(RandomMapNames.Name(set)));

    /// <summary>Yes or No of an option (<c>general.yes</c>, <c>general.no</c>; UI-T2).</summary>
    public static UiText YesNo(bool yes) => yes ? new("general", "yes", "Yes") : new("general", "no", "No");

    /// <summary>
    /// The loading screen's line for what is being built (UI-T5): <c>msg.game_random</c> for a random map,
    /// <c>msg.game_create</c> for a map or mission, <c>msg.game_load</c> for anything read from disk.
    /// </summary>
    public static UiText Loading(LoadingStage stage) => stage switch
    {
        LoadingStage.RandomMap => new("msg", "game_random", "Generating Random Map..."),
        LoadingStage.NewGame => new("msg", "game_create", "Initializing Game..."),
        _ => new("msg", "game_load", "Loading..."),
    };

    private static string Capitalized(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
}

/// <summary>What the loading screen waits for.</summary>
public enum LoadingStage
{
    /// <summary>Files read from disk: maps, campaigns, a saved game.</summary>
    Load,

    /// <summary>A random map being generated.</summary>
    RandomMap,

    /// <summary>A game being set up from a map or a mission.</summary>
    NewGame,
}
