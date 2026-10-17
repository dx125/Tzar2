using System;
using System.Collections.Generic;

namespace Tzar2.Formats.Pal;

/// <summary>
/// <c>IMAGES/GAME.PAL</c> (docs/formats/pal.md): <c>"PALF"</c>, uint16 count, uint16 0, uint32 0, then count × B, G, R, 0.
/// Entries 112–255 hold nine 16-step ramps; ramp <c>p</c> (0–7) is the player colour of slot <c>p</c>.
/// </summary>
public sealed class GamePalette
{
    /// <summary>First entry of the player ramps.</summary>
    public const int PlayerRampBase = 112;

    /// <summary>Entries per ramp.</summary>
    public const int RampLength = 16;

    /// <summary>Number of player ramps (blue, red, green, purple, yellow, cyan, orange, grey).</summary>
    public const int PlayerCount = 8;

    private const string Format = "PAL";

    private GamePalette(Rgb24[] entries) => Entries = entries;

    /// <summary>All entries.</summary>
    public IReadOnlyList<Rgb24> Entries { get; }

    /// <summary>Parses a PALF file.</summary>
    /// <exception cref="LegacyFormatException">The data is malformed.</exception>
    public static GamePalette Parse(ReadOnlySpan<byte> data)
    {
        if (!ByteReader.Matches(data, 0, "PALF"u8))
        {
            throw new LegacyFormatException(Format, "missing 'PALF' signature.");
        }

        int count = ByteReader.U16(data, 4, Format, "entry count");
        if (count > 256)
        {
            throw new LegacyFormatException(Format, $"{count} entries (max 256).");
        }

        ByteReader.Require(data, 12, 4L * count, Format, "entries");
        var entries = new Rgb24[count];
        for (int i = 0; i < count; i++)
        {
            int p = 12 + (4 * i);
            entries[i] = new Rgb24(data[p + 2], data[p + 1], data[p]);
        }

        return new GamePalette(entries);
    }

    /// <summary>The 16 colours of player ramp <paramref name="player"/> (0–7).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The player is out of range or the palette is too short.</exception>
    public Rgb24[] PlayerRamp(int player)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(player);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(player, PlayerCount);
        int start = PlayerRampBase + (RampLength * player);
        if (start + RampLength > Entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(player), "The palette has no ramp for this player.");
        }

        var ramp = new Rgb24[RampLength];
        for (int i = 0; i < RampLength; i++)
        {
            ramp[i] = Entries[start + i];
        }

        return ramp;
    }
}
