using System;
using System.Linq;
using Tzar2.Content.MapGen;
using Tzar2.Game.Maps;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;
using Tzar2.Sim;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>The random map screen (<c>S_SINGLERND</c>): its kingdoms, options and memory.</summary>
public class RandomMapScreenModelTests
{
    private static readonly string[] Civs = ["euro", "arab", "asia"];
    private static readonly AiScriptOption[] Scripts = [new("Random", string.Empty), new("Rush", "rush"), new("Boom", "boom")];

    private static RandomMapScreenModel New(byte[]? saved = null) => new(Civs, Scripts, saved);

    [Fact]
    [Trait("Rule", "SHELL-010")]
    [Trait("Rule", "AI-001")]
    public void Each_computer_gets_its_own_script_civilization_and_team()
    {
        RandomMapScreenModel m = New();
        m.Change(RandomMapOption.Players, +1);
        Assert.Equal(3, m.Rows.Count);
        m.ChangeRow(1, RandomMapRowField.Script, +1);
        m.ChangeRow(2, RandomMapRowField.Script, -1);
        m.ChangeRow(0, RandomMapRowField.Script, +1); // the player has no script
        m.ChangeRow(1, RandomMapRowField.Civilization, +2); // RANDOM -> arab
        m.ChangeRow(2, RandomMapRowField.Civilization, +3); // RANDOM -> asia
        m.ChangeRow(1, RandomMapRowField.Team, +1);
        m.ChangeRow(2, RandomMapRowField.Team, +1);

        MatchDescriptor d = m.Describe(42, "Me");
        Assert.Equal(["euro", "arab", "asia"], d.Players.Select(p => p.Civilization));
        Assert.Equal(["", "rush", "boom"], d.Players.Select(p => p.AiScript));
        Assert.Equal([ControllerKind.Human, ControllerKind.Computer, ControllerKind.Computer], d.Players.Select(p => p.Controller));
        Assert.Equal([0, 1, 1], d.Players.Select(p => (int)p.Team));
        Assert.Equal("Me", d.Players[0].Name);
        Assert.Equal(42UL, d.Settings.Seed);
    }

    [Fact]
    [Trait("Rule", "SHELL-010")]
    public void Game_type_victory_and_its_limit_difficulty_and_resources_are_chosen()
    {
        RandomMapScreenModel m = New();
        Assert.Equal(Difficulty.Normal, m.Difficulty);
        Assert.Equal(1, m.Settings.StartingResources); // Normal
        Assert.False(m.HasVictoryLimit);

        m.Change(RandomMapOption.GameType, +1);
        Assert.Equal(GameType.Deathmatch, m.Settings.GameType);
        m.Change(RandomMapOption.Victory, +2);
        Assert.Equal(VictoryCondition.TimeLimit, m.Settings.Victory);
        Assert.Equal(30, m.VictoryLimit);
        m.Change(RandomMapOption.VictoryLimit, -1); // 30-180 minutes by 30, wrapping
        Assert.Equal(180, m.VictoryLimit);
        m.Change(RandomMapOption.Victory, +1);
        Assert.Equal(VictoryCondition.ScoreLimit, m.Settings.Victory);
        Assert.Equal((3, true, true), (m.VictoryChoice, m.IsScoreLimit, m.IsDeathmatch)); // vcond_3
        Assert.Equal(200, m.VictoryLimit);
        m.Change(RandomMapOption.VictoryLimit, +1);
        Assert.Equal(400, m.Settings.ScoreLimit);
        m.Change(RandomMapOption.Difficulty, +1);
        m.Change(RandomMapOption.StartingResources, +1);
        m.Change(RandomMapOption.NoFog, +1);
        m.Change(RandomMapOption.Explore, +1);

        MatchDescriptor d = m.Describe(1);
        Assert.Equal(Difficulty.Hard, d.Players[1].Difficulty);
        Assert.Equal(2, d.Settings.StartingResources);
        Assert.True(d.Settings.NoFog && d.Settings.Explored);
        Assert.Equal(GameType.Deathmatch, d.Settings.GameType);
        Assert.Equal(400, d.Settings.ScoreLimit);
    }

    [Fact]
    [Trait("Rule", "SHELL-011")]
    public void The_map_has_size_type_terrain_resources_and_five_options()
    {
        RandomMapScreenModel m = New();
        // The original's defaults: guarded town, animals, magic items and gateways on, cliffs off.
        Assert.True(m.IsOn(RandomMapOption.GuardedTown) && m.IsOn(RandomMapOption.Animals) && m.IsOn(RandomMapOption.MagicItems) && m.IsOn(RandomMapOption.Gateways));
        Assert.False(m.IsOn(RandomMapOption.Cliffs));

        m.Change(RandomMapOption.Cliffs, +1);
        m.Change(RandomMapOption.Animals, +1);
        m.Change(RandomMapOption.MapResources, +1);
        m.Change(RandomMapOption.MapSize, +1);
        m.Change(RandomMapOption.MapType, +1);
        m.Change(RandomMapOption.Terrain, -1);

        var source = Assert.IsType<RandomMapSource>(m.Describe(7).Settings.MapSource());
        Assert.Equal(MapResourceLevel.High, source.Resources);
        Assert.Equal(RandomMapSize.Medium, source.Size);
        Assert.Equal(MapTerrainSet.Winter, source.Set);
        Assert.True(source.Features.HasFlag(MapFeatures.Cliffs));
        Assert.False(source.Features.HasFlag(MapFeatures.Animals));
        Assert.True(source.Features.HasFlag(MapFeatures.GuardedTowns));
    }

    [Fact]
    [Trait("Rule", "SHELL-010")]
    public void A_smaller_map_holds_fewer_kingdoms_and_colours_stay_distinct()
    {
        RandomMapScreenModel m = New();
        for (int i = 0; i < 4; i++)
        {
            m.Change(RandomMapOption.Players, +1);
        }

        Assert.Equal(6, m.Players); // a small map holds 6
        m.Change(RandomMapOption.Players, +1);
        Assert.Equal(2, m.Players); // wraps

        m.Change(RandomMapOption.Players, +1);
        m.ChangeRow(0, RandomMapRowField.Colour, +1); // colour 1 is the second kingdom's: skipped
        Assert.Equal(3, m.Rows[0].Colour);
        Assert.Equal(m.Rows.Count, m.Rows.Select(r => r.Colour).Distinct().Count());
    }

    [Fact]
    [Trait("Rule", "SHELL-010")]
    public void A_random_civilization_is_drawn_from_the_seed()
    {
        RandomMapScreenModel m = New();
        string[] a = [.. Enumerable.Range(0, 20).Select(s => m.Describe((ulong)s).Players[1].Civilization)];
        Assert.Equal(a, Enumerable.Range(0, 20).Select(s => m.Describe((ulong)s).Players[1].Civilization));
        Assert.True(a.Distinct().Count() > 1);
    }

    [Fact]
    [Trait("Rule", "SHELL-012")]
    public void The_settings_are_kept_for_the_next_time()
    {
        RandomMapScreenModel m = New();
        m.Change(RandomMapOption.Players, +2);
        m.ChangeRow(3, RandomMapRowField.Script, +2);
        m.ChangeRow(3, RandomMapRowField.Team, +2);
        m.ChangeRow(2, RandomMapRowField.Civilization, +1);
        m.Change(RandomMapOption.Victory, +3);
        m.Change(RandomMapOption.VictoryLimit, +4);
        m.Change(RandomMapOption.Difficulty, -1);
        m.Change(RandomMapOption.Cliffs, +1);
        m.Change(RandomMapOption.PopulationLimit, +1);

        RandomMapScreenModel again = New(m.ToBytes());
        Assert.Equal(m.Players, again.Players);
        Assert.Equal(m.Difficulty, again.Difficulty);
        Assert.Equal(m.Settings, again.Settings);
        Assert.Equal(m.Rows, again.Rows);
        Assert.Equal(m.Describe(5), again.Describe(5), new DescriptorBytes());

        // A broken or older file gives the defaults.
        Assert.Equal(New().Settings, New([1, 2]).Settings);
        Assert.Equal(New().Settings, New([9, 9, 9, 9]).Settings);
    }

    private sealed class DescriptorBytes : System.Collections.Generic.IEqualityComparer<MatchDescriptor>
    {
        public bool Equals(MatchDescriptor? x, MatchDescriptor? y) => x!.ToBytes().AsSpan().SequenceEqual(y!.ToBytes());

        public int GetHashCode(MatchDescriptor obj) => 0;
    }
}
