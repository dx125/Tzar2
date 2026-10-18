using System;
using System.Collections.Generic;
using FsCheck.Xunit;
using Tzar2.Formats.Fuzzing;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Formats.Tests;

/// <summary>
/// TDD §22.6: no decoder may fail with anything but <see cref="LegacyFormatException"/>. Random bytes rarely get past
/// the magic, so the stronger tests mutate valid synthetic files (bit flips, byte stores, truncation, extension).
/// The same entry points (<see cref="FuzzTargets"/>) serve a SharpFuzz harness.
/// </summary>
public class FuzzPropertyTests
{
    private static readonly Dictionary<string, byte[]> Seeds = new()
    {
        ["rle"] = SyntheticFormats.Rle(1, 2, 2, 30, 6, (c, x, y) => (x + y + c) % 3 == 0 ? x : null),
        ["dat"] = SyntheticFormats.Dat((0, 0, 63, 63), [0, 2, 2, 0], [(0, 0, 9, 9), (1, 1, 5, 5)]),
        ["bmp"] = SyntheticFormats.Bmp(5, 3, 8, new byte[15]),
        ["pal"] = SyntheticFormats.Pal(),
        ["fnt"] = SyntheticFormats.Fnt("Arial", 5, 6, 3, c => c > 32 ? (4, c % 3) : (0, 0)),
        ["hpfs"] = SyntheticFormats.Hpfs([("MAPS/a", [1, 2, 3]), ("MAPS/b.wmp", SyntheticFormats.Hpfs([("x", new byte[700])])), ("USER/c", new byte[1100])], truncate: 50),
        ["ini"] = "[a]\r\nk = v\r\nbare\r\n[b"u8.ToArray(),
        ["tsv"] = "A\tB\r\n\"x, y\"\t2\r\n"u8.ToArray(),
        ["wav"] = SyntheticFormats.Wav(frames: 10),
        ["mid"] = SyntheticFormats.Mid(),
        ["wmp"] = FuzzMap(),
        ["wcm"] = SyntheticMap.Campaign("C", "D", [("Fuzz", true, FuzzMap())]),
    };

    private static byte[] FuzzMap()
    {
        var m = new SyntheticMap { Name = "Fuzz", Side = 4 };
        m.Objects.Add(new SyntheticObject { Id = 1 });
        m.Actions.Add(("a", 5, new SyntheticPayload().Str("$x = 1").ToArray()));
        m.Rules.Add(("r", "1", ["a"]));
        return m.Build();
    }

    public static TheoryData<string> Targets() => new(FuzzTargets.Names);

    [Theory]
    [MemberData(nameof(Targets))]
    public void Seeds_are_valid(string target) => Assert.True(FuzzTargets.Run(target, Seeds[target]));

    [Property(MaxTest = 300)]
    public void Random_bytes_never_crash(byte[] data)
    {
        foreach (string target in FuzzTargets.Names)
        {
            FuzzTargets.Run(target, data ?? []);
        }
    }

    [Property(MaxTest = 500)]
    public void Mutated_files_never_crash(int seed)
    {
        var random = new Random(seed);
        foreach (string target in FuzzTargets.Names)
        {
            FuzzTargets.Run(target, Mutate(Seeds[target], random));
        }
    }

    [Fact]
    public void Every_truncation_of_every_seed_is_handled()
    {
        foreach ((string target, byte[] seed) in Seeds)
        {
            for (int length = 0; length < seed.Length; length += Math.Max(1, seed.Length / 300))
            {
                FuzzTargets.Run(target, seed[..length]);
            }
        }
    }

    private static byte[] Mutate(byte[] seed, Random random)
    {
        byte[] data = (byte[])seed.Clone();
        int edits = 1 + random.Next(8);
        for (int i = 0; i < edits; i++)
        {
            switch (random.Next(5))
            {
                case 0 when data.Length > 0:
                    data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when data.Length > 0:
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                    break;
                case 2 when data.Length > 0:
                    data[random.Next(data.Length)] = random.Next(2) == 0 ? (byte)0 : (byte)0xFF;
                    break;
                case 3:
                    data = data[..random.Next(data.Length + 1)];
                    break;
                default:
                    byte[] extra = new byte[random.Next(64)];
                    random.NextBytes(extra);
                    data = [.. data, .. extra];
                    break;
            }
        }

        return data;
    }
}
