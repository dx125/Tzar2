using System;
using System.Collections.Generic;
using FsCheck.Xunit;
using Tzar2.Core.Serialization;
using Tzar2.Net.Lan;
using Tzar2.Net.Protocol;
using Tzar2.Net.Relay;
using Xunit;

namespace Tzar2.Net.Tests;

/// <summary>
/// REQ-NET-11: every network decoder accepts or rejects hostile bytes with <see cref="System.IO.InvalidDataException"/>
/// only, and never allocates far beyond the input size (no count or length is trusted before it is checked against the
/// remaining input). Random bytes rarely pass the type byte, so valid messages are mutated too.
/// </summary>
public class WireFuzzTests
{
    private static readonly Dictionary<string, byte[][]> Seeds = BuildSeeds();

    public static TheoryData<string> Targets() => new(FuzzTargets.Names);

    [Theory]
    [MemberData(nameof(Targets))]
    public void Seeds_are_valid(string target)
    {
        foreach (byte[] seed in Seeds[target])
        {
            Assert.True(FuzzTargets.Run(target, seed), $"{target} seed of {seed.Length} bytes does not decode.");
        }
    }

    [Property(MaxTest = 300)]
    public void Random_bytes_never_crash(byte[] data)
    {
        foreach (string target in FuzzTargets.Names)
        {
            RunBounded(target, data ?? []);
        }
    }

    [Property(MaxTest = 400)]
    public void Mutated_messages_never_crash(int seed)
    {
        var random = new Random(seed);
        foreach ((string target, byte[][] seeds) in Seeds)
        {
            byte[] data = (byte[])seeds[random.Next(seeds.Length)].Clone();
            switch (random.Next(5))
            {
                case 0 when data.Length > 0:
                    data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when data.Length > 0:
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                    break;
                case 2:
                    data = data.AsSpan(0, random.Next(data.Length + 1)).ToArray();
                    break;
                case 3 when data.Length > 2:
                    // A huge varint where a count usually sits.
                    int at = random.Next(1, data.Length);
                    byte[] huge = [0xFF, 0xFF, 0xFF, 0xFF, 0x7F];
                    data = [.. data.AsSpan(0, at), .. huge, .. data.AsSpan(at)];
                    break;
                default:
                    data = [.. data, .. new byte[random.Next(1, 16)]];
                    break;
            }

            RunBounded(target, data);
        }
    }

    private static void RunBounded(string target, byte[] data)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        FuzzTargets.Run(target, data);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < (64 * 1024) + (64L * data.Length), $"{target} allocated {allocated} B for {data.Length} B of input.");
    }

    private static Dictionary<string, byte[][]> BuildSeeds()
    {
        byte[] W(Action<BinaryBufferWriter> write)
        {
            var w = new BinaryBufferWriter();
            write(w);
            return w.ToArray();
        }

        var closure = new TickClosure(4, [new ClosureEntry(1, [9, 9, 9])], [new ControlEvent(ControlKind.Save, 0, 3)]);
        var lobby = new LobbyState("ABCDEF", "L", 0, LobbyPhase.Lobby, 1, [1], [new SlotInfo(SlotState.Human, "a", true, true, 5, [2])]);
        return new Dictionary<string, byte[][]>(StringComparer.Ordinal)
        {
            ["hello"] = [W(w => Wire.Write(w, new Hello(HelloKind.Join, "v", "n", "ABCDEF", 0, 0, null))),
                W(w => Wire.Write(w, new Hello(HelloKind.Create, "v", "n", string.Empty, 0, 0, new LobbyCreate("x", 2, true))))],
            ["refusal"] = [W(w => Wire.Write(w, new Refusal(RefusalReason.LobbyFull, "full")))],
            ["browse-query"] = [W(w => Wire.Write(w, new BrowseQuery(0, 7)))],
            ["browse-reply"] = [W(w => Wire.Write(w, new BrowseReply(7, 1, [new LobbySummary("ABCDEF", "L", 1, 2, LobbyPhase.Lobby, "v")])))],
            ["relay-message"] =
            [
                W(w => Wire.WriteValue(w, MessageType.Ping, 123)),
                W(w => Wire.WriteChat(w, 3, "hi")),
                W(w => Wire.WriteSetChoice(w, 1, [1, 2])),
                W(w => Wire.WriteSetSettings(w, [5, 5, 5])),
                W(w => Wire.WriteSetSlot(w, 2, SlotState.Computer)),
                W(w => Wire.WriteFlag(w, MessageType.SetReady, true)),
                W(w => Wire.WriteStartRequest(w, new MatchParameters(0, 50_000, 10, 30_000, DropPolicy.Neutral, 3))),
                W(w => Wire.WriteLoaded(w, [1, 2], 3)),
                W(w =>
                {
                    Wire.WriteBatchesHeader(w, 2);
                    Wire.WriteBatch(w, new Batch(1, 5, 3, [1, 2, 3], [new TickHash(9, 4)]));
                    Wire.WriteBatch(w, new Batch(2, 6, 4, [], []));
                }),
                W(w => Wire.Write(w, new SnapshotMessage(SnapshotPurpose.Desync, 4, 10, 5, [1, 2], 9, [3]))),
                W(w => Wire.WriteSaveRequest(w, "s")),
                W(w => Wire.WriteSaveVote(w, 1, true)),
            ],
            ["client-message"] =
            [
                W(w => Wire.Write(w, new Welcome("ABCDEF", 1, 5, 2))),
                W(w => Wire.Write(w, lobby)),
                W(w => Wire.WriteChatDelivery(w, 1, 255, "hello")),
                W(w => Wire.WriteNotice(w, NoticeKind.ChatThrottled, "slow")),
                W(w => Wire.Write(w, new MatchParameters(1, 50_000, 10, 30_000, DropPolicy.AiTakeover, 3))),
                W(w => Wire.WriteStart(w, 1500)),
                W(w =>
                {
                    Wire.WriteClosuresHeader(w, 3, 2);
                    Wire.WriteClosure(w, closure);
                    Wire.WriteClosure(w, TickClosure.Empty(5));
                }),
                W(w => Wire.WriteLate(w, 3, 4, 6)),
                W(w => Wire.WriteStatus(w, [new PeerStatus(SlotControl.Human, true, 40, 2, 0)])),
                W(w => Wire.Write(w, new DesyncNotice(19, 8, [1]))),
                W(w => Wire.WriteSnapshotRequest(w, SnapshotPurpose.Rejoin, 2)),
                W(w => Wire.Write(w, new ResumeMessage(3, 1, [7], [closure]))),
                W(w => Wire.Write(w, new SaveProposal(1, 0, "x"))),
                W(w => Wire.Write(w, new SaveOutcome(1, true, 255))),
                W(w => Wire.WritePauseState(w, true, 1, 2)),
                W(w => Wire.Write(w, new EndMessage(100, EndReason.Desync, 1, "d"))),
            ],
            ["desync-bundle"] = [new DesyncBundle("ABCDEF", 1, 10, 2, [1], [], [new DesyncSlotReport(0, "a", 1, 5, [1], 10, 2, [3])], [TickClosure.Empty(6)]).ToBytes()],
            ["lan-query"] = [LanDiscovery.Query(5)],
            ["lan-answer"] = [LanDiscovery.Answer(1, 4000, [new LobbySummary("ABCDEF", "L", 1, 2, LobbyPhase.Lobby, "v")])],
        };
    }
}
