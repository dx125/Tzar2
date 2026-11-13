using System;

namespace Tzar2.Sim.Navigation;

/// <summary>
/// A min-heap of (key, value) pairs ordered as <see cref="MinHeap"/> (by key, then value), for keys below 2^32 and values
/// that are not negative: each pair is one 64-bit number (key in the high half), so the order is one comparison and the
/// pop sequence is the same as <see cref="MinHeap"/>'s for the same pushes. A 4-ary heap.
/// </summary>
internal sealed class PackedMinHeap
{
    private ulong[] _entries;

    public PackedMinHeap(int capacity = 256) => _entries = new ulong[Math.Max(4, capacity)];

    public int Count { get; private set; }

    public void Clear() => Count = 0;

    public void Push(ulong key, int value)
    {
        if (key > uint.MaxValue || value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(key), "A packed heap holds keys below 2^32 and values not negative.");
        }

        ulong entry = (key << 32) | (uint)value;
        ulong[] entries = _entries;
        if (Count == entries.Length)
        {
            Array.Resize(ref _entries, entries.Length * 2);
            entries = _entries;
        }

        int i = Count++;
        while (i > 0)
        {
            int parent = (i - 1) >> 2;
            ulong p = entries[parent];
            if (p <= entry)
            {
                break;
            }

            entries[i] = p;
            i = parent;
        }

        entries[i] = entry;
    }

    public int Pop(out ulong key)
    {
        ulong[] entries = _entries;
        ulong top = entries[0];
        int n = --Count;
        if (n > 0)
        {
            ulong last = entries[n];
            int i = 0;
            while (true)
            {
                int first = (i << 2) + 1;
                if (first >= n)
                {
                    break;
                }

                // The least of the up to four children.
                int best = first;
                ulong least = entries[first];
                int end = Math.Min(first + 4, n);
                for (int c = first + 1; c < end; c++)
                {
                    if (entries[c] < least)
                    {
                        least = entries[c];
                        best = c;
                    }
                }

                if (least >= last)
                {
                    break;
                }

                entries[i] = least;
                i = best;
            }

            entries[i] = last;
        }

        key = top >> 32;
        return (int)(uint)top;
    }
}
