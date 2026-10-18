using System;
using System.IO;
using Tzar2.Content.Compiled;
using Tzar2.Core.Serialization;
using Tzar2.Game.Session;

namespace Tzar2.Game.Campaigns;

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
    /// <summary>Format version.</summary>
    public const int Version = 1;

    /// <summary>Largest save accepted.</summary>
    public const int MaxBytes = 256 << 20;

    private const uint Magic = 0x56533254; // "T2SV"

    /// <summary>Saves a mission between two ticks (the sim thread, if running, takes the snapshot).</summary>
    public static SaveGame Capture(GameSession session, string campaignId, string mapId, CampaignProgress? progress, string label)
    {
        ArgumentNullException.ThrowIfNull(session);
        byte[] snapshot = session.CaptureSnapshotNow();
        return new SaveGame(campaignId, mapId, label, session.Simulation.World.Tick.Value, progress, snapshot);
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

        return new SaveGame(campaign, map, label, (int)tick, progress, snapshot);
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
