using System;
using System.Collections.Generic;
using System.IO;
using Tzar2.Core.Serialization;

namespace Tzar2.Content.Assets;

/// <summary>
/// A single imported bitmap (<c>.t2img</c>): UI screens, icons, terrain textures, masks, light maps, glyph strips.
/// Lossless: 16-bit sources keep their RGB555 value and only gain an opacity bit (colour-key pixels keep their colour
/// with bit 15 clear), 8-bit sources keep indices and palette (key entries get alpha 0), 24-bit sources become RGBA8.
/// </summary>
public sealed class ImageAsset
{
    /// <summary>File extension.</summary>
    public const string Extension = ".t2img";

    /// <summary>Current format version.</summary>
    public const ushort Version = 1;

    private const int HeaderSize = 20;
    private static ReadOnlySpan<byte> Magic => "T2IM"u8;

    /// <summary>Pixel layout.</summary>
    public required PixelFormat Format { get; init; }

    /// <summary>Width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>256 RGBA entries for <see cref="PixelFormat.Indexed8"/>, empty otherwise.</summary>
    public IReadOnlyList<Rgba32> Palette { get; init; } = [];

    /// <summary>Row-major top-down pixels.</summary>
    public required byte[] Pixels { get; init; }

    /// <summary>Serialises the image.</summary>
    public byte[] ToBytes()
    {
        Validate();
        var w = new BinaryBufferWriter(HeaderSize + (Palette.Count * 4) + Pixels.Length);
        w.WriteBytes(Magic);
        w.WriteUInt16(Version);
        w.WriteByte((byte)Format);
        w.WriteByte(0);
        w.WriteUInt32((uint)Width);
        w.WriteUInt32((uint)Height);
        w.WriteUInt16((ushort)Palette.Count);
        w.WriteUInt16(0);
        foreach (Rgba32 c in Palette)
        {
            w.WriteByte(c.R);
            w.WriteByte(c.G);
            w.WriteByte(c.B);
            w.WriteByte(c.A);
        }

        w.WriteBytes(Pixels);
        return w.ToArray();
    }

    /// <summary>Parses a <c>.t2img</c> file.</summary>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    public static ImageAsset Parse(ReadOnlySpan<byte> data)
    {
        var r = new BinaryBufferReader(data);
        if (data.Length < HeaderSize || !r.ReadBytes(4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("t2img: bad magic.");
        }

        ushort version = r.ReadUInt16();
        if (version != Version)
        {
            throw new InvalidDataException($"t2img: unsupported version {version}.");
        }

        byte format = r.ReadByte();
        if (!PixelFormats.IsDefined(format))
        {
            throw new InvalidDataException($"t2img: unknown pixel format {format}.");
        }

        r.ReadByte();
        uint width = r.ReadUInt32();
        uint height = r.ReadUInt32();
        int paletteCount = r.ReadUInt16();
        r.ReadUInt16();
        if (paletteCount > 256 || width > 65535 || height > 65535)
        {
            throw new InvalidDataException("t2img: invalid header.");
        }

        var palette = new Rgba32[paletteCount];
        for (int i = 0; i < paletteCount; i++)
        {
            palette[i] = new Rgba32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
        }

        long size = (long)width * height * PixelFormats.BytesPerPixel((PixelFormat)format);
        if (size != r.Remaining)
        {
            throw new InvalidDataException("t2img: pixel data size does not match.");
        }

        var image = new ImageAsset
        {
            Format = (PixelFormat)format,
            Width = (int)width,
            Height = (int)height,
            Palette = palette,
            Pixels = r.ReadBytes((int)size).ToArray(),
        };
        image.Validate();
        return image;
    }

    /// <summary>Checks internal consistency.</summary>
    /// <exception cref="InvalidDataException">The image is inconsistent.</exception>
    public void Validate()
    {
        if (Width is < 0 or > 65535 || Height is < 0 or > 65535)
        {
            throw new InvalidDataException("t2img: invalid size.");
        }

        if (Palette.Count != (Format == PixelFormat.Indexed8 ? 256 : 0))
        {
            throw new InvalidDataException("t2img: indexed images need exactly 256 palette entries, others none.");
        }

        if (Pixels.Length != (long)Width * Height * PixelFormats.BytesPerPixel(Format))
        {
            throw new InvalidDataException("t2img: pixel data size does not match.");
        }
    }

    /// <summary>The image as RGBA8 (top-down). R8 values are shown as grey with full alpha where non-zero.</summary>
    public byte[] ToRgba()
    {
        byte[] rgba = new byte[Width * Height * 4];
        for (int i = 0; i < Width * Height; i++)
        {
            Rgba32 c = Format switch
            {
                PixelFormat.Indexed8 => Palette[Pixels[i]].A == 0 ? Rgba32.Transparent : Palette[Pixels[i]],
                PixelFormat.Rgb5A1 => SpriteCompositor.DecodeRgb5A1(Pixels[2 * i] | (Pixels[(2 * i) + 1] << 8)),
                PixelFormat.Rgba8 => new Rgba32(Pixels[4 * i], Pixels[(4 * i) + 1], Pixels[(4 * i) + 2], Pixels[(4 * i) + 3]),
                _ => Pixels[i] == 0 ? Rgba32.Transparent : new Rgba32(Pixels[i], Pixels[i], Pixels[i], 255),
            };
            rgba[4 * i] = c.R;
            rgba[(4 * i) + 1] = c.G;
            rgba[(4 * i) + 2] = c.B;
            rgba[(4 * i) + 3] = c.A;
        }

        return rgba;
    }
}
