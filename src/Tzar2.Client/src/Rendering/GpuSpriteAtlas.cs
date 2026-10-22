using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using Tzar2.Content.Assets;
using PixelFormat = Tzar2.Content.Assets.PixelFormat;

namespace Tzar2.Client.Rendering;

/// <summary>
/// Uploads a <see cref="SpriteAtlas"/> (ADR 0013) to the GPU: one <c>Texture2DArray</c> per pixel format (R8 indices,
/// RG8 RGB5A1 words, RGBA8, R8 masks; a 1×1 placeholder when unused), the palette rows, the cell table (two RGBA32F
/// texels per cell) and the shared player-ramp texture. Owns its textures.
/// </summary>
public sealed class GpuSpriteAtlas : IDisposable
{
    /// <summary>Width of the cell-table texture (two texels per cell).</summary>
    public const int CellTextureWidth = 4096;

    private readonly int[] _sheetBase;
    private readonly List<Resource> _owned = [];

    /// <summary>
    /// Uploads <paramref name="atlas"/> with the player ramps of <paramref name="palette"/>; kingdom p is drawn with
    /// colour <c>colourOf[p]</c> (SHELL-016; null: its own).
    /// </summary>
    public GpuSpriteAtlas(SpriteAtlas atlas, PlayerPalette palette, IReadOnlyList<int>? colourOf = null)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(palette);
        Atlas = atlas;
        IndexPages = Own(Array(atlas, PixelFormat.Indexed8, Image.Format.R8));
        RgbPages = Own(Array(atlas, PixelFormat.Rgb5A1, Image.Format.Rg8));
        RgbaPages = Own(Array(atlas, PixelFormat.Rgba8, Image.Format.Rgba8));
        MaskPages = Own(Array(atlas, PixelFormat.R8, Image.Format.R8));
        Palettes = Own(ImageTexture.CreateFromImage(Image.CreateFromData(256, atlas.PaletteRows, false, Image.Format.Rgba8, atlas.PaletteTexture)));
        Ramps = Own(ImageTexture.CreateFromImage(Image.CreateFromData(PlayerPalette.RampLength, PlayerPalette.Players, false, Image.Format.Rgba8, palette.ToRampTexture(colourOf))));

        _sheetBase = new int[atlas.SheetCount];
        int total = 0;
        for (int s = 0; s < atlas.SheetCount; s++)
        {
            _sheetBase[s] = total;
            total += atlas.CellsOf(s).Count;
        }

        int texels = Math.Max(2, total * 2);
        int height = (texels + CellTextureWidth - 1) / CellTextureWidth;
        float[] table = new float[CellTextureWidth * height * 4];
        for (int s = 0; s < atlas.SheetCount; s++)
        {
            IReadOnlyList<AtlasCell> cells = atlas.CellsOf(s);
            for (int c = 0; c < cells.Count; c++)
            {
                AtlasCell cell = cells[c];
                int o = (_sheetBase[s] + c) * 8;
                table[o] = cell.X;
                table[o + 1] = cell.Y;
                table[o + 2] = cell.Width;
                table[o + 3] = cell.Height;
                table[o + 4] = cell.Layer;
                table[o + 5] = (int)cell.Format;
                table[o + 6] = cell.PlayerFirst;
                table[o + 7] = cell.ShadowColour;
            }
        }

        Cells = Own(ImageTexture.CreateFromImage(Image.CreateFromData(
            CellTextureWidth, height, false, Image.Format.Rgbaf, MemoryMarshal.AsBytes(table.AsSpan()).ToArray())));
    }

    /// <summary>The packed atlas.</summary>
    public SpriteAtlas Atlas { get; }

    /// <summary>Indexed8 pages.</summary>
    public Texture2DArray IndexPages { get; }

    /// <summary>RGB5A1 pages.</summary>
    public Texture2DArray RgbPages { get; }

    /// <summary>RGBA8 pages.</summary>
    public Texture2DArray RgbaPages { get; }

    /// <summary>Shadow mask pages.</summary>
    public Texture2DArray MaskPages { get; }

    /// <summary>Palette rows.</summary>
    public ImageTexture Palettes { get; }

    /// <summary>Player ramps (16 × 8).</summary>
    public ImageTexture Ramps { get; }

    /// <summary>Cell table.</summary>
    public ImageTexture Cells { get; }

    /// <summary>Global index (cell table row) of cell <paramref name="cell"/> of sheet <paramref name="sheet"/>.</summary>
    public int GlobalCell(int sheet, int cell) => _sheetBase[sheet] + cell;

    /// <summary>Binds the textures to a material using <c>shaders/sprite.gdshader</c>.</summary>
    public void Bind(ShaderMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        material.SetShaderParameter("idx_tex", IndexPages);
        material.SetShaderParameter("rgb_tex", RgbPages);
        material.SetShaderParameter("rgba_tex", RgbaPages);
        material.SetShaderParameter("mask_tex", MaskPages);
        material.SetShaderParameter("pal_tex", Palettes);
        material.SetShaderParameter("ramp_tex", Ramps);
        material.SetShaderParameter("cell_tex", Cells);
        material.SetShaderParameter("cell_tex_width", CellTextureWidth);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (Resource resource in _owned)
        {
            resource.Dispose();
        }

        _owned.Clear();
    }

    private T Own<T>(T resource)
        where T : Resource
    {
        _owned.Add(resource);
        return resource;
    }

    private static Texture2DArray Array(SpriteAtlas atlas, PixelFormat format, Image.Format imageFormat)
    {
        IReadOnlyList<byte[]> pages = atlas.Pages(format);
        var images = new Godot.Collections.Array<Image>();
        if (pages.Count == 0)
        {
            int bpp = PixelFormats.BytesPerPixel(format);
            images.Add(Image.CreateFromData(1, 1, false, imageFormat, new byte[bpp]));
        }

        foreach (byte[] page in pages)
        {
            images.Add(Image.CreateFromData(atlas.PageSize, atlas.PageSize, false, imageFormat, page));
        }

        var array = new Texture2DArray();
        Error error = array.CreateFromImages(images);
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"Texture2DArray ({format}) creation failed: {error}.");
        }

        return array;
    }
}
