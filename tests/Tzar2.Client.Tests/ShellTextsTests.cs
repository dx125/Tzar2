using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Client.Logic;
using Tzar2.Content.MapGen;
using Xunit;

namespace Tzar2.Client.Tests;

// UI review findings UI-T1, UI-T2, UI-T3, UI-T5 (ADR 0200): shell labels come from STRINGS.INI where it has them.
public class ShellTextsTests
{
    // A stand-in for STRINGS.INI (keys only; the words are invented).
    private static readonly Dictionary<(string, string), string> Strings = new()
    {
        [("mainscr", "pref")] = "PREFS",
        [("general", "yes")] = "Aye",
        [("general", "no")] = "Nay",
        [("sp_setuprnd", "m_type_3")] = "Mainland",
        [("sp_setuprnd", "m_size_6")] = "Whole World",
        [("msg", "game_random")] = "Rolling the map",
    };

    private static string Lookup(string section, string key, string fallback) => Strings.TryGetValue((section, key), out string? s) ? s : fallback;

    [Fact]
    public void Labels_come_from_their_strings_ini_keys()
    {
        Assert.Equal("PREFS", ShellTexts.Preferences.In(Lookup));
        Assert.Equal(("Aye", "Nay"), (ShellTexts.YesNo(true).In(Lookup), ShellTexts.YesNo(false).In(Lookup)));
        Assert.Equal("Mainland", ShellTexts.MapType(RandomMapType.Continental).In(Lookup));
        Assert.Equal("Whole World", ShellTexts.MapSize(RandomMapSize.RealWorld).In(Lookup));
        Assert.Equal("Rolling the map", ShellTexts.Loading(LoadingStage.RandomMap).In(Lookup));
    }

    [Fact]
    public void Map_types_and_sizes_use_the_originals_indices()
    {
        // The original's sp_setuprnd lists m_type_0..5 (Random ... Dry) and m_size_0..6 (Tiny ... Real World) in our order.
        Assert.Equal(Enumerable.Range(0, 6).Select(i => $"m_type_{i}"), Enum.GetValues<RandomMapType>().Select(t => ShellTexts.MapType(t).Key));
        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"m_size_{i}"), Enum.GetValues<RandomMapSize>().Select(s => ShellTexts.MapSize(s).Key));
        Assert.All(Enum.GetValues<RandomMapType>(), t => Assert.Equal("sp_setuprnd", ShellTexts.MapType(t).Section));
    }

    [Fact]
    public void Without_the_package_the_fallbacks_are_capitalised_words()
    {
        Assert.Equal("Continental", ShellTexts.MapType(RandomMapType.Continental).In(null));
        Assert.Equal("Real World", ShellTexts.MapSize(RandomMapSize.RealWorld).In(null));
        Assert.Equal("Grass", ShellTexts.Terrain(MapTerrainSet.Grass).In(Lookup)); // no key: always ours
        Assert.Equal("PREFERENCES", ShellTexts.Preferences.In(null));
        Assert.Equal(("msg", "game_load"), (ShellTexts.Loading(LoadingStage.Load).Section, ShellTexts.Loading(LoadingStage.Load).Key));
        Assert.Equal("game_create", ShellTexts.Loading(LoadingStage.NewGame).Key);
    }
}
