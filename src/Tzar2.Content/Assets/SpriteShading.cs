using System;

namespace Tzar2.Content.Assets;

/// <summary>What one sprite texel contributes to the frame.</summary>
public enum TexelKind
{
    /// <summary>Nothing (transparent).</summary>
    None,

    /// <summary>An opaque colour.</summary>
    Colour,

    /// <summary>A shadow pixel (kind-7 mask, or a baked-shadow hint when shadows are drawn as shadow).</summary>
    Shadow,
}

/// <summary>
/// CPU reference of the client's sprite shader (<c>src/Tzar2.Client/shaders/sprite.gdshader</c>): decodes one texel of
/// a packed <see cref="SpriteAtlas"/> cell, recolours the player range with the original ramps (REQ-GFX-04) and
/// classifies baked-shadow hints. The shader must stay a line-by-line translation of <see cref="Shade"/>; tests pin the
/// semantics here, where they can run headless.
/// </summary>
public static class SpriteShading
{
    /// <summary>Decodes texel (<paramref name="x"/>, <paramref name="y"/>) of <paramref name="cell"/> (trimmed-box coordinates).</summary>
    /// <param name="atlas">The atlas holding the cell.</param>
    /// <param name="cell">The cell.</param>
    /// <param name="x">Column inside the trimmed box.</param>
    /// <param name="y">Row inside the trimmed box.</param>
    /// <param name="ramps">Player ramps; may be null when <paramref name="player"/> is −1.</param>
    /// <param name="player">Player 0–7, or −1 to keep the sheet's own (grey reference) ramp.</param>
    /// <param name="bakedShadowAsShadow">Treat baked-shadow hints as shadow (else they are drawn as their opaque colour).</param>
    public static (TexelKind Kind, Rgba32 Colour) Shade(
        SpriteAtlas atlas, AtlasCell cell, int x, int y, PlayerPalette? ramps, int player, bool bakedShadowAsShadow)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        if (cell.IsEmpty || x < 0 || y < 0 || x >= cell.Width || y >= cell.Height)
        {
            return (TexelKind.None, Rgba32.Transparent);
        }

        int bpp = PixelFormats.BytesPerPixel(cell.Format);
        byte[] page = atlas.Pages(cell.Format)[cell.Layer];
        int o = ((((cell.Y + y) * atlas.PageSize) + cell.X + x) * bpp);
        switch (cell.Format)
        {
            case PixelFormat.Indexed8:
            {
                int index = page[o];
                int p = ((cell.PaletteRow * 256) + index) * 4;
                byte[] pal = atlas.PaletteTexture;
                var colour = new Rgba32(pal[p], pal[p + 1], pal[p + 2], 255);
                byte cls = pal[p + 3];
                if (cls == SpriteAtlas.PaletteTransparent)
                {
                    return (TexelKind.None, Rgba32.Transparent);
                }

                if (cls == SpriteAtlas.PaletteShadow && bakedShadowAsShadow)
                {
                    return (TexelKind.Shadow, colour);
                }

                if (player >= 0 && ramps is not null && cell.PlayerFirst >= 0
                    && index >= cell.PlayerFirst && index < cell.PlayerFirst + PlayerPalette.RampLength)
                {
                    colour = ramps.Ramp(player, index - cell.PlayerFirst);
                }

                return (TexelKind.Colour, colour);
            }

            case PixelFormat.Rgb5A1:
            {
                int word = page[o] | (page[o + 1] << 8);
                if ((word & 0x8000) == 0)
                {
                    return (TexelKind.None, Rgba32.Transparent);
                }

                Rgba32 colour = Rgba32.FromRgb555(word & 0x7FFF);
                return bakedShadowAsShadow && (word & 0x7FFF) == cell.ShadowColour
                    ? (TexelKind.Shadow, colour)
                    : (TexelKind.Colour, colour);
            }

            case PixelFormat.Rgba8:
                return page[o + 3] == 0
                    ? (TexelKind.None, Rgba32.Transparent)
                    : (TexelKind.Colour, new Rgba32(page[o], page[o + 1], page[o + 2], 255));

            default:
                return page[o] == 0 ? (TexelKind.None, Rgba32.Transparent) : (TexelKind.Shadow, Rgba32.Transparent);
        }
    }
}
