using System;
using Godot;
using RS = Godot.RenderingServer;

namespace Tzar2.Client.Viewer;

/// <summary>
/// The viewer's world area: a root canvas item on the main canvas (under the UI's <c>CanvasLayer</c>), a background,
/// and the view transform shared by all pages: <c>screen = origin + zoom × (logical − pan)</c> with an integer zoom
/// (REQ-GFX-07: integer scaling, nearest sampling) and whole-pixel pan. Text labels are drawn in screen space.
/// </summary>
public sealed class WorldView : IDisposable
{
    /// <summary>Draw index of the background.</summary>
    public const int DrawBackground = 0;

    /// <summary>Draw index of RGB555 surfaces (terrain).</summary>
    public const int DrawSurfaces = 10;

    /// <summary>Draw index of the sprite renderer (uses two consecutive indices: shadow composite, objects).</summary>
    public const int DrawSprites = 20;

    /// <summary>Draw index of font glyphs.</summary>
    public const int DrawGlyphs = 30;

    /// <summary>Draw index of labels.</summary>
    public const int DrawLabels = 40;

    /// <summary>Largest zoom factor.</summary>
    public const int MaxZoom = 8;

    private readonly Rid _background;
    private readonly Rid _labels;
    private Color _backgroundColour = new(0.29f, 0.36f, 0.23f);
    private Vector2I _screen;

    /// <summary>Creates the items on <paramref name="canvas"/>.</summary>
    public WorldView(Rid canvas)
    {
        Root = RS.CanvasItemCreate();
        RS.CanvasItemSetParent(Root, canvas);
        _background = RS.CanvasItemCreate();
        RS.CanvasItemSetParent(_background, Root);
        RS.CanvasItemSetDrawIndex(_background, DrawBackground);
        _labels = RS.CanvasItemCreate();
        RS.CanvasItemSetParent(_labels, Root);
        RS.CanvasItemSetDrawIndex(_labels, DrawLabels);
    }

    /// <summary>Parent of every world item.</summary>
    public Rid Root { get; }

    /// <summary>Screen position of logical (0, 0) at zero pan.</summary>
    public Vector2I Origin { get; set; } = new(360, 40);

    /// <summary>Integer zoom (1 = pixel-exact 1:1).</summary>
    public int Zoom { get; private set; } = 1;

    /// <summary>Logical pixel shown at <see cref="Origin"/>.</summary>
    public Vector2I Pan { get; set; }

    /// <summary>Raised when zoom or pan change.</summary>
    public event Action? Changed;

    /// <summary>The canvas transform for world items.</summary>
    public Transform2D Transform => new(new Vector2(Zoom, 0), new Vector2(0, Zoom), (Vector2)(Origin - (Pan * Zoom)));

    /// <summary>Screen position of a logical point.</summary>
    public Vector2 ToScreen(Vector2I logical) => (Vector2)(Origin + ((logical - Pan) * Zoom));

    /// <summary>Sets the zoom (clamped to 1…<see cref="MaxZoom"/>).</summary>
    public void SetZoom(int zoom)
    {
        Zoom = Math.Clamp(zoom, 1, MaxZoom);
        Changed?.Invoke();
    }

    /// <summary>Moves the view by <paramref name="delta"/> logical pixels.</summary>
    public void Scroll(Vector2I delta)
    {
        Pan += delta;
        Changed?.Invoke();
    }

    /// <summary>Resets pan to the origin.</summary>
    public void ResetPan()
    {
        Pan = Vector2I.Zero;
        Changed?.Invoke();
    }

    /// <summary>Background colour.</summary>
    public Color BackgroundColour
    {
        get => _backgroundColour;
        set
        {
            _backgroundColour = value;
            Redraw();
        }
    }

    /// <summary>Call when the window size changes.</summary>
    public void Resize(Vector2I screen)
    {
        if (screen != _screen)
        {
            _screen = screen;
            Redraw();
        }
    }

    /// <summary>Removes all labels.</summary>
    public void ClearLabels() => RS.CanvasItemClear(_labels);

    /// <summary>Draws <paramref name="text"/> with its baseline at screen position <paramref name="screen"/> (UI font, not original art).</summary>
    public void Label(Vector2 screen, string text, Color colour, int size = 13)
        => ThemeDB.FallbackFont.DrawString(_labels, screen, text, HorizontalAlignment.Left, -1, size, colour);

    /// <inheritdoc/>
    public void Dispose()
    {
        RS.FreeRid(_labels);
        RS.FreeRid(_background);
        RS.FreeRid(Root);
    }

    private void Redraw()
    {
        RS.CanvasItemClear(_background);
        RS.CanvasItemAddRect(_background, new Rect2(Vector2.Zero, _screen), _backgroundColour);
    }
}
