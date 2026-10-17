namespace Tzar2.Formats;

/// <summary>An 8-bit-per-channel colour (palette entry).</summary>
public readonly record struct Rgb24(byte R, byte G, byte B)
{
    /// <summary>Whether this is the pure-green transparency key (0, 255, 0) of the original art (docs/formats/bmp.md §1.2).</summary>
    public bool IsGreenKey => R == 0 && G == 255 && B == 0;
}

/// <summary>RGB555 helpers (docs/formats/rle.md §5.1, TDD §13.2).</summary>
public static class Rgb555
{
    /// <summary>The green transparency key of sprites, icons and overlays.</summary>
    public const ushort GreenKey = 0x03E0;

    /// <summary>The magenta key used in terrain transition tiles.</summary>
    public const ushort MagentaKey = 0x7C1F;

    /// <summary>Expands a 5-bit channel to 8 bits by bit replication (UNK-39 keeps this swappable).</summary>
    public static byte Expand5(int c5) => (byte)((c5 << 3) | (c5 >> 2));

    /// <summary>Expands an RGB555 value (bit 15 ignored) to 8 bits per channel.</summary>
    public static Rgb24 ToRgb24(ushort value)
        => new(Expand5((value >> 10) & 31), Expand5((value >> 5) & 31), Expand5(value & 31));
}
