using System;
using System.IO;
using Tzar2.Content.Compiled;
using Tzar2.Core.Serialization;
using Tzar2.Game.Session;

namespace Tzar2.Game.Campaigns;

/// <summary>What a save game continues (SHELL-014: the load screen lists single player games and campaigns).</summary>
public enum SaveKind : byte
{
    /// <summary>A campaign mission (or a single scenario of a campaign source).</summary>
    Campaign = 0,

    /// <summary>One of the package's maps played from CUSTOM MAP, with its setup.</summary>
    CustomMap = 1,

    /// <summary>A random map or the test map (the match description rebuilds it).</summary>
    Skirmish = 2,
}

/// <summary>
/// A mission save game (<c>.t2sav</c>, TDD §19.5 REQ-NET-10; ADR 0133): which campaign map it is, the campaign progress
/// at the time (so loading puts the player back into the campaign), a label, and the sim snapshot (REQ-SIM-13, which
/// holds the trigger state). Versioned binary; reading is bounded and validated (saves are user files).
/// </summary>
/// <param name="CampaignId">The campaign, or empty for a single scenario.</param>
/// <param name="MapId">The campaign map id (or the scenario's name).</param>
/// <param name="Label">A display label (e.g. the time it was saved).</param>
/// <param name="Tick">The tick of the snapshot.</param>
/// <param name="Progress">The campaign progress when saved, or null for a single scenario.</param>
/// <param name="Snapshot">The sim snapshot.</param>
public sealed record SaveGame(string CampaignId, string MapId, string Label, int Tick, CampaignProgress? Progress, byte[] Snapshot)
{
    /// <summary>Format version (2: the kind, a custom map's setup or a skirmish's description; 1 is still read).</summary>
    public const int Version = 2;

    /// <summary>What the save continues.</summary>
    public SaveKind Kind { get; init; }

    /// <summary>A custom map's setup (<see cref="SaveKind.CustomMap"/>; its AI is the one of the client that loads it).</summary>
    public CustomMapSettings? Custom { get; init; }

    /// <summary>A skirmish's match description (<see cref="MatchDescriptor.ToBytes"/>; <see cref="SaveKind.Skirmish"/>).</summary>
    public byte[] Descriptor { get; init; } = [];

    /// <summary>Saves a match that is not a campaign's: a custom map (its name and setup) or a skirmish (its description).</summary>
    /// <exception cref="ArgumentException">A skirmish whose map cannot be described.</exception>
    public static SaveGame CaptureMatch(GameSession session, string label, string? customMap = null, CustomMapSettings? custom = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        (byte[] snapshot, int tick) = session.CaptureSnapshotNow();
        if (customMap is not null)
        {
            return new SaveGame(string.Empty, customMap, label, tick, null, snapshot) { Kind = SaveKind.CustomMap, Custom = custom ?? new CustomMapSettings() };
        }

        MatchDescriptor descriptor = MatchDescriptor.Of(session.Setup) ?? throw new ArgumentException("This match cannot be saved: its map cannot be described.", nameof(session));
        return new SaveGame(string.Empty, string.Empty, label, tick, null, snapshot) { Kind = SaveKind.Skirmish, Descriptor = descriptor.ToBytes() };
    }

    /// <summary>Largest save accepted.</summary>
    public const int MaxBytes = 256 << 20;

    private const uint Magic = 0x56533254; // "T2SV"

    /// <summary>
    /// Saves a mission between two ticks: with a running sim thread, that thread takes the snapshot at the top of its
    /// loop, never mid-tick (<see cref="GameSession.CaptureSnapshotNow"/>).
    /// </summary>
    public static SaveGame Capture(GameSession session, string campaignId, string mapId, CampaignProgress? progress, string label)
    {
        ArgumentNullException.ThrowIfNull(session);
        (byte[] snapshot, int tick) = session.CaptureSnapshotNow();
        return new SaveGame(campaignId, mapId, label, tick, progress, snapshot);
    }

    /// <summary>The save as bytes.</summary>
    public byte[] ToBytes()
    {
        var w = new BinaryBufferWriter(Snapshot.Length + 4096);
        w.WriteUInt32(Magic);
        w.WriteVarUInt64(Version);
        w.WriteString(CampaignId);
        w.WriteString(MapId);
        w.WriteString(Label);
        w.WriteVarInt64(Tick);
        w.WriteByte((byte)Kind);
        if (Kind == SaveKind.CustomMap)
        {
            CustomMapSettings c = Custom ?? new CustomMapSettings();
            w.WriteByte((byte)c.Difficulty);
            w.WriteString(c.Civilization ?? string.Empty);
            w.WriteVarInt64(c.StartingResources);
            w.WriteVarInt64(c.PopulationLimit);
            w.WriteByte(c.NoFog is { } fog ? (byte)(fog ? 2 : 1) : (byte)0);
            w.WriteByte(c.Explored is { } explored ? (byte)(explored ? 2 : 1) : (byte)0);
        }
        else if (Kind == SaveKind.Skirmish)
        {
            w.WriteVarUInt64((ulong)Descriptor.Length);
            w.WriteBytes(Descriptor);
        }

        byte[] progress = Progress is null ? [] : new PlayerProfile(string.Empty, [Progress]).ToJson();
        w.WriteVarUInt64((ulong)progress.Length);
        w.WriteBytes(progress);
        w.WriteVarUInt64((ulong)Snapshot.Length);
        w.WriteBytes(Snapshot);
        return w.ToArray();
    }

    /// <summary>Reads a save.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a valid save of a supported version.</exception>
    public static SaveGame FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes)
        {
            throw new InvalidDataException("The save is too large.");
        }

        var r = new BinaryBufferReader(data);
        if (r.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("Not a Tzar2 save game.");
        }

        ulong version = r.ReadVarUInt64();
        if (version is < 1 or > Version)
        {
            throw new InvalidDataException($"Save version {version} is not supported.");
        }

        string campaign = r.ReadString(256);
        string map = r.ReadString(256);
        string label = r.ReadString(1024);
        long tick = r.ReadVarInt64();
        if (tick < 0 || tick > int.MaxValue)
        {
            throw new InvalidDataException("Invalid save tick.");
        }

        SaveKind kind = SaveKind.Campaign;
        CustomMapSettings? custom = null;
        byte[] descriptor = [];
        if (version >= 2)
        {
            byte k = r.ReadByte();
            kind = k <= (byte)SaveKind.Skirmish ? (SaveKind)k : throw new InvalidDataException("Unknown save kind.");
            if (kind == SaveKind.CustomMap)
            {
                byte difficulty = r.ReadByte();
                string civ = r.ReadString(64);
                long resources = r.ReadVarInt64();
                long population = r.ReadVarInt64();
                byte fog = r.ReadByte(), explored = r.ReadByte();
                if (difficulty > (byte)Difficulty.Hard || resources is < -1 or > 2 || population is < -1 or > 100_000 || fog > 2 || explored > 2)
                {
                    throw new InvalidDataException("Invalid custom map setup.");
                }

                custom = new CustomMapSettings
                {
                    Difficulty = (Difficulty)difficulty,
                    Civilization = civ.Length > 0 ? civ : null,
                    StartingResources = (int)resources,
                    PopulationLimit = (int)population,
                    NoFog = fog == 0 ? null : fog == 2,
                    Explored = explored == 0 ? null : explored == 2,
                };
            }
            else if (kind == SaveKind.Skirmish)
            {
                ulong length = r.ReadVarUInt64();
                if (length > (ulong)r.Remaining || length > 1 << 16)
                {
                    throw new InvalidDataException("Truncated save.");
                }

                descriptor = r.ReadBytes((int)length).ToArray();
                _ = MatchDescriptor.Parse(descriptor); // validated now, not when the match is rebuilt
            }
        }

        ulong progressLength = r.ReadVarUInt64();
        if (progressLength > (ulong)r.Remaining)
        {
            throw new InvalidDataException("Truncated save.");
        }

        ReadOnlySpan<byte> progressBytes = r.ReadBytes((int)progressLength);
        CampaignProgress? progress = null;
        if (progressBytes.Length > 0)
        {
            PlayerProfile wrapper = PlayerProfile.FromJson(progressBytes);
            if (wrapper.Campaigns.Length != 1)
            {
                throw new InvalidDataException("A save carries the progress of one campaign.");
            }

            progress = wrapper.Campaigns[0];
        }

        ulong snapshotLength = r.ReadVarUInt64();
        if (snapshotLength > (ulong)r.Remaining)
        {
            throw new InvalidDataException("Truncated save.");
        }

        byte[] snapshot = r.ReadBytes((int)snapshotLength).ToArray();
        if (!r.IsAtEnd)
        {
            throw new InvalidDataException("Trailing bytes after the save.");
        }

        return new SaveGame(campaign, map, label, (int)tick, progress, snapshot) { Kind = kind, Custom = custom, Descriptor = descriptor };
    }

    /// <summary>A custom map's save continued on <paramref name="mission"/> (the map it names), with the loading client's AI.</summary>
    public GameSession ResumeCustomMap(MissionContent mission, Ruleset ruleset, SkirmishAi ai)
    {
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentNullException.ThrowIfNull(ai);
        if (Kind != SaveKind.CustomMap)
        {
            throw new InvalidDataException("Not a custom map save.");
        }

        return GameSession.Restore(ScenarioMatch.Setup(mission, ruleset, custom: (Custom ?? new CustomMapSettings()) with { Ai = ai }), ruleset, Snapshot);
    }

    /// <summary>A skirmish's save continued: its description rebuilds the map, the snapshot the match.</summary>
    public GameSession ResumeSkirmish(Ruleset ruleset, SkirmishAi ai)
    {
        ArgumentNullException.ThrowIfNull(ai);
        if (Kind != SaveKind.Skirmish)
        {
            throw new InvalidDataException("Not a skirmish save.");
        }

        return GameSession.Restore(MatchDescriptor.Parse(Descriptor).ToSetup(ai, multiplayer: false, localSlot: null), ruleset, Snapshot);
    }

    /// <summary>
    /// Continues the saved mission: the same match setup is rebuilt from the campaign map (the scenario, its kingdoms and
    /// the carried progress), and the session restores the snapshot, trigger state included.
    /// </summary>
    /// <exception cref="InvalidDataException">The save does not belong to this campaign or map.</exception>
    public GameSession Resume(ICampaignSource source, Ruleset ruleset, bool computerAi = true, bool cheats = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(source.Campaign.Id, CampaignId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The save belongs to campaign '{CampaignId}', not '{source.Campaign.Id}'.");
        }

        MatchSetup setup = ScenarioMatch.Setup(source.Load(MapId), ruleset, Progress, computerAi, cheats);
        return GameSession.Restore(setup, ruleset, Snapshot);
    }
}
