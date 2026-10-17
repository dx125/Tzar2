using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using Tzar2.Content.Assets;
using RS = Godot.RenderingServer;

namespace Tzar2.Client.Viewer;

/// <summary>
/// The original bitmap fonts (<c>LOCAL/FONTS/*.FNT</c>, fnt.md): every font with sample text, and the full character
/// table of the selected one. Glyphs are drawn 1:1 from the imported strip (nearest, integer zoom).
/// </summary>
public sealed class FontPage : IViewerPage
{
    /// <summary>Sample text (ASCII + Windows-1252 letters).</summary>
    public const string Sample = "The quick brown fox jumps over the lazy dog 0123456789\nÀÉÎÕÜ àéîõü ßÆæØøÇç ñ €!?.,:;()[]+-*/%";

    private static readonly (string Name, Color Colour)[] Colours =
    [
        ("White", Color.Color8(255, 255, 255)),
        ("Gold", Color.Color8(255, 214, 90)),
        ("Black", Color.Color8(0, 0, 0)),
    ];

    private readonly AssetViewer _viewer;
    private readonly VBoxContainer _panel;
    private readonly ItemList _list;
    private readonly OptionButton _colour;
    private readonly RichTextLabel _info;
    private readonly Rid _item;
    private readonly List<(FontEntry Entry, FontAsset Font, ImageTexture Strip)> _fonts = [];
    private readonly List<(Vector2I At, string Text)> _labels = [];
    private int _selected;

    /// <summary>Creates the page.</summary>
    public FontPage(AssetViewer viewer)
    {
        _viewer = viewer;
        _panel = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 160) };
        _list.ItemSelected += i => SelectIndex((int)i);
        _panel.AddChild(_list);
        _colour = new OptionButton();
        foreach ((string name, _) in Colours)
        {
            _colour.AddItem(name);
        }

        _colour.ItemSelected += _ => Rebuild();
        _panel.AddChild(_colour);
        _info = new RichTextLabel { BbcodeEnabled = true, SelectionEnabled = true, CustomMinimumSize = new Vector2(0, 260), ScrollActive = true };
        _panel.AddChild(_info);

        _item = RS.CanvasItemCreate();
        RS.CanvasItemSetParent(_item, viewer.World.Root);
        RS.CanvasItemSetDrawIndex(_item, WorldView.DrawGlyphs);
        RS.CanvasItemSetDefaultTextureFilter(_item, RS.CanvasItemTextureFilter.Nearest);
        RS.CanvasItemSetVisible(_item, false);

        var errors = new StringBuilder();
        foreach (FontEntry entry in viewer.Store.Fonts)
        {
            try
            {
                FontAsset font = FontAsset.Parse(viewer.Store.Read(entry.Asset));
                ImageAsset strip = viewer.Store.LoadImage(font.Strip) ?? throw new FileNotFoundException(font.Strip);
                byte[] rgba = FontAsset.StripToRgba(strip, new Rgba32(255, 255, 255, 255));
                var texture = ImageTexture.CreateFromImage(Image.CreateFromData(strip.Width, strip.Height, false, Image.Format.Rgba8, rgba));
                _fonts.Add((entry, font, texture));
                _list.AddItem($"{Path.GetFileNameWithoutExtension(entry.Asset)}  {font.Face} {font.CellHeight}px");
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                errors.Append(CultureInfo.InvariantCulture, $"[color=#ff8080]{entry.Asset}: {e.Message}[/color]\n");
            }
        }

        _info.Text = errors.ToString();
        if (_fonts.Count > 0)
        {
            _list.Select(0);
        }

        Rebuild();
    }

    /// <inheritdoc/>
    public string Title => "Fonts";

    /// <inheritdoc/>
    public Control Panel => _panel;

    /// <summary>Number of fonts loaded.</summary>
    public int Count => _fonts.Count;

    /// <summary>Selects font <paramref name="index"/>.</summary>
    public void SelectIndex(int index)
    {
        _selected = Math.Clamp(index, 0, Math.Max(0, _fonts.Count - 1));
        Rebuild();
    }

    /// <inheritdoc/>
    public void SetActive(bool active)
    {
        RS.CanvasItemSetVisible(_item, active);
        if (!active)
        {
            _viewer.World.ClearLabels();
        }
    }

    /// <inheritdoc/>
    public void Update()
    {
        WorldView world = _viewer.World;
        RS.CanvasItemSetTransform(_item, world.Transform);
        world.ClearLabels();
        foreach ((Vector2I at, string text) in _labels)
        {
            world.Label(world.ToScreen(at) + new Vector2(0, -4), text, Color.Color8(200, 220, 255), 12);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        RS.FreeRid(_item);
        foreach ((_, _, ImageTexture strip) in _fonts)
        {
            strip.Dispose();
        }
    }

    private void Rebuild()
    {
        RS.CanvasItemClear(_item);
        _labels.Clear();
        Color colour = Colours[Math.Max(0, _colour.Selected)].Colour;
        int y = 0;
        foreach ((FontEntry entry, FontAsset font, ImageTexture strip) in _fonts)
        {
            _labels.Add((new Vector2I(0, y), $"{Path.GetFileNameWithoutExtension(entry.Asset)} ({font.Face}, cell {font.CellWidth}×{font.CellHeight})"));
            y += 4;
            Draw(font, strip, Sample, new Vector2I(0, y), colour);
            y += (font.CellHeight * 2) + 22;
        }

        if (_selected < _fonts.Count)
        {
            (FontEntry entry, FontAsset font, ImageTexture strip) = _fonts[_selected];
            _labels.Add((new Vector2I(0, y), $"{Path.GetFileNameWithoutExtension(entry.Asset)}: all characters (code page rows of 32)"));
            y += 4;
            var table = new StringBuilder();
            for (int code = 32; code < 256; code++)
            {
                FontCharacter? found = null;
                foreach (FontCharacter ch in font.Characters)
                {
                    if (ch.Code == code && ch.Width > 0)
                    {
                        found = ch;
                        break;
                    }
                }

                table.Append(found is { } f ? char.ConvertFromUtf32(f.Unicode) : " ");
                if (code % 32 == 31)
                {
                    table.Append('\n');
                }
            }

            Draw(font, strip, table.ToString(), new Vector2I(0, y), colour);
            _info.Text = $"{font.Face}: {font.Characters.Count} characters, {font.GlyphCount} glyphs, cell {font.CellWidth}×{font.CellHeight}\nstrip {font.Strip}";
        }
    }

    private void Draw(FontAsset font, ImageTexture strip, string text, Vector2I at, Color colour)
    {
        foreach (PlacedGlyph g in font.Layout(text))
        {
            RS.CanvasItemAddTextureRectRegion(
                _item,
                new Rect2(at.X + g.X, at.Y + g.Y, g.Width, font.CellHeight),
                strip.GetRid(),
                new Rect2(0, g.Glyph * font.CellHeight, g.Width, font.CellHeight),
                colour);
        }
    }
}
