using System;
using Godot;

namespace Tzar2.Spikes.S1;

/// <summary>
/// Procedurally generated placeholder sprites (no original assets, DEC-05). Every frame is a 48x48 cell with a colour
/// plane (RGBA8, colours quantised to RGB555 with bit replication, TDD §13.2) and a separate R8 player-colour mask
/// plane (layout (a) of REQ-GFX-03). The same frames are exposed as a <see cref="Texture2DArray"/> (one frame per layer)
/// and as a 2D atlas (16x16 grid) so the two layouts can be compared.
/// </summary>
public sealed class SpriteSheet
{
    public const int Cell = 48;
    public const int Kinds = 8;
    public const int Directions = 8;
    public const int AnimFrames = 4;
    public const int FrameCount = Kinds * Directions * AnimFrames;
    public const int AtlasColumns = 16;
    public const int AtlasRows = FrameCount / AtlasColumns;
    public const int PivotX = Cell / 2;
    public const int PivotY = Cell - 4;
    public const int Players = 8;
    public const int Shades = 8;
    public const int GroundTile = 64;

    private readonly byte[][] _color = new byte[FrameCount][];
    private readonly byte[][] _mask = new byte[FrameCount][];
    private readonly byte[] _ramps = new byte[Shades * Players * 4];
    private readonly byte[] _ground = new byte[GroundTile * GroundTile * 4];

    public SpriteSheet()
    {
        for (int k = 0; k < Kinds; k++)
        {
            for (int d = 0; d < Directions; d++)
            {
                for (int a = 0; a < AnimFrames; a++)
                {
                    int f = FrameIndex(k, d, a);
                    _color[f] = new byte[Cell * Cell * 4];
                    _mask[f] = new byte[Cell * Cell];
                    DrawFrame(k, d, a, _color[f], _mask[f]);
                }
            }
        }

        BuildRamps();
        BuildGround();
    }

    public static int FrameIndex(int kind, int dir, int anim) => (((kind * Directions) + dir) * AnimFrames) + anim;

    /// <summary>Expands a 5-bit channel to 8 bits by bit replication (TDD §13.2).</summary>
    public static byte Expand5(int c5) => (byte)((c5 << 3) | (c5 >> 2));

    /// <summary>Quantises an 8-bit channel to RGB555 and expands it back, so every colour is an exact RGB555 colour.</summary>
    public static byte Q(int c8) => Expand5(Math.Clamp(c8, 0, 255) >> 3);

    /// <summary>CPU reference of what the shader must output for one texel (used by the pixel-exactness check).</summary>
    public bool TryGetTexel(int frame, int x, int y, int player, out byte r, out byte g, out byte b)
    {
        byte[] c = _color[frame];
        int i = (y * Cell) + x;
        r = g = b = 0;
        if (c[(i * 4) + 3] < 128)
        {
            return false;
        }

        int m = _mask[frame][i];
        if (m > 0)
        {
            int ri = (((player * Shades) + (m - 1)) * 4);
            r = _ramps[ri];
            g = _ramps[ri + 1];
            b = _ramps[ri + 2];
        }
        else
        {
            r = c[i * 4];
            g = c[(i * 4) + 1];
            b = c[(i * 4) + 2];
        }

        return true;
    }

    public Texture2DArray CreateColorArray() => CreateArray(_color, Image.Format.Rgba8);

    public Texture2DArray CreateMaskArray() => CreateArray(_mask, Image.Format.R8);

    public ImageTexture CreateColorAtlas() => CreateAtlas(_color, Image.Format.Rgba8, 4);

    public ImageTexture CreateMaskAtlas() => CreateAtlas(_mask, Image.Format.R8, 1);

    public ImageTexture CreateRampTexture()
        => ImageTexture.CreateFromImage(Image.CreateFromData(Shades, Players, false, Image.Format.Rgba8, _ramps));

    public ImageTexture CreateGroundTexture()
        => ImageTexture.CreateFromImage(Image.CreateFromData(GroundTile, GroundTile, false, Image.Format.Rgba8, _ground));

    private static Texture2DArray CreateArray(byte[][] planes, Image.Format format)
    {
        var images = new Godot.Collections.Array<Image>();
        foreach (byte[] plane in planes)
        {
            images.Add(Image.CreateFromData(Cell, Cell, false, format, plane));
        }

        var array = new Texture2DArray();
        Error err = array.CreateFromImages(images);
        if (err != Error.Ok)
        {
            throw new InvalidOperationException("Texture2DArray.CreateFromImages failed: " + err);
        }

        return array;
    }

    private static ImageTexture CreateAtlas(byte[][] planes, Image.Format format, int bpp)
    {
        int w = AtlasColumns * Cell;
        int h = AtlasRows * Cell;
        byte[] atlas = new byte[w * h * bpp];
        for (int f = 0; f < planes.Length; f++)
        {
            int ox = (f % AtlasColumns) * Cell;
            int oy = (f / AtlasColumns) * Cell;
            for (int y = 0; y < Cell; y++)
            {
                Buffer.BlockCopy(planes[f], y * Cell * bpp, atlas, (((oy + y) * w) + ox) * bpp, Cell * bpp);
            }
        }

        return ImageTexture.CreateFromImage(Image.CreateFromData(w, h, false, format, atlas));
    }

    private void BuildRamps()
    {
        // Placeholder ramps (the real ones come from original data, REQ-GFX-04): 8 hues x 8 brightness steps.
        (int R, int G, int B)[] hues =
        [
            (230, 40, 40), (50, 90, 235), (40, 190, 60), (240, 220, 50),
            (40, 210, 220), (210, 60, 210), (245, 140, 30), (235, 235, 235),
        ];
        for (int p = 0; p < Players; p++)
        {
            for (int s = 0; s < Shades; s++)
            {
                float f = 0.35f + (0.65f * s / (Shades - 1));
                int i = ((p * Shades) + s) * 4;
                _ramps[i] = Q((int)(hues[p].R * f));
                _ramps[i + 1] = Q((int)(hues[p].G * f));
                _ramps[i + 2] = Q((int)(hues[p].B * f));
                _ramps[i + 3] = 255;
            }
        }
    }

    private void BuildGround()
    {
        uint seed = 12345;
        for (int y = 0; y < GroundTile; y++)
        {
            for (int x = 0; x < GroundTile; x++)
            {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                int n = (int)(seed % 24);
                int i = ((y * GroundTile) + x) * 4;
                _ground[i] = Q(60 + n);
                _ground[i + 1] = Q(110 + n + ((x / 8 + y / 8) % 2 * 6));
                _ground[i + 2] = Q(45 + (n / 2));
                _ground[i + 3] = 255;
            }
        }
    }

    private static void DrawFrame(int kind, int dir, int anim, byte[] color, byte[] mask)
    {
        int bob = (anim % 2 == 1) ? -1 : 0;
        int foot = PivotY + bob;
        int bodyH = 10 + (kind * 2);
        int bodyW = 10 + ((kind % 4) * 3);
        int legH = 5;
        int cx = PivotX;

        // Legs, alternating stride per animation frame.
        int stride = anim switch { 1 => 2, 3 => -2, _ => 0 };
        FillRect(color, cx - 4 + stride, foot - legH, 3, legH, Q(70), Q(50), Q(30));
        FillRect(color, cx + 1 - stride, foot - legH, 3, legH, Q(70), Q(50), Q(30));

        // Tunic: player-coloured through the mask, vertical gradient plus a 1-px checker dither (shows pixel exactness).
        int top = foot - legH - bodyH;
        for (int y = 0; y < bodyH; y++)
        {
            for (int x = 0; x < bodyW; x++)
            {
                int px = cx - (bodyW / 2) + x;
                int py = top + y;
                int shade = Math.Clamp(Shades - 1 - (y * (Shades - 2) / bodyH) - ((px + py) & 1), 0, Shades - 1);
                SetPixel(color, px, py, Q(128), Q(128), Q(128));
                mask[(py * Cell) + px] = (byte)(shade + 1);
            }
        }

        // Kind-specific belt (not player-coloured) so kinds are distinguishable.
        byte br = Q(40 + (kind * 25)), bg = Q(200 - (kind * 20)), bb = Q(90 + ((kind * 37) % 120));
        FillRect(color, cx - (bodyW / 2), top + (bodyH * 2 / 3), bodyW, 2, br, bg, bb);
        for (int x = cx - (bodyW / 2); x < cx - (bodyW / 2) + bodyW; x++)
        {
            for (int y = top + (bodyH * 2 / 3); y < top + (bodyH * 2 / 3) + 2; y++)
            {
                mask[(y * Cell) + x] = 0;
            }
        }

        // Head.
        int hy = top - 4;
        for (int y = -4; y <= 4; y++)
        {
            for (int x = -4; x <= 4; x++)
            {
                if ((x * x) + (y * y) <= 16)
                {
                    SetPixel(color, cx + x, hy + y, Q(235), Q(190), Q(150));
                }
            }
        }

        // Facing indicator: a spear in one of the 8 directions (0 = east, counter-clockwise).
        double angle = dir * Math.PI / 4.0;
        for (int t = 3; t <= 16; t++)
        {
            int px = cx + (int)Math.Round(Math.Cos(angle) * t);
            int py = (top + (bodyH / 2)) - (int)Math.Round(Math.Sin(angle) * t);
            SetPixel(color, px, py, Q(200), Q(200), Q(215));
            if (px >= 0 && px < Cell && py >= 0 && py < Cell)
            {
                mask[(py * Cell) + px] = 0;
            }
        }

        // 1-px black outline around every opaque pixel.
        byte[] alpha = new byte[Cell * Cell];
        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = color[(i * 4) + 3];
        }

        for (int y = 0; y < Cell; y++)
        {
            for (int x = 0; x < Cell; x++)
            {
                if (alpha[(y * Cell) + x] != 0)
                {
                    continue;
                }

                bool edge = (x > 0 && alpha[(y * Cell) + x - 1] != 0) || (x < Cell - 1 && alpha[(y * Cell) + x + 1] != 0)
                    || (y > 0 && alpha[((y - 1) * Cell) + x] != 0) || (y < Cell - 1 && alpha[((y + 1) * Cell) + x] != 0);
                if (edge)
                {
                    SetPixel(color, x, y, 0, 0, 0);
                }
            }
        }
    }

    private static void FillRect(byte[] color, int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                SetPixel(color, x, y, r, g, b);
            }
        }
    }

    private static void SetPixel(byte[] color, int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || x >= Cell || y < 0 || y >= Cell)
        {
            return;
        }

        int i = ((y * Cell) + x) * 4;
        color[i] = r;
        color[i + 1] = g;
        color[i + 2] = b;
        color[i + 3] = 255;
    }
}
