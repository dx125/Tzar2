using System;
using Tzar2.Core.Rng;

namespace Tzar2.Content.MapGen;

/// <summary>
/// Integer value noise for the generator: lattice values from a hash of (salt, cell), smoothstep-interpolated in
/// 1/4,096 steps, summed over octaves. The salt is drawn from the <c>mapgen</c> stream, so the noise is part of it.
/// </summary>
internal static class Noise
{
    /// <summary>Largest value <see cref="Fill"/> writes.</summary>
    public const int Max = 65535;

    private const int One = 4096;

    /// <summary>
    /// Fills <paramref name="field"/> (row-major <paramref name="width"/> × <paramref name="height"/>) with fractal noise
    /// in 0…<see cref="Max"/>: <paramref name="octaves"/> layers, the first with cells of <paramref name="cell"/>, each
    /// next one half the size and half the weight.
    /// </summary>
    public static void Fill(int[] field, int width, int height, ulong salt, int cell, int octaves)
    {
        Array.Clear(field, 0, width * height);
        int totalWeight = 0;
        int weight = 1 << octaves;
        for (int o = 0; o < octaves; o++)
        {
            int c = Math.Max(2, cell >> o);
            ulong s = SplitMix64.Mix(salt + (ulong)o);
            for (int y = 0; y < height; y++)
            {
                int gy = y / c;
                int wy = Smooth((y - (gy * c)) * One / c);
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int gx = x / c;
                    int wx = Smooth((x - (gx * c)) * One / c);
                    int v00 = Lattice(s, gx, gy), v10 = Lattice(s, gx + 1, gy);
                    int v01 = Lattice(s, gx, gy + 1), v11 = Lattice(s, gx + 1, gy + 1);
                    int top = v00 + (((v10 - v00) * wx) / One);
                    int bottom = v01 + (((v11 - v01) * wx) / One);
                    field[row + x] += (top + (((bottom - top) * wy) / One)) * weight;
                }
            }

            totalWeight += weight;
            weight >>= 1;
        }

        for (int i = 0; i < width * height; i++)
        {
            field[i] /= totalWeight;
        }
    }

    /// <summary>The value at lattice point (<paramref name="x"/>, <paramref name="y"/>), 0…<see cref="Max"/>.</summary>
    public static int Lattice(ulong salt, int x, int y)
        => (int)(SplitMix64.Mix(salt ^ (((ulong)(uint)x << 32) | (uint)y)) >> 48);

    // 3t² − 2t³ for t in 0…One.
    private static int Smooth(int t) => (int)((long)t * t * ((3 * One) - (2 * t)) / ((long)One * One));
}
