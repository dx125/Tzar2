using System;
using System.IO;
using System.Linq;
using Tzar2.Content.Rules;
using Tzar2.Core.Ids;
using Tzar2.Game.Session;
using Tzar2.Net.Protocol;
using Tzar2.Net.Relay;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Xunit;

namespace Tzar2.Tools.Cli.Tests;

/// <summary><c>t2 replay</c>, <c>t2 desync</c> and <c>t2 soak</c> (ADRs 0164, 0165, 0167).</summary>
[Collection("soak")]
public sealed class MultiplayerCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tzar2-cli-mp-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliApp.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void A_recorded_replay_verifies_and_a_tampered_one_does_not()
    {
        string file = Path.Combine(_dir, "a.t2rep");
        (int code, string output, string error) = Run("replay", "record", "--synthetic", "--players", "2", "--ticks", "300", "--size", "64", "--out", file);
        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("recorded 300 ticks", output, StringComparison.Ordinal);

        (code, output, error) = Run("replay", "verify", "--synthetic", file, "--report", Path.Combine(_dir, "r.json"));
        Assert.True(code == ExitCodes.Success, error + output);
        Assert.Contains(": OK 300 ticks", output, StringComparison.Ordinal);
        Assert.Contains("\"verified\": true", File.ReadAllText(Path.Combine(_dir, "r.json")), StringComparison.Ordinal);

        (code, output, _) = Run("replay", "info", file);
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("2 kingdoms", output, StringComparison.Ordinal);

        // The file ends with tick 299's full hash, the end tag and the tick count (300 = 0xAC 0x02): flip a hash byte.
        byte[] bytes = File.ReadAllBytes(file);
        Assert.Equal([0, 0xAC, 0x02], bytes[^3..]);
        bytes[^4] ^= 0x40;
        string tampered = Path.Combine(_dir, "b.t2rep");
        File.WriteAllBytes(tampered, bytes);
        (code, output, _) = Run("replay", "verify", "--synthetic", tampered);
        Assert.Equal(ExitCodes.Mismatch, code);
        Assert.Contains("MISMATCH at tick 299", output, StringComparison.Ordinal);

        (code, _, _) = Run("replay", "verify");
        Assert.Equal(ExitCodes.UsageError, code);
    }

    [Fact]
    public void Desync_diff_names_the_differing_entity_and_extract_writes_the_snapshots()
    {
        // A report of two peers whose states differ by one move order.
        var rules = SyntheticLegacyContent.CompileRuleset();
        var settings = new MatchSettings { Map = MapSourceKind.TestMap, TestMapSize = 64, Seed = 3 };
        byte[] choice = SlotChoice.Default.ToBytes();
        DesyncLobbySlot[] lobby = [new(SlotState.Human, "a", choice), new(SlotState.Human, "b", choice)];
        var descriptor = new MatchDescriptor(settings, [new MatchPlayer(0, "a", "euro", ControllerKind.Human, Difficulty.Normal, 0), new MatchPlayer(1, "b", "euro", ControllerKind.Human, Difficulty.Normal, 0)]);
        MatchSetup setup = descriptor.ToSetup(SkirmishAi.EngineDefault, multiplayer: true, localSlot: null);
        byte[] Snapshot(bool move)
        {
            // The second peer's local player is kingdom 1: its order is stamped with that slot.
            using GameSession s = GameSession.ForReplay(move ? setup with { LocalSlot = 1 } : setup, rules);
            s.Step(20);
            if (move)
            {
                World w = s.Simulation.World;
                EntityId unit = w.Identities.Entities.ToArray().First(e => w.Identities.Get(e).Owner == 1 && w.TypeOf(e).IsMobile);
                var batch = new CommandList();
                batch.Add(CommandKind.Move, [unit], x: 20 * 2048, y: 20 * 2048);
                s.Submit(batch);
            }

            s.Step(40);
            return s.CaptureSnapshot();
        }

        byte[] same = Snapshot(move: false);
        byte[] moved = Snapshot(move: true);
        var bundle = new DesyncBundle("ABCDEF", 1, 59, 7, [1], settings.ToBytes(),
            [new DesyncSlotReport(0, "a", 7, -1, [], 59, 1, same), new DesyncSlotReport(1, "b", 8, -1, [], 59, 2, moved)], [])
        {
            LobbySlots = lobby,
        };
        string report = Path.Combine(_dir, "r.t2desync");
        File.WriteAllBytes(report, bundle.ToBytes());

        (int code, string output, string error) = Run("desync", "diff", report, "--synthetic");
        Assert.True(code == ExitCodes.Mismatch, error + output);
        Assert.Contains("odd slots [1]", output, StringComparison.Ordinal);
        Assert.Contains("store:core.position entity", output, StringComparison.Ordinal);
        Assert.Contains("of player 1", output, StringComparison.Ordinal);

        (code, output, _) = Run("desync", "extract", report, "--out", Path.Combine(_dir, "x"));
        Assert.Equal(ExitCodes.Success, code);
        string[] snaps = Directory.GetFiles(Path.Combine(_dir, "x"), "*.snap").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        Assert.Equal(["slot0-after-t59.snap", "slot1-after-t59.snap"], snaps);
        (code, output, error) = Run("desync", "diff", Path.Combine(_dir, "x", snaps[0]), Path.Combine(_dir, "x", snaps[0]), "--setup", report, "--synthetic");
        Assert.True(code == ExitCodes.Success, error + output);
        Assert.Contains("no differences", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_small_soak_passes_and_writes_its_report()
    {
        string report = Path.Combine(_dir, "soak.json");
        (int code, string output, string error) = Run("soak", "--synthetic", "--matches", "1", "--parallel", "1", "--ticks", "400", "--map", "64",
            "--rtt", "60", "--loss", "2", "--disconnects", "1", "--drop-timeout", "1", "--computers", "1", "--report", report);
        Assert.True(code == ExitCodes.Success, error + output);
        Assert.Contains("soak result: 1/1 matches ok, 0 desyncs", output, StringComparison.Ordinal);
        string json = File.ReadAllText(report);
        Assert.Contains("\"desyncs\": 0", json, StringComparison.Ordinal);
        Assert.Contains("\"rejoins\": 1", json, StringComparison.Ordinal);
        (code, _, _) = Run("soak", "--matches", "0");
        Assert.Equal(ExitCodes.UsageError, code);
    }
}

[CollectionDefinition("soak", DisableParallelization = true)]
public sealed class SoakCollection
{
}
