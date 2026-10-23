using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Tzar2.Content.MapGen;
using Tzar2.Game.Session;
using Tzar2.Sim;

namespace Tzar2.Game.Ui;

/// <summary>An option of the random map screen (<c>S_SINGLERND</c>; SHELL-010, SHELL-011).</summary>
public enum RandomMapOption
{
    /// <summary>Number of Players.</summary>
    Players,

    /// <summary>Game Type (Standard or Deathmatch).</summary>
    GameType,

    /// <summary>Victory Condition.</summary>
    Victory,

    /// <summary>The time or score limit of the victory condition.</summary>
    VictoryLimit,

    /// <summary>Population Limit.</summary>
    PopulationLimit,

    /// <summary>Difficulty Level.</summary>
    Difficulty,

    /// <summary>Starting Resources.</summary>
    StartingResources,

    /// <summary>Map Size.</summary>
    MapSize,

    /// <summary>Map Type.</summary>
    MapType,

    /// <summary>Terrain.</summary>
    Terrain,

    /// <summary>Map Resources.</summary>
    MapResources,

    /// <summary>Guarded Town (a check box).</summary>
    GuardedTown,

    /// <summary>Animals (a check box).</summary>
    Animals,

    /// <summary>Magic Items (a check box).</summary>
    MagicItems,

    /// <summary>Cliffs (a check box).</summary>
    Cliffs,

    /// <summary>Gateways (a check box).</summary>
    Gateways,

    /// <summary>No Fog (a check box).</summary>
    NoFog,

    /// <summary>Explore (a check box).</summary>
    Explore,
}

/// <summary>What a kingdom's row of the random map screen changes.</summary>
public enum RandomMapRowField
{
    /// <summary>The civilization: RANDOM, then the ruleset's playable ones (the original's <c>race_N</c>).</summary>
    Civilization,

    /// <summary>A computer's AI script (the small button by the name, <c>D_CHOOSEAI</c>; AI-001).</summary>
    Script,

    /// <summary>The team (0 none, 1-4: the original's <c>TEAMS.BMP</c>).</summary>
    Team,

    /// <summary>The colour (each kingdom its own).</summary>
    Colour,
}

/// <summary>One kingdom of the random map screen.</summary>
/// <param name="Slot">Its slot; slot 0 is the player.</param>
/// <param name="Civilization">0 RANDOM, else 1 + the index in the playable civilizations.</param>
/// <param name="Script">The index of its AI script (computers only).</param>
/// <param name="Team">0 none, 1-4.</param>
/// <param name="Colour">Its colour 0-7.</param>
public sealed record RandomMapRow(byte Slot, int Civilization, int Script, byte Team, byte Colour);

/// <summary>
/// The random map screen (<c>S_SINGLERND</c>; SHELL-010, SHELL-011, SHELL-012, AI-001): for each kingdom its civilization,
/// AI script, team and colour; the number of players, game type, victory condition and its limit, population limit,
/// difficulty and starting resources; the map's size, type, terrain set, resources and its five options (guarded town,
/// animals, magic items, cliffs, gateways); No Fog and Explore. What the screen set is kept for the next time
/// (<see cref="ToBytes"/>). Steps and defaults follow the original as ReTzar reads its <c>tzar.ini [SINGLE_RANDOM]</c>
/// handling: time limits 30-180 minutes by 30, score limits 200-2,000 by 200, Normal resources and difficulty, the map
/// options on except cliffs.
/// </summary>
public sealed class RandomMapScreenModel
{
    /// <summary>The most kingdoms (the original's eight colours).</summary>
    public const int MaxPlayers = 8;

    /// <summary>The most teams (<c>TEAMS.BMP</c> has 1-4 and none).</summary>
    public const int MaxTeams = 4;

    /// <summary>The time limit step and smallest limit, in minutes.</summary>
    public const int TimeStep = 30;

    /// <summary>The longest time limit, in minutes.</summary>
    public const int TimeMax = 180;

    /// <summary>The score limit step and smallest limit.</summary>
    public const int ScoreStep = 200;

    /// <summary>The highest score limit.</summary>
    public const int ScoreMax = 2000;

    private const byte FormatVersion = 1;

    private static readonly RandomMapType[] Types = Enum.GetValues<RandomMapType>();
    private static readonly RandomMapSize[] Sizes = Enum.GetValues<RandomMapSize>();
    private static readonly MapTerrainSet[] Terrains = Enum.GetValues<MapTerrainSet>();
    private static readonly VictoryCondition[] Victories = [VictoryCondition.Standard, VictoryCondition.DestroyCastles, VictoryCondition.TimeLimit, VictoryCondition.ScoreLimit];

    private readonly RandomMapRow[] _rows = new RandomMapRow[MaxPlayers];

    /// <summary>
    /// Creates the screen for the playable <paramref name="civilizations"/> (keys) and the AI <paramref name="scripts"/>,
    /// from what it kept last time (<paramref name="saved"/>, <see cref="ToBytes"/>) when that still reads.
    /// </summary>
    public RandomMapScreenModel(IReadOnlyList<string> civilizations, IReadOnlyList<AiScriptOption> scripts, ReadOnlySpan<byte> saved = default)
    {
        ArgumentNullException.ThrowIfNull(civilizations);
        ArgumentNullException.ThrowIfNull(scripts);
        Civilizations = civilizations.Count > 0 ? civilizations : ["euro", "arab", "asia"];
        Scripts = scripts.Count > 0 ? scripts : [new AiScriptOption("Default", string.Empty)];
        Settings = new MatchSettings
        {
            Map = MapSourceKind.RandomMap,
            Features = MapFeatures.Default | MapFeatures.GuardedTowns | MapFeatures.MagicItems | MapFeatures.Gateways,
            StartingResources = (int)MapResourceLevel.Normal,
            PopulationLimit = 200,
            TimeLimitMinutes = TimeStep,
            ScoreLimit = ScoreStep,
            AiAssist = true, // the kingdom's card offers AI assistance (ADR 0183)
        };
        for (int i = 0; i < MaxPlayers; i++)
        {
            _rows[i] = new RandomMapRow((byte)i, i == 0 ? 1 : 0, 0, 0, (byte)i);
        }

        if (!saved.IsEmpty)
        {
            Restore(saved);
        }

        Players = Math.Clamp(Players, 2, MostPlayers);
    }

    /// <summary>The playable civilizations (keys).</summary>
    public IReadOnlyList<string> Civilizations { get; }

    /// <summary>The AI scripts.</summary>
    public IReadOnlyList<AiScriptOption> Scripts { get; }

    /// <summary>The match settings the screen holds.</summary>
    public MatchSettings Settings { get; private set; }

    /// <summary>The computers' difficulty.</summary>
    public Session.Difficulty Difficulty { get; private set; } = Session.Difficulty.Normal;

    /// <summary>The kingdoms playing (2 up to what the map size holds).</summary>
    public int Players { get; private set; } = 2;

    /// <summary>The most kingdoms the map size holds.</summary>
    public int MostPlayers => Math.Min(MaxPlayers, MapGenerator.MaxPlayersFor(Settings.MapSize));

    /// <summary>The rows of the kingdoms that play.</summary>
    public IReadOnlyList<RandomMapRow> Rows => _rows[..Players];

    /// <summary>Whether the victory condition has a limit to set (time or score).</summary>
    public bool HasVictoryLimit => Settings.Victory is VictoryCondition.TimeLimit or VictoryCondition.ScoreLimit;

    /// <summary>The victory condition's limit: minutes or score.</summary>
    public int VictoryLimit => Settings.Victory == VictoryCondition.ScoreLimit ? Settings.ScoreLimit : Settings.TimeLimitMinutes;

    /// <summary>Whether check box <paramref name="option"/> is on (false for an option that is no check box).</summary>
    public bool IsOn(RandomMapOption option) => option switch
    {
        RandomMapOption.GuardedTown => Settings.Features.HasFlag(MapFeatures.GuardedTowns),
        RandomMapOption.Animals => Settings.Features.HasFlag(MapFeatures.Animals),
        RandomMapOption.MagicItems => Settings.Features.HasFlag(MapFeatures.MagicItems),
        RandomMapOption.Cliffs => Settings.Features.HasFlag(MapFeatures.Cliffs),
        RandomMapOption.Gateways => Settings.Features.HasFlag(MapFeatures.Gateways),
        RandomMapOption.NoFog => Settings.NoFog,
        RandomMapOption.Explore => Settings.Explored,
        _ => false,
    };

    /// <summary>The civilization key of <paramref name="row"/>, or null for RANDOM.</summary>
    public string? CivilizationOf(RandomMapRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Civilization == 0 ? null : Civilizations[row.Civilization - 1];
    }

    /// <summary>Changes <paramref name="option"/> by one step (a check box toggles).</summary>
    public void Change(RandomMapOption option, int step)
    {
        MatchSettings s = Settings;
        switch (option)
        {
            case RandomMapOption.Players:
                Players = 2 + Wrap(Players - 2 + step, MostPlayers - 1);
                return;
            case RandomMapOption.Difficulty:
                Difficulty = (Session.Difficulty)Wrap((int)Difficulty + step, 3);
                return;
        }

        Settings = option switch
        {
            RandomMapOption.GameType => s with { GameType = s.GameType == GameType.Standard ? GameType.Deathmatch : GameType.Standard },
            RandomMapOption.Victory => s with { Victory = Cycle(Victories, s.Victory, step), TimeLimitMinutes = TimeStep, ScoreLimit = ScoreStep },
            RandomMapOption.VictoryLimit when s.Victory == VictoryCondition.ScoreLimit => s with { ScoreLimit = ScoreStep * (1 + Wrap((s.ScoreLimit / ScoreStep) - 1 + step, ScoreMax / ScoreStep)) },
            RandomMapOption.VictoryLimit when s.Victory == VictoryCondition.TimeLimit => s with { TimeLimitMinutes = TimeStep * (1 + Wrap((s.TimeLimitMinutes / TimeStep) - 1 + step, TimeMax / TimeStep)) },
            RandomMapOption.PopulationLimit => s with { PopulationLimit = Cycle(LobbyScreenModel.PopulationLimits, s.PopulationLimit, step) },
            RandomMapOption.StartingResources => s with { StartingResources = Wrap(s.StartingResources + step, 3) },
            RandomMapOption.MapSize => s with { MapSize = Cycle(Sizes, s.MapSize, step) },
            RandomMapOption.MapType => s with { MapType = Cycle(Types, s.MapType, step) },
            RandomMapOption.Terrain => s with { Terrain = Cycle(Terrains, s.Terrain, step) },
            RandomMapOption.MapResources => s with { Resources = (MapResourceLevel)Wrap((int)s.Resources + step, 3) },
            RandomMapOption.GuardedTown => s with { Features = s.Features ^ MapFeatures.GuardedTowns },
            RandomMapOption.Animals => s with { Features = s.Features ^ MapFeatures.Animals },
            RandomMapOption.MagicItems => s with { Features = s.Features ^ MapFeatures.MagicItems },
            RandomMapOption.Cliffs => s with { Features = s.Features ^ MapFeatures.Cliffs },
            RandomMapOption.Gateways => s with { Features = s.Features ^ MapFeatures.Gateways },
            RandomMapOption.NoFog => s with { NoFog = !s.NoFog },
            RandomMapOption.Explore => s with { Explored = !s.Explored },
            _ => s,
        };
        Players = Math.Clamp(Players, 2, MostPlayers); // a smaller map holds fewer kingdoms
    }

    /// <summary>Changes <paramref name="field"/> of the kingdom in row <paramref name="row"/> by one step.</summary>
    public void ChangeRow(int row, RandomMapRowField field, int step)
    {
        if ((uint)row >= (uint)Players)
        {
            return;
        }

        RandomMapRow r = _rows[row];
        switch (field)
        {
            case RandomMapRowField.Civilization:
                _rows[row] = r with { Civilization = Wrap(r.Civilization + step, Civilizations.Count + 1) };
                break;
            case RandomMapRowField.Script when row > 0:
                _rows[row] = r with { Script = Wrap(r.Script + step, Scripts.Count) };
                break;
            case RandomMapRowField.Team:
                _rows[row] = r with { Team = (byte)Wrap(r.Team + step, MaxTeams + 1) };
                break;
            case RandomMapRowField.Colour:
                // The next colour no other playing kingdom has.
                var taken = _rows[..Players].Where(x => x.Slot != row).Select(x => (int)x.Colour).ToHashSet();
                int c = r.Colour;
                for (int k = 0; k < MaxPlayers; k++)
                {
                    c = Wrap(c + step, MaxPlayers);
                    if (!taken.Contains(c))
                    {
                        _rows[row] = r with { Colour = (byte)c };
                        break;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// The match these choices describe with <paramref name="seed"/>: kingdom 0 is the human, the others computers at the
    /// difficulty with their scripts; a RANDOM civilization is drawn from the seed. When rows share a colour (a smaller
    /// count after a change) every kingdom takes its slot's.
    /// </summary>
    public MatchDescriptor Describe(ulong seed, string playerName = "Player")
    {
        ArgumentNullException.ThrowIfNull(playerName);
        bool distinct = _rows[..Players].Select(r => r.Colour).Distinct().Count() == Players;
        var players = ImmutableArray.CreateBuilder<MatchPlayer>(Players);
        for (int i = 0; i < Players; i++)
        {
            RandomMapRow r = _rows[i];
            int civ = r.Civilization > 0 ? r.Civilization - 1 : (int)(Mix(seed + (ulong)i) % (ulong)Civilizations.Count);
            bool human = i == 0;
            players.Add(new MatchPlayer((byte)i, human ? playerName : string.Empty, Civilizations[civ], human ? ControllerKind.Human : ControllerKind.Computer,
                Difficulty, r.Team, human ? string.Empty : Scripts[r.Script].Script, distinct ? r.Colour : (byte)255));
        }

        return new MatchDescriptor(Settings with { Seed = seed }, players.MoveToImmutable());
    }

    /// <summary>What the screen set, kept for the next time (SHELL-012).</summary>
    public byte[] ToBytes()
    {
        byte[] settings = Settings.ToBytes();
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            w.Write(FormatVersion);
            w.Write((byte)Players);
            w.Write((byte)Difficulty);
            foreach (RandomMapRow r in _rows)
            {
                w.Write((byte)r.Civilization);
                w.Write(Scripts[r.Script].Script);
                w.Write(r.Team);
                w.Write(r.Colour);
            }

            w.Write(settings.Length);
            w.Write(settings);
        }

        return stream.ToArray();
    }

    private void Restore(ReadOnlySpan<byte> saved)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(saved.ToArray()));
            if (r.ReadByte() != FormatVersion)
            {
                return;
            }

            int players = r.ReadByte();
            int difficulty = r.ReadByte();
            var rows = new RandomMapRow[MaxPlayers];
            for (int i = 0; i < MaxPlayers; i++)
            {
                int civ = r.ReadByte();
                string script = r.ReadString();
                byte team = r.ReadByte();
                byte colour = r.ReadByte();
                int s = Scripts.Select(o => o.Script).ToList().IndexOf(script);
                rows[i] = new RandomMapRow((byte)i, civ <= Civilizations.Count ? civ : 0, Math.Max(0, s), team <= MaxTeams ? team : (byte)0, colour < MaxPlayers ? colour : (byte)i);
            }

            MatchSettings settings = MatchSettings.Parse(r.ReadBytes(r.ReadInt32()));
            if (settings.Map != MapSourceKind.RandomMap || difficulty > (int)Session.Difficulty.Hard)
            {
                return;
            }

            rows.CopyTo(_rows, 0);
            Settings = settings;
            Difficulty = (Session.Difficulty)difficulty;
            Players = players;
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException or ArgumentOutOfRangeException)
        {
            // An older or broken file: the defaults.
        }
    }

    private static T Cycle<T>(IReadOnlyList<T> values, T value, int step)
    {
        int i = 0;
        while (i < values.Count && !EqualityComparer<T>.Default.Equals(values[i], value))
        {
            i++;
        }

        return values[Wrap((i == values.Count ? 0 : i) + step, values.Count)];
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
