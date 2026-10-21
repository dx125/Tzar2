using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Tzar2.Content.MapGen;
using Tzar2.Core.Serialization;
using Tzar2.Game.Maps;
using Tzar2.Sim;

namespace Tzar2.Game.Session;

/// <summary>Which map source a <see cref="MatchSettings"/> names.</summary>
public enum MapSourceKind : byte
{
    /// <summary>The generated skirmish test map (<see cref="SkirmishTestMap"/>).</summary>
    TestMap = 1,

    /// <summary>A random map (<see cref="RandomMapSource"/>).</summary>
    RandomMap = 2,
}

/// <summary>
/// The host's match settings of a multiplayer lobby (the relay's opaque settings blob; ADR 0162) and the shared part of
/// a <see cref="MatchDescriptor"/>: ruleset, map source and every lobby option of REQ-NET-08 that reaches the sim.
/// Encoded in a versioned binary form; decoding is bounded (it comes from other players).
/// </summary>
public sealed record MatchSettings
{
    /// <summary>Format version.</summary>
    public const byte Version = 1;

    /// <summary>The ruleset name.</summary>
    public string Ruleset { get; init; } = "classic";

    /// <summary>The map source.</summary>
    public MapSourceKind Map { get; init; } = MapSourceKind.RandomMap;

    /// <summary>Test map side in tiles.</summary>
    public int TestMapSize { get; init; } = 96;

    /// <summary>Random map type.</summary>
    public RandomMapType MapType { get; init; } = RandomMapType.Continental;

    /// <summary>Random map terrain set.</summary>
    public MapTerrainSet Terrain { get; init; } = MapTerrainSet.Grass;

    /// <summary>Random map size.</summary>
    public RandomMapSize MapSize { get; init; } = RandomMapSize.Small;

    /// <summary>Random map resources.</summary>
    public MapResourceLevel Resources { get; init; } = MapResourceLevel.Normal;

    /// <summary>Random map features.</summary>
    public MapFeatures Features { get; init; } = MapFeatures.Default;

    /// <summary>The match seed.</summary>
    public ulong Seed { get; init; } = 0x7A42_0006UL;

    /// <summary>Starting-resource level, or -1 for the ruleset default.</summary>
    public int StartingResources { get; init; } = -1;

    /// <summary>Population limit, or -1 for the ruleset default.</summary>
    public int PopulationLimit { get; init; } = -1;

    /// <summary>Victory condition.</summary>
    public VictoryCondition Victory { get; init; } = VictoryCondition.Standard;

    /// <summary>Game type.</summary>
    public GameType GameType { get; init; } = GameType.Standard;

    /// <summary>Score limit.</summary>
    public int ScoreLimit { get; init; } = 10_000;

    /// <summary>Time limit in minutes.</summary>
    public int TimeLimitMinutes { get; init; } = 60;

    /// <summary>No Fog.</summary>
    public bool NoFog { get; init; }

    /// <summary>Explore.</summary>
    public bool Explored { get; init; }

    /// <summary>Day/night.</summary>
    public bool DayNight { get; init; } = true;

    /// <summary>Weather.</summary>
    public bool Weather { get; init; } = true;

    /// <summary>Cheats allowed (GAME-19: only when the host allows it, never in a ranked match).</summary>
    public bool CheatsAllowed { get; init; }

    /// <summary>A ranked match (cheats are never allowed).</summary>
    public bool Ranked { get; init; }

    /// <summary>
    /// Resume a saved multiplayer match (REQ-NET-10; ADR 0166): the content hash of the save's snapshot, 0 for a new
    /// match. Every peer must have that save.
    /// </summary>
    public ulong ResumeSnapshotHash { get; init; }

    /// <summary>Game speed in percent of the classic tick rate (the relay's tick length).</summary>
    public int SpeedPercent { get; init; } = 100;

    /// <summary>AI assistance for the humans (the lobby's "AI Assist"; ADR 0183).</summary>
    public bool AiAssist { get; init; }

    /// <summary>The map source these settings describe.</summary>
    public IMapSource MapSource() => Map == MapSourceKind.TestMap
        ? new SkirmishTestMap(TestMapSize)
        : new RandomMapSource(MapType, Terrain, MapSize, Resources, Features);

    /// <summary>The encoded settings.</summary>
    public byte[] ToBytes()
    {
        var w = new BinaryBufferWriter(128);
        Write(w);
        return w.ToArray();
    }

    internal void Write(BinaryBufferWriter w)
    {
        w.WriteByte(Version);
        w.WriteString(Ruleset);
        w.WriteByte((byte)Map);
        w.WriteVarUInt64((ulong)TestMapSize);
        w.WriteByte((byte)MapType);
        w.WriteByte((byte)Terrain);
        w.WriteByte((byte)MapSize);
        w.WriteByte((byte)Resources);
        w.WriteUInt16((ushort)Features);
        w.WriteUInt64(Seed);
        w.WriteVarInt64(StartingResources);
        w.WriteVarInt64(PopulationLimit);
        w.WriteByte((byte)Victory);
        w.WriteByte((byte)GameType);
        w.WriteVarInt64(ScoreLimit);
        w.WriteVarInt64(TimeLimitMinutes);
        w.WriteByte((byte)((NoFog ? 1 : 0) | (Explored ? 2 : 0) | (DayNight ? 4 : 0) | (Weather ? 8 : 0) | (CheatsAllowed ? 16 : 0) | (Ranked ? 32 : 0) | (AiAssist ? 64 : 0)));
        w.WriteUInt64(ResumeSnapshotHash);
        w.WriteVarInt64(SpeedPercent);
    }

    /// <summary>Decodes settings.</summary>
    /// <exception cref="InvalidDataException">Malformed, another version, or a value out of range.</exception>
    public static MatchSettings Parse(ReadOnlySpan<byte> data)
    {
        var r = new BinaryBufferReader(data);
        MatchSettings settings = Read(ref r);
        return r.IsAtEnd ? settings : throw new InvalidDataException("Trailing bytes after the match settings.");
    }

    internal static MatchSettings Read(ref BinaryBufferReader r)
    {
        if (r.ReadByte() != Version)
        {
            throw new InvalidDataException("Unsupported match settings version.");
        }

        string ruleset = r.ReadString(64);
        var map = (MapSourceKind)r.ReadByte();
        int testSize = (int)Bounded(r.ReadVarUInt64(), 48, 512);
        var type = (RandomMapType)r.ReadByte();
        var terrain = (MapTerrainSet)r.ReadByte();
        var size = (RandomMapSize)r.ReadByte();
        var resources = (MapResourceLevel)r.ReadByte();
        var features = (MapFeatures)r.ReadUInt16();
        ulong seed = r.ReadUInt64();
        int starting = (int)Bounded(r.ReadVarInt64(), -1, 15);
        int population = (int)Bounded(r.ReadVarInt64(), -1, 100_000);
        var victory = (VictoryCondition)r.ReadByte();
        var gameType = (GameType)r.ReadByte();
        int score = (int)Bounded(r.ReadVarInt64(), 1, 100_000_000);
        int time = (int)Bounded(r.ReadVarInt64(), 1, 10_000);
        byte flags = r.ReadByte();
        ulong resume = r.ReadUInt64();
        int speed = (int)Bounded(r.ReadVarInt64(), 25, 400);
        if (map is not (MapSourceKind.TestMap or MapSourceKind.RandomMap) || type > RandomMapType.Dry || terrain > MapTerrainSet.Winter
            || size > RandomMapSize.RealWorld || resources > MapResourceLevel.High || victory > VictoryCondition.NotSpecified
            || gameType > GameType.Deathmatch || flags > 127 || ruleset.Length == 0)
        {
            throw new InvalidDataException("Match settings out of range.");
        }

        bool cheats = (flags & 16) != 0;
        bool ranked = (flags & 32) != 0;
        return new MatchSettings
        {
            Ruleset = ruleset,
            Map = map,
            TestMapSize = testSize,
            MapType = type,
            Terrain = terrain,
            MapSize = size,
            Resources = resources,
            Features = features,
            Seed = seed,
            StartingResources = starting,
            PopulationLimit = population,
            Victory = victory,
            GameType = gameType,
            ScoreLimit = score,
            TimeLimitMinutes = time,
            NoFog = (flags & 1) != 0,
            Explored = (flags & 2) != 0,
            DayNight = (flags & 4) != 0,
            Weather = (flags & 8) != 0,
            CheatsAllowed = cheats && !ranked, // GAME-19: never in a ranked match
            Ranked = ranked,
            ResumeSnapshotHash = resume,
            SpeedPercent = speed,
            AiAssist = (flags & 64) != 0,
        };
    }

    internal static long Bounded(long value, long min, long max) =>
        value >= min && value <= max ? value : throw new InvalidDataException($"Value {value} outside [{min}, {max}].");

    internal static ulong Bounded(ulong value, ulong min, ulong max) =>
        value >= min && value <= max ? value : throw new InvalidDataException($"Value {value} outside [{min}, {max}].");
}

/// <summary>A lobby slot's own choices (the relay's per-slot choice blob): civilization, team and, for computers, difficulty.</summary>
/// <param name="Civilization">Civilization key of the ruleset.</param>
/// <param name="Team">Team 0 (none) to 8.</param>
/// <param name="Difficulty">Computer difficulty.</param>
public sealed record SlotChoice(string Civilization, byte Team, Difficulty Difficulty)
{
    /// <summary>The default choice for a fresh slot.</summary>
    public static SlotChoice Default { get; } = new("euro", 0, Difficulty.Normal);

    /// <summary>The encoded choice.</summary>
    public byte[] ToBytes()
    {
        var w = new BinaryBufferWriter(24);
        w.WriteString(Civilization.Length <= 16 ? Civilization : Civilization[..16]);
        w.WriteByte(Team);
        w.WriteByte((byte)Difficulty);
        return w.ToArray();
    }

    /// <summary>Decodes a choice; an empty or malformed blob gives <see cref="Default"/> (choices only describe, a bad one is not fatal).</summary>
    public static SlotChoice Parse(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return Default;
        }

        try
        {
            var r = new BinaryBufferReader(data);
            string civ = r.ReadString(16);
            byte team = r.ReadByte();
            byte difficulty = r.ReadByte();
            return team <= 8 && difficulty <= (byte)Difficulty.Hard && civ.Length > 0 && r.IsAtEnd
                ? new SlotChoice(civ, team, (Difficulty)difficulty)
                : Default;
        }
        catch (InvalidDataException)
        {
            return Default;
        }
    }
}

/// <summary>One kingdom of a <see cref="MatchDescriptor"/>.</summary>
public sealed record MatchPlayer(byte Slot, string Name, string Civilization, ControllerKind Controller, Difficulty Difficulty, byte Team);

/// <summary>
/// Everything needed to rebuild a match's <see cref="MatchSetup"/> on another machine (ADR 0162, 0164): the settings
/// and the kingdoms. It is the replay header's setup description (<c>t2 replay verify</c>), part of multiplayer saves and
/// what a desync report rebuilds its snapshots with. Only matches on a test or random map can be described.
/// </summary>
public sealed record MatchDescriptor(MatchSettings Settings, ImmutableArray<MatchPlayer> Players)
{
    /// <summary>The encoded description.</summary>
    public byte[] ToBytes()
    {
        var w = new BinaryBufferWriter(256);
        Settings.Write(w);
        w.WriteByte((byte)Players.Length);
        foreach (MatchPlayer p in Players)
        {
            w.WriteByte(p.Slot);
            w.WriteString(p.Name.Length <= 32 ? p.Name : p.Name[..32]);
            w.WriteString(p.Civilization);
            w.WriteByte((byte)p.Controller);
            w.WriteByte((byte)p.Difficulty);
            w.WriteByte(p.Team);
        }

        return w.ToArray();
    }

    /// <summary>Decodes a description.</summary>
    /// <exception cref="InvalidDataException">Malformed.</exception>
    public static MatchDescriptor Parse(ReadOnlySpan<byte> data)
    {
        var r = new BinaryBufferReader(data);
        MatchSettings settings = MatchSettings.Read(ref r);
        int count = r.ReadByte();
        if (count is < 1 or > 16)
        {
            throw new InvalidDataException("A match has 1 to 16 kingdoms.");
        }

        var players = ImmutableArray.CreateBuilder<MatchPlayer>(count);
        for (int i = 0; i < count; i++)
        {
            byte slot = r.ReadByte();
            string name = r.ReadString(32 * 4);
            string civ = r.ReadString(16 * 4);
            byte controller = r.ReadByte();
            byte difficulty = r.ReadByte();
            byte team = r.ReadByte();
            if (slot != i || controller > (byte)ControllerKind.Passive || difficulty > (byte)Difficulty.Hard || team > 8)
            {
                throw new InvalidDataException("Invalid kingdom in the match description.");
            }

            players.Add(new MatchPlayer(slot, name, civ, (ControllerKind)controller, (Difficulty)difficulty, team));
        }

        return r.IsAtEnd ? new MatchDescriptor(settings, players.MoveToImmutable()) : throw new InvalidDataException("Trailing bytes.");
    }

    /// <summary>The teams of the kingdoms, 4 bits per slot (<see cref="SimulationConfig.Teams"/>).</summary>
    public ulong Teams => Players.Aggregate(0UL, (teams, p) => teams | ((ulong)p.Team << (p.Slot * 4)));

    /// <summary>
    /// The setup of this match. <paramref name="localSlot"/> is the slot this machine plays (null for a replay or a
    /// headless check); with <paramref name="multiplayer"/> every human kingdom gets a standby AI (REQ-NET-07).
    /// </summary>
    public MatchSetup ToSetup(SkirmishAi ai, bool multiplayer, byte? localSlot)
    {
        ArgumentNullException.ThrowIfNull(ai);
        MatchSettings s = Settings;
        return new MatchSetup
        {
            Ruleset = s.Ruleset,
            Map = s.MapSource(),
            Players = [.. Players.Select(p => new PlayerSetup(p.Slot, p.Civilization, p.Controller, p.Difficulty))],
            Seed = s.Seed,
            StartingResources = s.StartingResources,
            PopulationLimit = s.PopulationLimit,
            Victory = s.Victory,
            GameType = s.GameType,
            ScoreLimit = s.ScoreLimit,
            TimeLimitMinutes = s.TimeLimitMinutes,
            NoFog = s.NoFog,
            Explored = s.Explored,
            DayNight = s.DayNight,
            Weather = s.Weather,
            Teams = Teams,
            CheatsAllowed = s.CheatsAllowed && !s.Ranked,
            Ranked = s.Ranked,
            AiAssist = s.AiAssist,
            Ai = ai,
            Multiplayer = multiplayer,
            LocalSlot = localSlot,
            Descriptor = this,
        };
    }

    /// <summary>The description of <paramref name="setup"/>, or null when its map cannot be described (scenario or campaign maps).</summary>
    public static MatchDescriptor? Of(MatchSetup setup, Func<byte, string>? names = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (setup.Descriptor is { } known)
        {
            return known;
        }

        MatchSettings? settings = setup.Map switch
        {
            SkirmishTestMap t when t.StartPeasants == 6 && t.StartFootmen == 2 && t.Lakes => new MatchSettings { Map = MapSourceKind.TestMap, TestMapSize = t.Size },
            RandomMapSource m => new MatchSettings { Map = MapSourceKind.RandomMap, MapType = m.Type, Terrain = m.Set, MapSize = m.Size, Resources = m.Resources, Features = m.Features },
            _ => null,
        };
        if (settings is null || setup.Scenario is not null || setup.Preparation is not null || setup.TriggerOptions is not null || !setup.ComputerAi)
        {
            return null;
        }

        settings = settings with
        {
            Ruleset = setup.Ruleset,
            Seed = setup.Seed,
            StartingResources = setup.StartingResources,
            PopulationLimit = setup.PopulationLimit,
            Victory = setup.Victory,
            GameType = setup.GameType,
            ScoreLimit = setup.ScoreLimit,
            TimeLimitMinutes = setup.TimeLimitMinutes,
            NoFog = setup.NoFog,
            Explored = setup.Explored,
            DayNight = setup.DayNight,
            Weather = setup.Weather,
            CheatsAllowed = setup.CheatsAllowed,
            Ranked = setup.Ranked,
            AiAssist = setup.AiAssist,
        };
        return new MatchDescriptor(settings, [.. setup.Players.Select(p => new MatchPlayer(
            p.Slot, names?.Invoke(p.Slot) ?? string.Empty, p.Civilization, p.Controller, p.Difficulty, (byte)((setup.Teams >> (p.Slot * 4)) & 0xF)))]);
    }
}
