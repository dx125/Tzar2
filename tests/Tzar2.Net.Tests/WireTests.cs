using System;
using System.IO;
using System.Linq;
using System.Text;
using Tzar2.Core.Serialization;
using Tzar2.Net.Protocol;
using Tzar2.Net.Relay;
using Xunit;

namespace Tzar2.Net.Tests;

public class WireTests
{
    [Fact]
    public void Hello_round_trips_and_carries_the_protocol_version()
    {
        var hello = new Hello(HelloKind.Create, "0.1.0", "Ana", string.Empty, 0, 0, new LobbyCreate("Friday", 4, true));
        var w = new BinaryBufferWriter();
        Wire.Write(w, hello);
        Hello back = Wire.ReadHello(w.WrittenSpan);
        Assert.Equal(hello.Kind, back.Kind);
        Assert.Equal(hello.Name, back.Name);
        Assert.Equal(hello.Create, back.Create);
        Assert.Equal(Wire.ProtocolVersion, back.Protocol);
    }

    [Fact]
    public void A_hello_of_another_protocol_version_is_read_as_its_version_only()
    {
        var w = new BinaryBufferWriter();
        w.WriteByte((byte)MessageType.Hello);
        w.WriteUInt32(Wire.Magic);
        w.WriteUInt16(Wire.ProtocolVersion + 7);
        w.WriteBytes([0xFF, 0xFF, 0xFF]); // whatever a future layout holds
        Assert.Equal(Wire.ProtocolVersion + 7, Wire.ReadHello(w.WrittenSpan).Protocol);
    }

    [Fact]
    public void Lobby_state_round_trips()
    {
        var state = new LobbyState("ABC234", "Lobby", 1, LobbyPhase.Lobby, 3, [1, 2, 3],
        [
            new SlotInfo(SlotState.Human, "a", false, true, 12, [9]),
            new SlotInfo(SlotState.Human, "b", true, true, 40, []),
            new SlotInfo(SlotState.Computer, string.Empty, false, false, 0, [7, 7]),
        ]);
        var w = new BinaryBufferWriter();
        Wire.Write(w, state);
        var r = new BinaryBufferReader(w.WrittenSpan);
        Assert.Equal(MessageType.LobbyState, Wire.ReadType(ref r));
        LobbyState back = Wire.ReadLobbyState(ref r);
        Assert.Equal(state.Code, back.Code);
        Assert.Equal(state.Settings, back.Settings);
        Assert.Equal(state.Slots.Length, back.Slots.Length);
        Assert.Equal(state.Slots[2].Choice, back.Slots[2].Choice);
        Assert.True(back.Slots[1].Ready);
        Assert.True(r.IsAtEnd);
    }

    [Fact]
    public void Closures_and_batches_round_trip()
    {
        var closure = new TickClosure(17, [new ClosureEntry(0, [1, 2]), new ClosureEntry(3, [4])], [new ControlEvent(ControlKind.Takeover, 3, 0)]);
        var w = new BinaryBufferWriter();
        Wire.WriteClosuresHeader(w, 5, 1);
        Wire.WriteClosure(w, closure);
        var r = new BinaryBufferReader(w.WrittenSpan);
        Assert.Equal(MessageType.Closures, Wire.ReadType(ref r));
        TickClosure back = Assert.Single(Wire.ReadClosures(ref r, out uint ack));
        Assert.Equal(5u, ack);
        Assert.Equal(17, back.Tick);
        Assert.Equal([1, 2], back.Entries[0].Payload);
        Assert.Equal(closure.Controls, back.Controls);

        w.Clear();
        Wire.WriteBatchesHeader(w, 1);
        Wire.WriteBatch(w, new Batch(9, 20, 15, [5, 6], [new TickHash(9, 0xABCDEF)]));
        r = new BinaryBufferReader(w.WrittenSpan);
        Assert.Equal(MessageType.Batches, Wire.ReadType(ref r));
        Batch batch = Assert.Single(Wire.ReadBatches(ref r));
        Assert.Equal(20, batch.TargetTick);
        Assert.Equal(0xABCDEFUL, batch.Hashes[0].Hash);
    }

    [Fact]
    public void Resume_requires_consecutive_backlog_after_the_snapshot_tick()
    {
        var w = new BinaryBufferWriter();
        Wire.Write(w, new ResumeMessage(10, 1, [1], [TickClosure.Empty(11), TickClosure.Empty(13)]));
        Assert.Throws<InvalidDataException>(() =>
        {
            var r = new BinaryBufferReader(w.WrittenSpan);
            Wire.ReadType(ref r);
            Wire.ReadResume(ref r);
        });
    }

    [Fact]
    public void Text_is_truncated_on_a_character_boundary_and_lone_surrogates_are_replaced()
    {
        string text = new string('é', 200); // 2 bytes each
        string cut = Wire.Truncate(text, 255);
        Assert.Equal(127, cut.Length);
        Assert.True(Encoding.UTF8.GetByteCount(cut) <= 255);
        Assert.Equal("a�b", Wire.Truncate("a\uD800b", 256));

        // The writers accept the result.
        var w = new BinaryBufferWriter();
        Wire.WriteChat(w, ChatRecipients.All, "x\uDC00" + new string('y', 400));
        var r = new BinaryBufferReader(w.WrittenSpan);
        Wire.ReadType(ref r);
        Assert.True(Encoding.UTF8.GetByteCount(Wire.ReadChat(ref r).Text) <= Limits.MaxChatBytes);
    }

    [Fact]
    public void Desync_bundle_round_trips()
    {
        var bundle = new DesyncBundle("ABCDEF", 99, 120, 7, [1], [3, 3],
            [new DesyncSlotReport(0, "a", 7, 99, [1], 125, 8, [2]), new DesyncSlotReport(1, "b", 9, 99, [1], 125, 10, [3])],
            [TickClosure.Empty(100), new TickClosure(101, [new ClosureEntry(1, [4])], [])]);
        DesyncBundle back = DesyncBundle.Parse(bundle.ToBytes());
        Assert.Equal(bundle.Tick, back.Tick);
        Assert.Equal(bundle.Slots.Length, back.Slots.Length);
        Assert.Equal([3], back.Slots[1].After);
        Assert.Equal(101, back.Log[1].Tick);
        byte[] bytes = bundle.ToBytes();
        Assert.Throws<InvalidDataException>(() => DesyncBundle.Parse(bytes.AsSpan(0, bytes.Length - 1)));
    }

    [Fact]
    public void Slot_masks()
    {
        Assert.True(SlotMask.Contains(ChatRecipients.All, 7));
        Assert.False(SlotMask.Contains(SlotMask.Of(2), 3));
        Assert.Equal(0b100, SlotMask.Of(2));
        Assert.Equal("ABC234", RelayServer.NormalizeCode(" abc-234 "));
        Assert.Equal(Limits.MaxCodeBytes, RelayServer.NormalizeCode(new string('a', 40)).Length);
    }

    [Fact]
    public void Writers_refuse_messages_beyond_the_limits()
    {
        var w = new BinaryBufferWriter();
        Assert.Throws<ArgumentException>(() => Wire.WriteBatch(w, new Batch(1, 1, 1, new byte[Limits.MaxPayloadBytes + 1], [])));
        Assert.Throws<ArgumentException>(() => Wire.Write(w, new BrowseReply(1, 20, Enumerable.Repeat(new LobbySummary("A", "b", 1, 2, LobbyPhase.Lobby, "v"), 17).ToArray())));
    }
}
