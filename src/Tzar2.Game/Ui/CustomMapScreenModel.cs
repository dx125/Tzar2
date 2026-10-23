using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Session;

namespace Tzar2.Game.Ui;

/// <summary>An option of the custom map setup (<c>S_SINGLESETUP</c>; SHELL-007).</summary>
public enum CustomMapOption
{
    /// <summary>USE MAP SETTINGS (a check box): the map's own settings apply, only No Fog and Explore change.</summary>
    UseMapSettings,

    /// <summary>Number of Players.</summary>
    Players,

    /// <summary>Game Type.</summary>
    GameType,

    /// <summary>Victory Condition.</summary>
    Victory,

    /// <summary>The time or score limit.</summary>
    VictoryLimit,

    /// <summary>Population Limit.</summary>
    PopulationLimit,

    /// <summary>Difficulty Level.</summary>
    Difficulty,

    /// <summary>Starting Resources (Low, Normal, High or Default: the map's stockpiles).</summary>
    StartingResources,

    /// <summary>No Fog (a check box).</summary>
    NoFog,

    /// <summary>Explore (a check box).</summary>
    Explore,
}

/// <summary>One kingdom of the custom map setup.</summary>
/// <param name="Slot">Its kingdom (0-7).</param>
/// <param name="Name">Its name on the map.</param>
/// <param name="Human">Whether the player plays it.</param>
/// <param name="Civilization">0 RANDOM, else 1 + the index in the playable civilizations.</param>
/// <param name="Colour">Its colour 0-7.</param>
/// <param name="Team">0 none, 1-4.</param>
public sealed record CustomMapRow(byte Slot, string Name, bool Human, int Civilization, byte Colour, byte Team);

/// <summary>
/// The setup of a custom map (<c>S_SINGLESETUP</c>; SHELL-006, SHELL-007): one row per kingdom of the map with its name,
/// colour, civilization and team; USE MAP SETTINGS, which keeps the map's settings and kingdoms (only No Fog and Explore
/// change, ReTzar's reading of the original); unchecked, the number of kingdoms, game type, victory condition and its
/// limit, population limit, difficulty, starting resources and each kingdom's row can be changed.
/// </summary>
public sealed class CustomMapScreenModel
{
    /// <summary>Population limits offered (the random map screen's).</summary>
    public static ImmutableArray<int> PopulationLimits => LobbyScreenModel.PopulationLimits;

    private static readonly Sim.VictoryCondition[] Victories = [Sim.VictoryCondition.Standard, Sim.VictoryCondition.DestroyCastles, Sim.VictoryCondition.TimeLimit, Sim.VictoryCondition.ScoreLimit];

    private readonly CustomMapRow[] _rows;
    private readonly CustomMapRow[] _mapRows;
    private readonly MapProperties _map;

    /// <summary>Creates the setup of <paramref name="mission"/> with the playable <paramref name="civilizations"/> (keys).</summary>
    public CustomMapScreenModel(MissionContent mission, IReadOnlyList<string> civilizations)
    {
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentNullException.ThrowIfNull(civilizations);
        Civilizations = civilizations.Count > 0 ? civilizations : ["euro", "arab", "asia"];
        _map = mission.Scenario.Properties;
        _mapRows = [.. ScenarioMatch.SetupKingdoms(mission).Select(k => new CustomMapRow(
            (byte)k.Slot, k.Kingdom is { Name.Length: > 0 } named ? named.Name : $"Kingdom {k.Slot + 1}", k.Human, CivilizationIndex(k.Kingdom?.Civilization ?? string.Empty), (byte)k.Slot, 0))];
        _rows = [.. _mapRows];
        Players = _rows.Length;
        Victory = ScenarioMatch.MapVictory(_map.Victory) is var v && Array.IndexOf(Victories, v) >= 0 ? v : Sim.VictoryCondition.Standard;
        Difficulty = (Session.Difficulty)Math.Clamp(_map.Difficulty, 0, 2);
        PopulationLimit = PopulationLimits.Contains(_map.PopulationLimit) ? _map.PopulationLimit : 200;
        NoFog = _map.NoFog;
        Explore = _map.Explore;
    }

    /// <summary>The playable civilizations (keys).</summary>
    public IReadOnlyList<string> Civilizations { get; }

    /// <summary>USE MAP SETTINGS.</summary>
    public bool UseMapSettings { get; private set; } = true;

    /// <summary>The kingdoms that take part.</summary>
    public int Players { get; private set; }

    /// <summary>The fewest kingdoms: the player and one other (or the player alone on a one-kingdom map).</summary>
    public int FewestPlayers => Math.Min(2, _rows.Length);

    /// <summary>The most kingdoms: those of the map.</summary>
    public int MostPlayers => _rows.Length;

    /// <summary>The rows of the kingdoms that take part (with the map's settings: all of them, as the map has them).</summary>
    public IReadOnlyList<CustomMapRow> Rows => UseMapSettings ? _mapRows : Taking();

    /// <summary>Whether the game type is Deathmatch.</summary>
    public bool IsDeathmatch { get; private set; }

    /// <summary>The victory condition.</summary>
    public Sim.VictoryCondition Victory { get; private set; }

    /// <summary>The victory condition as the original numbers it (<c>vcond_0</c>-<c>vcond_3</c>).</summary>
    public int VictoryChoice => Array.IndexOf(Victories, Victory);

    /// <summary>Whether the victory condition has a limit.</summary>
    public bool HasVictoryLimit => Victory is Sim.VictoryCondition.TimeLimit or Sim.VictoryCondition.ScoreLimit;

    /// <summary>Whether the limit is a score (else minutes).</summary>
    public bool IsScoreLimit => Victory == Sim.VictoryCondition.ScoreLimit;

    /// <summary>The limit: minutes or score (the random map screen's steps).</summary>
    public int VictoryLimit { get; private set; } = RandomMapScreenModel.TimeStep;

    /// <summary>The population limit.</summary>
    public int PopulationLimit { get; private set; }

    /// <summary>The computers' difficulty (with the map's settings: the map's).</summary>
    public Session.Difficulty Difficulty { get; private set; }

    /// <summary>Starting resources: 0 low, 1 normal, 2 high, 3 the map's stockpiles (<c>resurcs_3</c> "Default").</summary>
    public int StartingResources { get; private set; } = 3;

    /// <summary>No Fog.</summary>
    public bool NoFog { get; private set; }

    /// <summary>Explore.</summary>
    public bool Explore { get; private set; }

    /// <summary>Whether <paramref name="option"/> can be changed now (with the map's settings only No Fog and Explore).</summary>
    public bool CanChange(CustomMapOption option) => option switch
    {
        CustomMapOption.UseMapSettings or CustomMapOption.NoFog or CustomMapOption.Explore => true,
        CustomMapOption.Players => !UseMapSettings && MostPlayers > FewestPlayers,
        CustomMapOption.VictoryLimit => !UseMapSettings && HasVictoryLimit,
        _ => !UseMapSettings,
    };

    /// <summary>Whether the kingdoms' rows can be changed (not with the map's settings).</summary>
    public bool CanChangeRows => !UseMapSettings;

    /// <summary>The civilization key of <paramref name="row"/>, or null for RANDOM.</summary>
    public string? CivilizationOf(CustomMapRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Civilization == 0 ? null : Civilizations[row.Civilization - 1];
    }

    /// <summary>Changes <paramref name="option"/> by one step (a check box toggles); nothing when it cannot change now.</summary>
    public void Change(CustomMapOption option, int step)
    {
        if (!CanChange(option))
        {
            return;
        }

        switch (option)
        {
            case CustomMapOption.UseMapSettings:
                UseMapSettings = !UseMapSettings;
                if (UseMapSettings)
                {
                    Difficulty = (Session.Difficulty)Math.Clamp(_map.Difficulty, 0, 2);
                }

                break;
            case CustomMapOption.Players:
                Players = FewestPlayers + Wrap(Players - FewestPlayers + step, MostPlayers - FewestPlayers + 1);
                break;
            case CustomMapOption.GameType:
                IsDeathmatch = !IsDeathmatch;
                break;
            case CustomMapOption.Victory:
                Victory = Victories[Wrap(VictoryChoice + step, Victories.Length)];
                VictoryLimit = IsScoreLimit ? RandomMapScreenModel.ScoreStep : RandomMapScreenModel.TimeStep;
                break;
            case CustomMapOption.VictoryLimit:
                (int s, int max) = IsScoreLimit ? (RandomMapScreenModel.ScoreStep, RandomMapScreenModel.ScoreMax) : (RandomMapScreenModel.TimeStep, RandomMapScreenModel.TimeMax);
                VictoryLimit = s * (1 + Wrap((VictoryLimit / s) - 1 + step, max / s));
                break;
            case CustomMapOption.PopulationLimit:
                PopulationLimit = PopulationLimits[Wrap(Math.Max(0, PopulationLimits.IndexOf(PopulationLimit)) + step, PopulationLimits.Length)];
                break;
            case CustomMapOption.Difficulty:
                Difficulty = (Session.Difficulty)Wrap((int)Difficulty + step, 3);
                break;
            case CustomMapOption.StartingResources:
                StartingResources = Wrap(StartingResources + step, 4);
                break;
            case CustomMapOption.NoFog:
                NoFog = !NoFog;
                break;
            case CustomMapOption.Explore:
                Explore = !Explore;
                break;
        }
    }

    /// <summary>Changes a field of the kingdom in row <paramref name="row"/> of <see cref="Rows"/> (not with the map's settings).</summary>
    public void ChangeRow(int row, RandomMapRowField field, int step)
    {
        IReadOnlyList<CustomMapRow> rows = Rows;
        if (!CanChangeRows || (uint)row >= (uint)rows.Count)
        {
            return;
        }

        int i = Array.FindIndex(_rows, r => r.Slot == rows[row].Slot);
        CustomMapRow r = _rows[i];
        switch (field)
        {
            case RandomMapRowField.Civilization:
                _rows[i] = r with { Civilization = Wrap(r.Civilization + step, Civilizations.Count + 1) };
                break;
            case RandomMapRowField.Team:
                _rows[i] = r with { Team = (byte)Wrap(r.Team + step, RandomMapScreenModel.MaxTeams + 1) };
                break;
            case RandomMapRowField.Colour:
                var taken = rows.Where(x => x.Slot != r.Slot).Select(x => (int)x.Colour).ToHashSet();
                int c = r.Colour;
                for (int k = 0; k < RandomMapScreenModel.MaxPlayers; k++)
                {
                    c = Wrap(c + step, RandomMapScreenModel.MaxPlayers);
                    if (!taken.Contains(c))
                    {
                        _rows[i] = r with { Colour = (byte)c };
                        break;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// The match settings of these choices, with <paramref name="ai"/>. A RANDOM civilization is drawn from
    /// <paramref name="seed"/> (the map's kingdoms with RANDOM on the map keep the map's way: the first playable one).
    /// </summary>
    public CustomMapSettings ToSettings(SkirmishAi ai, ulong seed = 0)
    {
        ArgumentNullException.ThrowIfNull(ai);
        if (UseMapSettings)
        {
            return new CustomMapSettings { Difficulty = Difficulty, NoFog = NoFog, Explored = Explore, Ai = ai };
        }

        var kingdoms = ImmutableArray.CreateBuilder<CustomKingdom>();
        foreach (CustomMapRow r in Taking())
        {
            int civ = r.Civilization > 0 ? r.Civilization - 1 : (int)(Mix(seed + r.Slot) % (ulong)Civilizations.Count);
            kingdoms.Add(new CustomKingdom(r.Slot, Civilizations[civ], r.Colour, r.Team));
        }

        return new CustomMapSettings
        {
            Difficulty = Difficulty,
            StartingResources = StartingResources is >= 0 and <= 2 ? StartingResources : -1,
            PopulationLimit = PopulationLimit,
            NoFog = NoFog,
            Explored = Explore,
            Ai = ai,
            Players = Players < MostPlayers ? Players : 0,
            Victory = Victory,
            GameType = IsDeathmatch ? Sim.GameType.Deathmatch : Sim.GameType.Standard,
            ScoreLimit = IsScoreLimit ? VictoryLimit : null,
            TimeLimitMinutes = Victory == Sim.VictoryCondition.TimeLimit ? VictoryLimit : null,
            Kingdoms = kingdoms.ToImmutable(),
        };
    }

    // The player and the first others in kingdom order, as many as Players (ScenarioMatch keeps the same).
    private CustomMapRow[] Taking()
    {
        var kept = new List<CustomMapRow>();
        int others = Players - (_rows.Any(r => r.Human) ? 1 : 0);
        foreach (CustomMapRow r in _rows)
        {
            if (r.Human)
            {
                kept.Add(r);
            }
            else if (others > 0)
            {
                kept.Add(r);
                others--;
            }
        }

        return [.. kept];
    }

    private int CivilizationIndex(string key)
    {
        int i = Civilizations.ToList().IndexOf(key);
        return i < 0 ? 0 : i + 1;
    }

    private static int Wrap(int v, int n) => ((v % n) + n) % n;

    private static ulong Mix(ulong x)
    {
        x += 0x9E37_79B9_7F4A_7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return x ^ (x >> 31);
    }
}
