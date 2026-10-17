using System;

namespace Tzar2.Client.Logic;

/// <summary>
/// Stable LSD radix sort of 32-bit keys (painter's order, TDD §13.3, ADR 0010): sorts instance indices by key; equal
/// keys keep their input order, so callers append in entity-id order to get the <c>(…, entityId)</c> tie-break.
/// Allocation-free after the buffers have grown. Godot-free (unit-tested headless).
/// </summary>
public sealed class RadixSort
{
    private int[] _scratch = [];
    private readonly int[] _counts = new int[256];

    /// <summary>Writes into <paramref name="order"/> the indices 0…count−1 sorted by <paramref name="keys"/> (ascending, stable).</summary>
    public void Sort(ReadOnlySpan<uint> keys, Span<int> order)
    {
        int n = keys.Length;
        if (order.Length < n)
        {
            throw new ArgumentException("order is shorter than keys.", nameof(order));
        }

        if (_scratch.Length < n)
        {
            _scratch = new int[Math.Max(n, _scratch.Length * 2)];
        }

        for (int i = 0; i < n; i++)
        {
            order[i] = i;
        }

        Span<int> src = order[..n];
        Span<int> dst = _scratch.AsSpan(0, n);
        for (int shift = 0; shift < 32; shift += 8)
        {
            Array.Clear(_counts);
            for (int i = 0; i < n; i++)
            {
                _counts[(keys[src[i]] >> shift) & 0xFF]++;
            }

            if (n == 0 || _counts[(keys[src[0]] >> shift) & 0xFF] == n)
            {
                continue; // every key has the same digit: this pass would not move anything
            }

            int total = 0;
            for (int d = 0; d < 256; d++)
            {
                int c = _counts[d];
                _counts[d] = total;
                total += c;
            }

            for (int i = 0; i < n; i++)
            {
                int index = src[i];
                dst[_counts[(keys[index] >> shift) & 0xFF]++] = index;
            }

            Span<int> swap = src;
            src = dst;
            dst = swap;
        }

        if (!src.Overlaps(order))
        {
            src.CopyTo(order);
        }
    }
}
