using System;
using System.Collections.Generic;
using System.IO;
using Tzar2.Core.Serialization;
using Tzar2.Net.Lan;
using Tzar2.Net.Relay;

namespace Tzar2.Net.Protocol;

/// <summary>
/// Entry points for fuzzing every network decoder (REQ-NET-11, TDD §22.6): each takes untrusted bytes and must either
/// decode them or throw <see cref="InvalidDataException"/>; anything else is a bug. Used by the property tests and by
/// a SharpFuzz harness.
/// </summary>
public static class FuzzTargets
{
    private static readonly Dictionary<string, Action<byte[]>> Targets = new(StringComparer.Ordinal)
    {
        ["hello"] = d => Wire.ReadHello(d),
        ["refusal"] = d => Wire.ReadRefusal(d),
        ["browse-query"] = d => Wire.ReadBrowseQuery(d),
        ["browse-reply"] = d => Wire.ReadBrowseReply(d),
        ["relay-message"] = RelayMessage,
        ["client-message"] = ClientMessage,
        ["desync-bundle"] = d => DesyncBundle.Parse(d),
        ["lan-query"] = d => LanDiscovery.TryReadQuery(d, out _),
        ["lan-answer"] = d => LanDiscovery.TryReadAnswer(d, 1, out _, out _),
    };

    /// <summary>The target names.</summary>
    public static IReadOnlyCollection<string> Names => Targets.Keys;

    /// <summary>Runs target <paramref name="name"/>; true if the input decoded, false if it was rejected.</summary>
    /// <exception cref="ArgumentException">Unknown target.</exception>
    public static bool Run(string name, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!Targets.TryGetValue(name, out Action<byte[]>? target))
        {
            throw new ArgumentException($"Unknown fuzz target '{name}'.", nameof(name));
        }

        try
        {
            target(data);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    // What a member may send to the relay, dispatched on the type byte exactly like RelayServer.Handle.
    private static void RelayMessage(byte[] data)
    {
        var r = new BinaryBufferReader(data);
        switch (Wire.ReadType(ref r))
        {
            case MessageType.Ping or MessageType.WaitVote: Wire.ReadValue(ref r); break;
            case MessageType.Chat: Wire.ReadChat(ref r); break;
            case MessageType.SetChoice: Wire.ReadSetChoice(ref r); break;
            case MessageType.SetSettings: Wire.ReadSetSettings(ref r); break;
            case MessageType.SetSlot: Wire.ReadSetSlot(ref r); break;
            case MessageType.SetReady or MessageType.Pause: r.ReadBoolean(); break;
            case MessageType.StartRequest: Wire.ReadStartRequest(ref r); break;
            case MessageType.Loaded: Wire.ReadLoaded(ref r); break;
            case MessageType.Batches: Wire.ReadBatches(ref r); break;
            case MessageType.Snapshot: Wire.ReadSnapshot(ref r); break;
            case MessageType.SaveRequest: Wire.ReadSaveRequest(ref r); break;
            case MessageType.SaveVote: Wire.ReadSaveVote(ref r); break;
            default: throw new InvalidDataException("Unexpected message type.");
        }
    }

    // What the relay may send to a member, dispatched like LockstepClient.Handle.
    private static void ClientMessage(byte[] data)
    {
        var r = new BinaryBufferReader(data);
        switch (Wire.ReadType(ref r))
        {
            case MessageType.Welcome: Wire.ReadWelcome(ref r); break;
            case MessageType.LobbyState: Wire.ReadLobbyState(ref r); break;
            case MessageType.ChatDelivery: Wire.ReadChatDelivery(ref r); break;
            case MessageType.Notice: Wire.ReadNotice(ref r); break;
            case MessageType.Load: Wire.ReadLoad(ref r); break;
            case MessageType.Start: Wire.ReadStart(ref r); break;
            case MessageType.Closures: Wire.ReadClosures(ref r, out _); break;
            case MessageType.Late: Wire.ReadLate(ref r); break;
            case MessageType.Pong: Wire.ReadValue(ref r); break;
            case MessageType.Status: Wire.ReadStatus(ref r); break;
            case MessageType.Desync: Wire.ReadDesync(ref r); break;
            case MessageType.SnapshotRequest: Wire.ReadSnapshotRequest(ref r); break;
            case MessageType.Resume: Wire.ReadResume(ref r); break;
            case MessageType.SaveProposal: Wire.ReadSaveProposal(ref r); break;
            case MessageType.SaveOutcome: Wire.ReadSaveOutcome(ref r); break;
            case MessageType.PauseState: Wire.ReadPauseState(ref r); break;
            case MessageType.End: Wire.ReadEnd(ref r); break;
            default: throw new InvalidDataException("Unexpected message type.");
        }
    }
}
