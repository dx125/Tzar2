namespace Tzar2.Content.Assets;

/// <summary>An 8-bit-per-channel RGBA colour.</summary>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A)
{
    /// <summary>Fully transparent black.</summary>
    public static Rgba32 Transparent => default;

    /// <summary>The debug look of shadow pixels: black at 50 % (matches the RE prototype's PNGs).</summary>
    public static Rgba32 ShadowDebug => new(0, 0, 0, 128);

    /// <summary>Expands an RGB555 value by bit replication (TDD §13.2; swappable per UNK-39).</summary>
    public static Rgba32 FromRgb555(int value)
        => new(Expand5((value >> 10) & 31), Expand5((value >> 5) & 31), Expand5(value & 31), 255);

    /// <summary>Bit-replication expansion of a 5-bit channel.</summary>
    public static byte Expand5(int c5) => (byte)((c5 << 3) | (c5 >> 2));
}
