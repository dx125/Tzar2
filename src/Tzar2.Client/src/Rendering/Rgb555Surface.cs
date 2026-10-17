using System;
using System.Collections.Generic;
using Godot;
using RS = Godot.RenderingServer;

namespace Tzar2.Client.Rendering;

/// <summary>
/// A rectangle of RGB5A1 pixels with one or more animation frames (composed terrain, raw terrain bitmaps), uploaded
/// losslessly as an RG8 <c>Texture2DArray</c> (one layer per frame) and expanded in <c>shaders/rgb555.gdshader</c>.
/// </summary>
public sealed class Rgb555Surface : IDisposable
{
    private static ImageTexture? s_white;
    private readonly Rid _item;
    private readonly ShaderMaterial _material;
    private Texture2DArray? _frames;
    private int _frameCount = 1;

    /// <summary>Creates the canvas item under <paramref name="parent"/>.</summary>
    public Rgb555Surface(Rid parent, int drawIndex)
    {
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/rgb555.gdshader") };
        _item = RS.CanvasItemCreate();
        RS.CanvasItemSetParent(_item, parent);
        RS.CanvasItemSetDrawIndex(_item, drawIndex);
        RS.CanvasItemSetMaterial(_item, _material.GetRid());
        RS.CanvasItemSetDefaultTextureFilter(_item, RS.CanvasItemTextureFilter.Nearest);
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>Uploads frames of <paramref name="width"/> × <paramref name="height"/> little-endian RGB5A1 words, drawn at <paramref name="position"/> (logical pixels).</summary>
    public void Set(int width, int height, IReadOnlyList<byte[]> frames, Vector2I position, bool showKeys)
    {
        ArgumentNullException.ThrowIfNull(frames);
        _frames?.Dispose();
        var images = new Godot.Collections.Array<Image>();
        foreach (byte[] frame in frames)
        {
            images.Add(Image.CreateFromData(width, height, false, Image.Format.Rg8, frame));
        }

        _frames = new Texture2DArray();
        Error error = _frames.CreateFromImages(images);
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"RGB555 surface upload failed: {error}.");
        }

        Width = width;
        Height = height;
        _frameCount = frames.Count;
        _material.SetShaderParameter("frames", _frames);
        _material.SetShaderParameter("size", new Vector2(width, height));
        _material.SetShaderParameter("show_keys", showKeys);
        _material.SetShaderParameter("frame", 0);
        s_white ??= ImageTexture.CreateFromImage(Image.CreateEmpty(1, 1, false, Image.Format.Rgba8));
        RS.CanvasItemClear(_item);
        RS.CanvasItemAddTextureRect(_item, new Rect2(position, new Vector2(width, height)), s_white.GetRid());
    }

    /// <summary>Shows animation frame <paramref name="frame"/> (modulo the frame count).</summary>
    public void SetFrame(int frame) => _material.SetShaderParameter("frame", ((frame % _frameCount) + _frameCount) % _frameCount);

    /// <summary>Sets the canvas transform.</summary>
    public void SetView(Transform2D view) => RS.CanvasItemSetTransform(_item, view);

    /// <summary>Shows or hides the surface.</summary>
    public void SetVisible(bool value) => RS.CanvasItemSetVisible(_item, value);

    /// <inheritdoc/>
    public void Dispose()
    {
        _frames?.Dispose();
        _frames = null;
        RS.FreeRid(_item);
        _material.Dispose();
    }
}
