using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Tzar2.Core.Serialization;
using Tzar2.Game.Session;
using Tzar2.Net.Lockstep;
using Tzar2.Net.Protocol;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Kingdoms;

namespace Tzar2.Game.Multiplayer;

/// <summary>
/// Turns the relay's closed ticks into the sim's command sets and back (ADR 0162, 0163): each entry's payload is
/// decoded with <see cref="CommandCodec"/> and stamped with the kingdom of the relay slot it came from (never a
/// player field of the payload); a payload that does not decode, or would overflow the tick's command set, is skipped
/// on every peer alike. <see cref="CommandKind.SetController"/> from peers is dropped: only the relay's control events
/// produce it. The local player's commands go up as one payload per batch; checkpoint hashes are reported every
/// <see cref="NetworkMatch.HashInterval"/> ticks (full-state hash where the sim has one).
/// </summary>
internal sealed class LockstepDriver(NetworkMatch match, LockstepClient client) : ILockstepDriver
{
    private readonly CommandList _local = new();
    private readonly CommandList _decoded = new();
    private readonly CommandList _filtered = new();
    private readonly BinaryBufferWriter _payload = new(1024);
    private readonly List<uint> _saves = [];
    private readonly ConcurrentQueue<(ChatTarget Target, byte Mask, string Text)> _chat = new();
    private bool _resumed;

    /// <summary>Sim tick of closure 0 (a match resumed from a save or rejoined starts later than tick 0).</summary>
    public int TickOffset { get; private set; }

    /// <summary>The tick of the last desync notice (closure numbering), if any.</summary>
    public int? LastDesyncTick { get; set; }

    /// <summary>Test hook: changes a tick's command set on this peer only (forced desync).</summary>
    internal Action<int, CommandList>? TickHook { get; set; }

    /// <inheritdoc />
    public int SpeedPercent => match.SpeedPercent;

    /// <inheritdoc />
    public bool Paused => match.PausedByRelay;

    public void Begin(int tickOffset, bool resumed)
    {
        TickOffset = tickOffset;
        _resumed = resumed;
    }

    /// <inheritdoc />
    public void Pump(GameSession session)
    {
        client.Poll();
        match.ProcessEvents();
        if (client.Phase == ClientPhase.Running && client.OutboxEmpty && session.TakeLocalCommands(_local))
        {
            _payload.Clear();
            CommandCodec.WriteBatch(_payload, _local);
            if (_payload.Length <= Limits.MaxPayloadBytes)
            {
                client.Submit(_payload.ToArray());
            }
            else
            {
                match.System("Too many orders at once: some were dropped.");
            }
        }

        while (_chat.TryDequeue(out (ChatTarget Target, byte Mask, string Text) line))
        {
            client.Chat(Recipients(session.Simulation.World, session.LocalPlayer, line.Target, line.Mask), line.Text);
        }
    }

    /// <inheritdoc />
    public bool TryTakeTick(int tick, CommandList into)
    {
        int closureTick = tick - TickOffset;
        if (closureTick < 0 || !client.TryTakeClosure(closureTick, out TickClosure? closure))
        {
            return false;
        }

        into.Clear();
        if (closureTick == 0 && _resumed)
        {
            ReturnResumedKingdoms(into);
        }

        foreach (ControlEvent control in closure!.Controls)
        {
            Control(control, into);
        }

        foreach (ClosureEntry entry in closure.Entries)
        {
            byte kingdom = match.SimSlot(entry.Slot);
            if (kingdom == 255 || entry.Payload.Length == 0)
            {
                continue;
            }

            try
            {
                var reader = new BinaryBufferReader(entry.Payload);
                CommandCodec.ReadBatch(ref reader, _decoded);
            }
            catch (InvalidDataException)
            {
                continue; // every peer fails on the same bytes: skipped everywhere
            }

            CommandList batch = WithoutControllerChanges(_decoded);
            if (into.CanAppend(batch))
            {
                into.AppendStamped(kingdom, batch);
            }
        }

        TickHook?.Invoke(closureTick, into);
        return true;
    }

    /// <inheritdoc />
    public void AfterTick(GameSession session, TickResult result)
    {
        int closureTick = result.Tick.Value - TickOffset;
        if ((closureTick + 1) % match.HashInterval == 0)
        {
            client.ReportHash(closureTick, result.FullHash ?? result.StateHash);
        }

        foreach (uint id in _saves)
        {
            match.Saved(session, id, closureTick);
        }

        _saves.Clear();
    }

    /// <inheritdoc />
    public void RequestPause(bool paused) => client.Pause(paused);

    /// <inheritdoc />
    public void SendChat(ChatTarget target, byte customMask, string text) => _chat.Enqueue((target, customMask, text));

    // REQ-NET-07: the relay's control events become the same commands on every peer.
    private void Control(ControlEvent control, CommandList into)
    {
        byte kingdom = match.SimSlot(control.Slot);
        if (kingdom == 255)
        {
            return;
        }

        switch (control.Kind)
        {
            case ControlKind.Takeover when control.Arg == (int)DropPolicy.AiTakeover:
                into.Add(CommandKind.SetController, arg0: 1, player: kingdom);
                break;
            case ControlKind.Takeover when control.Arg == (int)DropPolicy.Neutral:
                into.Add(CommandKind.SetController, arg0: 0, player: kingdom);
                for (int other = 0; other < match.Descriptor!.Players.Length; other++)
                {
                    if (other != kingdom)
                    {
                        into.Add(CommandKind.SetDiplomacy, x: other, arg0: (int)Stance.Neutral, player: kingdom);
                    }
                }

                break;
            case ControlKind.Takeover:
                into.Add(CommandKind.Resign, player: kingdom);
                break;
            case ControlKind.Returned:
                into.Add(CommandKind.SetController, arg0: 0, player: kingdom);
                break;
            case ControlKind.Save:
                _saves.Add((uint)control.Arg);
                break;
        }
    }

    // A resumed save: kingdoms the computer had taken over go back to the players who are in the lobby again.
    private void ReturnResumedKingdoms(CommandList into)
    {
        MatchDescriptor descriptor = match.Descriptor!;
        foreach (MatchPlayer p in descriptor.Players)
        {
            if (p.Controller == ControllerKind.Human)
            {
                into.Add(CommandKind.SetController, arg0: 0, player: p.Slot);
            }
        }
    }

    private CommandList WithoutControllerChanges(CommandList batch)
    {
        // AI assistance only in a match that offers it (ADR 0183): every peer drops it alike.
        bool assist = match.Descriptor?.Settings.AiAssist == true;
        bool any = false;
        for (int i = 0; i < batch.Count; i++)
        {
            any |= batch[i].Kind == CommandKind.SetController || (!assist && batch[i].Kind == CommandKind.SetAiAssist);
        }

        if (!any)
        {
            return batch;
        }

        _filtered.Clear();
        for (int i = 0; i < batch.Count; i++)
        {
            ref readonly Command c = ref batch[i];
            if (c.Kind != CommandKind.SetController && (assist || c.Kind != CommandKind.SetAiAssist))
            {
                _filtered.Add(c.Kind, batch.EntitiesOf(c), c.X, c.Y, c.Target, c.Arg0, c.Arg1, c.Flags);
            }
        }

        return _filtered;
    }

    // Chat recipients as relay slots: allies are the own kingdom, mutual allies and team mates (REQ-NET-03).
    private byte Recipients(World world, byte local, ChatTarget target, byte customMask)
    {
        if (target == ChatTarget.All)
        {
            return ChatRecipients.All;
        }

        byte mask = 0;
        int team = world.Config.TeamOf(local);
        for (int k = 0; k < world.Config.PlayerCount; k++)
        {
            bool ally = k == local || (team != 0 && world.Config.TeamOf(k) == team) || world.Diplomacy.AreMutualAllies(local, k);
            bool include = target switch
            {
                ChatTarget.Allies => ally,
                ChatTarget.Enemies => !ally,
                _ => SlotMask.Contains(customMask, k),
            };
            byte relaySlot = match.LobbySlot((byte)k);
            if (include && relaySlot < 8)
            {
                mask |= SlotMask.Of(relaySlot);
            }
        }

        return mask;
    }
}
