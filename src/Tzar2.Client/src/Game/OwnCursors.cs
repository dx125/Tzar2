using System;
using System.Collections.Generic;
using Godot;

namespace Tzar2.Client.Game;

/// <summary>
/// Cursors the original does not have (its <c>IMAGES/CURSORS</c> hold one harvest cursor, a shovel): a pickaxe over gold
/// and stone and a saw over trees (play-test of 2026-10-20), and a pointer for when no order is pending (second play-test:
/// the original's hand is for taking things). They are Tzar2's own art, painted at run time from a few
/// shapes in the style of the original cursors: flat colours inside a one-pixel black outline.
/// </summary>
public static class OwnCursors
{
    /// <summary>Pickaxe: gold and stone.</summary>
    public const string Mine = "x_mine";

    /// <summary>Saw: trees.</summary>
    public const string Saw = "x_saw";

    /// <summary>The pointer: nothing to do under it.</summary>
    public const string Arrow = "x_arrow";

    /// <summary>Canvas size in pixels.</summary>
    public const int Size = 24;

    private static readonly Color Metal = Color.Color8(214, 214, 206);
    private static readonly Color Steel = Color.Color8(140, 140, 136);
    private static readonly Color Wood = Color.Color8(150, 96, 48);
    private static readonly Color DarkWood = Color.Color8(96, 60, 28);
    private static readonly Color Gold = Color.Color8(222, 176, 70);
    private static readonly Color PaleGold = Color.Color8(250, 222, 140);

    private static readonly Shape[] Pickaxe =
    [
        new Capsule(Wood, new(15.5f, 8.5f), new(4.5f, 19.5f), 1.25f),
        new Capsule(DarkWood, new(15.5f, 9.5f), new(5.5f, 19.5f), 0.5f),
        new Polygon(Metal, [new(3, 6), new(9, 2.5f), new(15, 2.5f), new(21, 7), new(22.5f, 13), new(19.5f, 9), new(14.5f, 5.5f), new(9, 5.5f)]),
        new Polygon(Steel, [new(9, 5.5f), new(14.5f, 5.5f), new(19.5f, 9), new(22.5f, 13), new(21, 11), new(15, 7), new(9, 6.5f)]),
    ];

    private static readonly Shape[] HandSaw =
    [
        new Polygon(Metal, [new(1.5f, 3), new(5, 1), new(18, 11.5f), new(13, 16.5f)]),
        new Pixels(Metal, [new(1, 5), new(3, 7), new(4, 9), new(6, 11), new(8, 13), new(9, 15), new(11, 16)]),
        new Polygon(Steel, [new(5, 1), new(18, 11.5f), new(17, 12.5f), new(4.5f, 2.2f)]),
        new Polygon(Wood, [new(15, 11), new(18, 9.5f), new(22.5f, 15), new(21.5f, 20.5f), new(16.5f, 21.5f), new(12.5f, 17)]),
        new Hole([new(16.5f, 13.5f), new(18, 12.5f), new(20.5f, 15.5f), new(19.5f, 18.5f), new(17.5f, 18.5f), new(15.5f, 15.5f)]),
    ];

    private static readonly Shape[] Pointer =
    [
        new Polygon(Gold, [new(1.5f, 1.5f), new(1.5f, 17.5f), new(5.5f, 13.5f), new(8.5f, 20.5f), new(11, 19.5f), new(8.5f, 12.5f), new(13.5f, 12.5f)]),
        new Polygon(PaleGold, [new(2.5f, 3.5f), new(2.5f, 13.5f), new(4.5f, 11.5f), new(4.5f, 5.5f)]),
    ];

    /// <summary>Whether <paramref name="name"/> is one of these cursors.</summary>
    public static bool Has(string name) => name is Mine or Saw or Arrow;

    /// <summary>The point that clicks: the pick's tip, the saw's tip, the pointer's tip.</summary>
    public static Vector2 Hotspot(string name) => name switch
    {
        Mine => new Vector2(3, 6),
        Arrow => new Vector2(1, 1),
        _ => new Vector2(2, 3),
    };

    /// <summary>Paints cursor <paramref name="name"/>, or returns null for an unknown name.</summary>
    public static Image? Paint(string name) => name switch
    {
        Mine => Render(Pickaxe),
        Saw => Render(HandSaw),
        Arrow => Render(Pointer),
        _ => null,
    };

    private static Image Render(IReadOnlyList<Shape> shapes)
    {
        // Fill: the last shape covering a pixel centre wins; a hole clears. Then a black outline around the filled pixels.
        var fill = new Color?[Size, Size];
        foreach (Shape shape in shapes)
        {
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    if (!shape.Covers(p, x, y))
                    {
                        continue;
                    }

                    fill[x, y] = shape is Hole ? null : shape.Colour;
                }
            }
        }

        Image image = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (fill[x, y] is { } c)
                {
                    image.SetPixel(x, y, c);
                }
                else if (Filled(fill, x + 1, y) || Filled(fill, x - 1, y) || Filled(fill, x, y + 1) || Filled(fill, x, y - 1))
                {
                    image.SetPixel(x, y, Colors.Black);
                }
            }
        }

        return image;
    }

    private static bool Filled(Color?[,] fill, int x, int y) => x >= 0 && y >= 0 && x < Size && y < Size && fill[x, y] is not null;

    private abstract record Shape(Color Colour)
    {
        public abstract bool Covers(Vector2 centre, int x, int y);
    }

    private sealed record Capsule(Color Colour, Vector2 A, Vector2 B, float Radius) : Shape(Colour)
    {
        public override bool Covers(Vector2 centre, int x, int y)
        {
            Vector2 ab = B - A;
            float t = ab.LengthSquared() == 0 ? 0 : Math.Clamp((centre - A).Dot(ab) / ab.LengthSquared(), 0f, 1f);
            return centre.DistanceTo(A + (ab * t)) <= Radius;
        }
    }

    private record Polygon(Color Colour, Vector2[] Points) : Shape(Colour)
    {
        public override bool Covers(Vector2 centre, int x, int y)
        {
            bool inside = false;
            for (int i = 0, j = Points.Length - 1; i < Points.Length; j = i++)
            {
                Vector2 a = Points[i], b = Points[j];
                if ((a.Y > centre.Y) != (b.Y > centre.Y) && centre.X < ((b.X - a.X) * (centre.Y - a.Y) / (b.Y - a.Y)) + a.X)
                {
                    inside = !inside;
                }
            }

            return inside;
        }
    }

    private sealed record Hole(Vector2[] Points) : Polygon(Colors.Transparent, Points);

    private sealed record Pixels(Color Colour, Vector2I[] At) : Shape(Colour)
    {
        public override bool Covers(Vector2 centre, int x, int y) => Array.IndexOf(At, new Vector2I(x, y)) >= 0;
    }
}
