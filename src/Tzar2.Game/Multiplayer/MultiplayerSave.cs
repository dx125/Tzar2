using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tzar2.Core.Hashing;
using Tzar2.Core.Serialization;
using Tzar2.Game.Session;

namespace Tzar2.Game.Multiplayer;

/// <summary>
/// A multiplayer save (<c>.t2msav</c>, REQ-NET-10; ADR 0166): written by every peer after the same closed tick once
/// every connected player consented, so all copies hold the same snapshot. It carries the match description (map,
/// settings, kingdoms with names) so the host can open a lobby that resumes it with the same slots; a peer joins the
/// resumed match only with a save of the same snapshot hash. Reading is bounded (saves are user files).
/// </summary>
/// <param name="Label">A label (e.g. the time it was saved).</param>
/// <param name="MatchId">The relay's id of the match it came from.</param>
/// <param name="Descriptor">The match description (the kingdoms as they were when saved).</param>
/// <param name="Tick">The sim tick of the snapshot (the state before this tick).</param>
/// <param name="Snapshot">The sim snapshot.</param>
public sealed record MultiplayerSave(string Label, ulong MatchId, MatchDescriptor Descriptor, int Tick, byte[] Snapshot)
{
    /// <summary>The file extension.</summary>
    public const string Extension = ".t2msav";

    /// <summary>Format version.</summary>
    public const int Version = 1;

    /// <summary>Largest save accepted.</summary>
    public const int MaxBytes = 256 << 20;

    private const uint Magic = 0x534D3254; // "T2MS"

    /// <summary>The content hash of the snapshot (identifies the save across peers).</summary>
    public ulong SnapshotHash => StateHasher.Hash(Snapshot);

    /// <summary>The save as bytes.</summary>
    public byte[] ToBytes()
    {
        byte[] descriptor = Descriptor.ToBytes();
        var w = new BinaryBufferWriter(Snapshot.Length + descriptor.Length + 128);
        w.WriteUInt32(Magic);
        w.WriteVarUInt64(Version);
        w.WriteString(Label.Length <= 64 ? Label : Label[..64]);
        w.WriteUInt64(MatchId);
        w.WriteVarUInt64((ulong)descriptor.Length);
        w.WriteBytes(descriptor);
        w.WriteVarInt64(Tick);
        w.WriteVarUInt64((ulong)Snapshot.Length);
        w.WriteBytes(Snapshot);
        return w.ToArray();
    }

    /// <summary>Reads a save.</summary>
    /// <exception cref="InvalidDataException">Not a multiplayer save of a supported version, or malformed.</exception>
    public static MultiplayerSave Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes)
        {
            throw new InvalidDataException("The save is too large.");
        }

        var r = new BinaryBufferReader(data);
        if (r.ReadUInt32() != Magic || r.ReadVarUInt64() != Version)
        {
            throw new InvalidDataException("Not a Tzar2 multiplayer save of a supported version.");
        }

        string label = r.ReadString(256);
        ulong match = r.ReadUInt64();
        MatchDescriptor descriptor = MatchDescriptor.Parse(r.ReadBytes(Length(ref r, 1 << 16)));
        long tick = r.ReadVarInt64();
        byte[] snapshot = r.ReadBytes(Length(ref r, MaxBytes)).ToArray();
        if (tick is < 0 or > int.MaxValue || !r.IsAtEnd)
        {
            throw new InvalidDataException("Invalid multiplayer save.");
        }

        return new MultiplayerSave(label, match, descriptor, (int)tick, snapshot);
    }

    /// <summary>The file name for this save (relay-generated fields only).</summary>
    public string FileName => string.Create(CultureInfo.InvariantCulture, $"mp-{MatchId:X16}-t{Tick}{Extension}");

    /// <summary>Writes the save into <paramref name="directory"/> and returns its path.</summary>
    public string Write(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        File.WriteAllBytes(path, ToBytes());
        return path;
    }

    /// <summary>The readable saves in <paramref name="directory"/>, newest first (unreadable files are skipped).</summary>
    public static IReadOnlyList<(string Path, MultiplayerSave Save)> List(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var saves = new List<(string, MultiplayerSave, DateTime)>();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*" + Extension))
        {
            try
            {
                if (new FileInfo(path).Length <= MaxBytes)
                {
                    saves.Add((path, Parse(File.ReadAllBytes(path)), File.GetLastWriteTimeUtc(path)));
                }
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Not a save of ours, or damaged.
            }
        }

        saves.Sort((a, b) => b.Item3.CompareTo(a.Item3));
        return saves.ConvertAll(s => (s.Item1, s.Item2));
    }

    /// <summary>The save in <paramref name="directory"/> whose snapshot hash is <paramref name="hash"/>, or null.</summary>
    public static MultiplayerSave? Find(string? directory, ulong hash)
    {
        if (directory is null)
        {
            return null;
        }

        foreach ((string _, MultiplayerSave save) in List(directory))
        {
            if (save.SnapshotHash == hash)
            {
                return save;
            }
        }

        return null;
    }

    private static int Length(ref BinaryBufferReader r, int max)
    {
        ulong n = r.ReadVarUInt64();
        return n <= (ulong)max && n <= (ulong)r.Remaining ? (int)n : throw new InvalidDataException("Length out of range.");
    }
}
