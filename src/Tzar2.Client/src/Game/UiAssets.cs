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
    private readonly Dictionary<string, FontFile?> _godotFonts = new(StringComparer.Ordinal);
    private Theme? _theme;

    /// <summary>Creates the cache over <paramref name="store"/>.</summary>
    public UiAssets(ContentStore store) => _store = store;

    /// <summary>The numbers of key <paramref name="key"/> of IMAGES.INI entry <paramref name="entry"/> (a cursor's hotspot), or null.</summary>
    public int[]? ImageInts(string entry, string key) => _store.Index.Find(entry)?.GetInts(key);

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

    /// <summary>
    /// A sub-rectangle of an image as a texture of its own (for tiling: an atlas region cannot repeat), or null.
    /// </summary>
    public ImageTexture? Slice(string asset, Rect2I rect)
    {
        string key = $"{asset}|slice|{rect.Position.X},{rect.Position.Y},{rect.Size.X},{rect.Size.Y}";
        if (!_textures.TryGetValue(key, out ImageTexture? slice))
        {
            slice = Image(asset) is { } image && rect.End.X <= image.GetWidth() && rect.End.Y <= image.GetHeight() && rect.Size.X > 0
                ? ImageTexture.CreateFromImage(image.GetRegion(rect))
                : null;
            _textures[key] = slice;
        }

        return slice;
    }

    /// <summary>Cell <paramref name="index"/> of a vertical strip of square cells (icons.bmp, buttons.bmp), or null.</summary>
    public Texture2D? Cell(string asset, int index, int size = 48)
        => index < 0 ? null : Region(asset, new Rect2I(0, index * size, size, size));

    /// <summary>
    /// Cell <paramref name="index"/> of a strip in player <paramref name="player"/>'s colour (portraits of ICONS.BMP: the
    /// sprites' player range 240-255 takes the owner's ramp), or the plain cell for no player (−1, Nature).
    /// </summary>
    public Texture2D? PlayerCell(string asset, int index, int player, int size = 48)
    {
        if (player is < 0 or >= PlayerPalette.Players)
        {
            return Cell(asset, index, size);
        }

        string key = $"{asset}|player{player}";
        if (!_textures.TryGetValue(key, out ImageTexture? strip))
        {
            ImageAsset? source = null;
            try
            {
                source = _store.LoadImage(asset);
            }
            catch (InvalidDataException)
            {
            }

            strip = source is null ? null : ImageTexture.CreateFromImage(Godot.Image.CreateFromData(source.Width, source.Height, false, Godot.Image.Format.Rgba8, source.ToRgba(_store.Palette.RampOf(player))));
            _textures[key] = strip;
        }

        if (index < 0 || strip is null || (index + 1) * size > strip.GetHeight())
        {
            return null;
        }

        string cellKey = $"{key}|{index}|{size}";
        if (!_cells.TryGetValue(cellKey, out Texture2D? cell))
        {
            cell = new AtlasTexture { Atlas = strip, Region = new Rect2(0, index * size, size, size) };
            _cells[cellKey] = cell;
        }

        return cell;
    }

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
        texture.ResourceName = UiAudit.TextPrefix + text; // tells the layout audit that this picture is text (ADR 0200)
        if (_texts.Count > 4096)
        {
            ClearTexts();
        }

        _texts[key] = texture;
        return texture;
    }

    /// <summary>
    /// The original bitmap font <paramref name="font"/> as a Godot font (UI-F1), so that Godot's own controls (text fields,
    /// lists, chat) write in it: one glyph per character of the strip, at the font's cell height. Null when missing.
    /// </summary>
    public FontFile? GodotFont(string font = SmallFont)
    {
        if (_godotFonts.TryGetValue(font, out FontFile? cached))
        {
            return cached;
        }

        FontFile? result = null;
        if (LoadFont(font) is { } loaded)
        {
            (FontAsset asset, Image strip) = loaded;
            int size = asset.CellHeight, ascent = Math.Max(1, asset.CellHeight - 3);
            var cache = new Vector2I(size, 0);
            result = new FontFile { FixedSize = size, Antialiasing = TextServer.FontAntialiasing.None, ResourceName = font };
            result.SetTextureImage(0, cache, 0, strip);
            result.SetCacheAscent(0, size, ascent);
            result.SetCacheDescent(0, size, size - ascent);
            foreach (FontCharacter c in asset.Characters)
            {
                int width = Math.Min(c.Width, asset.CellWidth);
                result.SetGlyphAdvance(0, size, c.Unicode, new Vector2(c.Width, 0));
                result.SetGlyphOffset(0, cache, c.Unicode, new Vector2(0, -ascent));
                result.SetGlyphSize(0, cache, c.Unicode, new Vector2(width, asset.CellHeight));
                result.SetGlyphUVRect(0, cache, c.Unicode, new Rect2(0, c.Glyph * asset.CellHeight, width, asset.CellHeight));
                result.SetGlyphTextureIdx(0, cache, c.Unicode, 0);
            }
        }

        _godotFonts[font] = result;
        return result;
    }

    /// <summary>
    /// A theme for Godot's controls on the original's screens (UI-F1): the original's small font in the screens' light
    /// gold, no boxes of Godot's own (the fields and lists sit on the art's bevels), a faint gold bar for a selection.
    /// </summary>
    public Theme ScreenTheme()
    {
        if (_theme is not null)
        {
            return _theme;
        }

        var theme = new Theme();
        if (GodotFont(SmallFont) is { } font)
        {
            theme.DefaultFont = font;
            theme.DefaultFontSize = font.FixedSize;
            foreach (string type in new[] { "Label", "LineEdit", "ItemList", "Button" })
            {
                theme.SetFontSize("font_size", type, font.FixedSize);
            }

            theme.SetFontSize("normal_font_size", "RichTextLabel", font.FixedSize);
            theme.SetConstant("line_spacing", "Label", 1);
            theme.SetStylebox("normal", "Label", new StyleBoxEmpty());
        }

        var gold = new Color(1f, 0.92f, 0.65f);
        var none = new StyleBoxEmpty();
        var picked = new StyleBoxFlat { BgColor = new Color(1f, 0.85f, 0.4f, 0.25f) };
        foreach (string type in new[] { "LineEdit", "ItemList", "RichTextLabel", "Label", "Button" })
        {
            theme.SetColor("font_color", type, gold);
        }

        theme.SetColor("default_color", "RichTextLabel", gold);
        theme.SetColor("font_placeholder_color", "LineEdit", new Color(0.7f, 0.65f, 0.5f));
        theme.SetColor("caret_color", "LineEdit", gold);
        theme.SetColor("font_selected_color", "ItemList", Colors.White);
        theme.SetStylebox("normal", "LineEdit", none);
        theme.SetStylebox("focus", "LineEdit", none);
        theme.SetStylebox("read_only", "LineEdit", none);
        theme.SetStylebox("panel", "ItemList", none);
        theme.SetStylebox("focus", "ItemList", none);
        theme.SetStylebox("selected", "ItemList", picked);
        theme.SetStylebox("selected_focus", "ItemList", picked);
        theme.SetStylebox("cursor", "ItemList", none);
        theme.SetStylebox("cursor_unfocused", "ItemList", none);
        theme.SetStylebox("normal", "RichTextLabel", none);
        theme.SetStylebox("focus", "RichTextLabel", none);
        _theme = theme;
        return theme;
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
