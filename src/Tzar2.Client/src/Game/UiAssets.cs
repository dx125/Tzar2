using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using Tzar2.Client.Viewer;
using Tzar2.Content.Assets;

namespace Tzar2.Client.Game;

/// <summary>
/// HUD and dialog art from the user's imported package (REQ-UI-01/02: reproduced at runtime, never shipped): images as
/// nearest-sampled RGBA8 textures (RGB555 expanded by bit replication, TDD §13.2), 48×48 cells of the icon and order
/// strips, and text drawn with the original bitmap fonts. Missing art falls back to flat placeholders so a partial
/// package still runs.
/// </summary>
public sealed class UiAssets : IDisposable
{
    /// <summary>Font of HUD numbers and labels.</summary>
    public const string SmallFont = "fonts/arial10_aa.json";

    /// <summary>Font of buttons and titles.</summary>
    public const string BoldFont = "fonts/arial12b_aa.json";

    /// <summary>Font of big titles.</summary>
    public const string TitleFont = "fonts/arial14b_aa.json";

    private readonly ContentStore _store;
    private readonly Dictionary<string, ImageTexture?> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Image?> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D> _cells = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (FontAsset Font, Image Strip)?> _fonts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Image> _tintedStrips = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageTexture> _texts = new(StringComparer.Ordinal);

    /// <summary>Creates the cache over <paramref name="store"/>.</summary>
    public UiAssets(ContentStore store) => _store = store;

    /// <summary>An image as RGBA8, or null when missing.</summary>
    public Image? Image(string asset)
    {
        if (!_images.TryGetValue(asset, out Image? image))
        {
            ImageAsset? source = null;
            try
            {
                source = _store.LoadImage(asset);
            }
            catch (InvalidDataException)
            {
            }

            image = source is null ? null : Godot.Image.CreateFromData(source.Width, source.Height, false, Godot.Image.Format.Rgba8, source.ToRgba());
            _images[asset] = image;
        }

        return image;
    }

    /// <summary>A texture of an image, or null when missing.</summary>
    public ImageTexture? Texture(string asset)
    {
        if (!_textures.TryGetValue(asset, out ImageTexture? texture))
        {
            texture = Image(asset) is { } image ? ImageTexture.CreateFromImage(image) : null;
            _textures[asset] = texture;
        }

        return texture;
    }

    /// <summary>A sub-rectangle of an image (button states, icon cells), or null.</summary>
    public Texture2D? Region(string asset, Rect2I rect)
    {
        string key = $"{asset}|{rect.Position.X},{rect.Position.Y},{rect.Size.X},{rect.Size.Y}";
        if (_cells.TryGetValue(key, out Texture2D? cell))
        {
            return cell;
        }

        if (Texture(asset) is not { } texture || rect.End.X > texture.GetWidth() || rect.End.Y > texture.GetHeight() || rect.Size.X <= 0)
        {
            return null;
        }

        cell = new AtlasTexture { Atlas = texture, Region = new Rect2(rect.Position, rect.Size) };
        _cells[key] = cell;
        return cell;
    }

    /// <summary>Cell <paramref name="index"/> of a vertical strip of square cells (icons.bmp, buttons.bmp), or null.</summary>
    public Texture2D? Cell(string asset, int index, int size = 48)
        => index < 0 ? null : Region(asset, new Rect2I(0, index * size, size, size));

    /// <summary>
    /// <paramref name="text"/> drawn with an imported bitmap font (1:1 glyphs, coverage → alpha) in
    /// <paramref name="colour"/>, or null when the font is missing.
    /// </summary>
    public ImageTexture? Text(string text, string font = SmallFont, Color? colour = null)
    {
        Color c = colour ?? Colors.White;
        string key = $"{font}|{c.ToHtml()}|{text}";
        if (_texts.TryGetValue(key, out ImageTexture? cached))
        {
            return cached;
        }

        if (LoadFont(font) is not { } loaded || text.Length == 0)
        {
            return null;
        }

        (FontAsset asset, Image strip) = loaded;
        if (c != Colors.White)
        {
            strip = Tinted(font, strip, c);
        }

        IReadOnlyList<PlacedGlyph> glyphs = asset.Layout(text);
        int width = Math.Max(1, asset.Measure(text)), height = asset.CellHeight;
        foreach (PlacedGlyph g in glyphs)
        {
            height = Math.Max(height, g.Y + asset.CellHeight);
        }

        Image image = Godot.Image.CreateEmpty(width, height, false, Godot.Image.Format.Rgba8);
        foreach (PlacedGlyph g in glyphs)
        {
            image.BlendRect(strip, new Rect2I(0, g.Glyph * asset.CellHeight, g.Width, asset.CellHeight), new Vector2I(g.X, g.Y));
        }

        var texture = ImageTexture.CreateFromImage(image);
        if (_texts.Count > 4096)
        {
            ClearTexts();
        }

        _texts[key] = texture;
        return texture;
    }

    /// <summary>Width of <paramref name="text"/> in <paramref name="font"/>.</summary>
    public int Measure(string text, string font = SmallFont) => LoadFont(font) is { } f ? f.Font.Measure(text) : text.Length * 6;

    /// <inheritdoc/>
    public void Dispose()
    {
        ClearTexts();
        foreach (ImageTexture? t in _textures.Values)
        {
            t?.Dispose();
        }

        _textures.Clear();
        _cells.Clear();
        _images.Clear();
        _tintedStrips.Clear();
    }

    private void ClearTexts()
    {
        foreach (ImageTexture t in _texts.Values)
        {
            t.Dispose();
        }

        _texts.Clear();
    }

    // The glyph strip tinted once per colour (texts are then plain blits).
    private Image Tinted(string font, Image strip, Color c)
    {
        string key = $"{font}|{c.ToHtml()}";
        if (_tintedStrips.TryGetValue(key, out Image? tinted))
        {
            return tinted;
        }

        byte[] data = strip.GetData();
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i] = (byte)(data[i] * c.R);
            data[i + 1] = (byte)(data[i + 1] * c.G);
            data[i + 2] = (byte)(data[i + 2] * c.B);
            data[i + 3] = (byte)(data[i + 3] * c.A);
        }

        tinted = Godot.Image.CreateFromData(strip.GetWidth(), strip.GetHeight(), false, Godot.Image.Format.Rgba8, data);
        _tintedStrips[key] = tinted;
        return tinted;
    }

    private (FontAsset Font, Image Strip)? LoadFont(string font)
    {
        if (_fonts.TryGetValue(font, out (FontAsset, Image)? loaded))
        {
            return loaded;
        }

        loaded = null;
        try
        {
            if (_store.Vfs.Exists(font))
            {
                FontAsset asset = FontAsset.Parse(_store.Read(font));
                if (_store.LoadImage(asset.Strip) is { } strip)
                {
                    byte[] rgba = FontAsset.StripToRgba(strip, new Rgba32(255, 255, 255, 255));
                    loaded = (asset, Godot.Image.CreateFromData(strip.Width, strip.Height, false, Godot.Image.Format.Rgba8, rgba));
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or IOException)
        {
            GD.PrintErr($"Font {font}: {e.Message}");
        }

        _fonts[font] = loaded;
        return loaded;
    }
}
