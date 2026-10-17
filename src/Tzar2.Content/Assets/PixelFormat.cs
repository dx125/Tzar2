namespace Tzar2.Content.Assets;

/// <summary>
/// Lossless pixel layouts of imported art (ADR 0013, spike S6, REQ-GFX-03). Each maps to one GPU texture-array
/// format; nothing is block-compressed.
/// </summary>
public enum PixelFormat : byte
{
    /// <summary>1 byte per pixel: index into the asset's 256-entry RGBA palette; palette alpha 0 = transparent. GPU: R8.</summary>
    Indexed8 = 1,

    /// <summary>2 bytes per pixel, little-endian: bit 15 = opaque, bits 0–14 = the original RGB555 value. GPU: RG8, unpacked in the shader.</summary>
    Rgb5A1 = 2,

    /// <summary>4 bytes per pixel: R, G, B, A (lossless home for 24-bit sources). GPU: RGBA8.</summary>
    Rgba8 = 3,

    /// <summary>1 byte per pixel of raw values: shadow masks (0 = none, 1 = shadow), font coverage. GPU: R8.</summary>
    R8 = 4,
}

/// <summary>Helpers for <see cref="PixelFormat"/>.</summary>
public static class PixelFormats
{
    /// <summary>Bytes per pixel of <paramref name="format"/>.</summary>
    public static int BytesPerPixel(PixelFormat format) => format switch
    {
        PixelFormat.Indexed8 => 1,
        PixelFormat.Rgb5A1 => 2,
        PixelFormat.Rgba8 => 4,
        PixelFormat.R8 => 1,
        _ => throw new System.ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format."),
    };

    /// <summary>Whether <paramref name="value"/> is a defined format.</summary>
    public static bool IsDefined(byte value) => value is >= 1 and <= 4;

    /// <summary>Lower-case name used in manifests.</summary>
    public static string Name(PixelFormat format) => format switch
    {
        PixelFormat.Indexed8 => "indexed8",
        PixelFormat.Rgb5A1 => "rgb5a1",
        PixelFormat.Rgba8 => "rgba8",
        PixelFormat.R8 => "r8",
        _ => "unknown",
    };
}
