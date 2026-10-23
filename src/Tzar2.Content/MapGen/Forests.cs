using System;
using Tzar2.Core.Rng;

namespace Tzar2.Content.MapGen;

/// <summary>
/// The original's forests of a random map (WORLD-013, ADR 0201), as ReTzar reads its generator (0x4b2ab0 for the
/// trees, the plasma field it samples): a 512 × 512 midpoint-displacement field decides where forests stand, and
/// <c>TERRAIN.INI [randomtreeset]</c> gives, by map side, the field's roughness and the chances of a tree inside a forest,
/// outside it and astray. The facts are ReTzar's reading; the code and its random numbers are Tzar2's.
/// </summary>
public static class Forests
{
    /// <summary>The field's side (the original's, whatever the map's).</summary>
    public const int FieldSide = 512;

    /// <summary>
    /// One row of <c>[randomtreeset]</c>: the forest band of the field (<paramref name="Low"/>-<paramref name="High"/>),
    /// the field's roughness and divisor, the stray chance in 10,000, the divisors of the chance inside the band and above
    /// <paramref name="Dense"/> (a tree with about 1 / (100 / divisor) chance), and the dense threshold.
    /// </summary>
    /// <param name="Low">Lowest field value of the forest band.</param>
    /// <param name="High">Highest field value of the forest band.</param>
    /// <param name="Roughness">The field's roughness.</param>
    /// <param name="Divisor">The field's divisor.</param>
    /// <param name="Stray">A tree's chance in 10,000 outside the band.</param>
    /// <param name="Sparse">The chance divisor inside the band, up to <paramref name="Dense"/>.</param>
    /// <param name="Thick">The chance divisor above <paramref name="Dense"/>.</param>
    /// <param name="Dense">The field value above which forests are thick.</param>
    public readonly record struct TreeSettings(int Low, int High, int Roughness, int Divisor, int Stray, int Sparse, int Thick, int Dense);

    /// <summary>
    /// <c>[randomtreeset]</c> by map side; the same in the four terrain sets of GOG v1.10 (numbers restated from the
    /// data, REQ-LEGAL-01).
    /// </summary>
    public static TreeSettings ForSide(int side) => side switch
    {
        <= 64 => new(1, 255, 11, 5, 1, 1, 40, 205),
        <= 96 => new(1, 255, 11, 5, 1, 1, 40, 235),
        <= 128 => new(1, 255, 19, 5, 1, 1, 40, 234),
        <= 192 => new(1, 255, 15, 5, 1, 1, 40, 230),
        <= 256 => new(1, 255, 11, 5, 1, 1, 40, 235),
        <= 384 => new(1, 255, 20, 5, 1, 1, 30, 223),
        _ => new(1, 255, 25, 5, 1, 1, 30, 214),
    };

    /// <summary>
    /// The plasma field: the four corners random in 0-255, then every rectangle split at its middle, each new point the
    /// mean of its ends plus a random offset of up to ± extent × roughness / divisor, set once. As ReTzar reads it, a
    /// value below 0 wraps and so ends at 255, like one above.
    /// </summary>
    public static byte[] Field(ulong seed, int roughness, int divisor)
    {
        var rng = Xoshiro256StarStar.FromSeed(seed);
        const int n = FieldSide;
        int[] a = new int[n * n];
        const int Set = 0x8000;
        int rough = Math.Clamp(roughness, 1, 255), div = Math.Clamp(divisor, 1, 255);
        foreach (int i in new[] { 0, n - 1, (n * n) - 1, (n - 1) * n })
        {
            a[i] = rng.NextInt(256);
        }

        void Put(int x, int y, int mean, int amplitude)
        {
            int i = (y * n) + x;
            if ((a[i] & Set) != 0)
            {
                return;
            }

            int v = mean + rng.NextInt((amplitude * 2) + 1) - amplitude;
            a[i] = (v < 0 || v > 255 ? 255 : v) | Set;
        }

        int At(int x, int y) => a[(y * n) + x] & 0x7FFF;

        // An explicit stack instead of recursion, in the same order (x0, y0, x1, y1).
        var stack = new System.Collections.Generic.Stack<(int X0, int Y0, int X1, int Y1)>();
        stack.Push((0, 0, n - 1, n - 1));
        while (stack.Count > 0)
        {
            (int x0, int y0, int x1, int y1) = stack.Pop();
            int dx = x1 - x0, dy = y1 - y0;
            if (dx == 1)
            {
                if (dy != 1)
                {
                    int amp = 2 * rough / div;
                    Put(x0, y0 + 1, (At(x0, y0) + At(x0, y0 + 2)) / 2, amp);
                    Put(x1, y0 + 1, (At(x1, y0) + At(x1, y0 + 2)) / 2, amp);
                }

                continue;
            }

            int x = x0 + (dx / 2), y = y0 + (dy / 2);
            int ax = dx * rough / div, ay = dy * rough / div;
            Put(x1, y, (At(x1, y0) + At(x1, y1)) / 2, ay);
            Put(x0, y, (At(x0, y0) + At(x0, y1)) / 2, ay);
            Put(x, y0, (At(x0, y0) + At(x1, y0)) / 2, ax);
            Put(x, y1, (At(x0, y1) + At(x1, y1)) / 2, ax);
            Put(x, y, (At(x0, y0) + At(x1, y0) + At(x1, y1) + At(x0, y1)) / 4, ax);

            // Pushed in reverse so that they are split in the original's order.
            stack.Push((x0, y, x, y1));
            stack.Push((x, y, x1, y1));
            stack.Push((x, y0, x1, y));
            stack.Push((x0, y0, x, y));
        }

        byte[] field = new byte[n * n];
        for (int i = 0; i < field.Length; i++)
        {
            field[i] = (byte)a[i];
        }

        return field;
    }

    /// <summary>The field's value under tile (<paramref name="x"/>, <paramref name="y"/>) of a map of side <paramref name="side"/>.</summary>
    public static int Sample(byte[] field, int side, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field[(Math.Min(y, side - 1) * FieldSide / side * FieldSide) + (Math.Min(x, side - 1) * FieldSide / side)];
    }

    /// <summary>
    /// Whether a tree grows on a tile whose field value is <paramref name="value"/>, where <paramref name="pictures"/> tree
    /// pictures may grow (the kinds of <c>[treeterrain]</c>): inside the band one draw in 0..n·k with k = 100 / divisor
    /// (thick above the dense threshold), outside it a stray draw in 10,000 first.
    /// </summary>
    public static bool Grows(in TreeSettings s, int value, int pictures, ref Xoshiro256StarStar rng)
    {
        if (pictures <= 0)
        {
            return false;
        }

        int k;
        if (value >= s.Low && value <= s.High)
        {
            k = 100 / Math.Max(1, value > s.Dense ? s.Thick : s.Sparse);
        }
        else if (rng.NextInt(10_001) <= s.Stray)
        {
            k = 1;
        }
        else
        {
            return false;
        }

        return rng.NextInt((pictures * Math.Max(0, k)) + 1) <= pictures;
    }
}
