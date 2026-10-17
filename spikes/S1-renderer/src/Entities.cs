using System;

namespace Tzar2.Spikes.S1;

/// <summary>
/// Stand-in for a render snapshot (REQ-ARCH-03): structure-of-arrays entity state that moves every frame, plus the
/// per-frame painter's-order sort. The sort key is <c>(footY, entityId)</c>: an LSD radix sort on the 16-bit foot line
/// that is stable, so entities with equal footY stay in id order (TDD §13.3; layer/subOrder are a single layer here).
/// </summary>
public sealed class Entities
{
    private readonly float[] _vx;
    private readonly float[] _vy;
    private readonly float[] _phase;
    private readonly ushort[] _keys;
    private readonly int[] _scratch;
    private readonly int[] _counts = new int[256];

    public Entities(int count, float worldW, float worldH, uint seed)
    {
        Count = count;
        WorldW = worldW;
        WorldH = worldH;
        X = new float[count];
        Y = new float[count];
        _vx = new float[count];
        _vy = new float[count];
        _phase = new float[count];
        Kind = new byte[count];
        Player = new byte[count];
        Frame = new ushort[count];
        Order = new int[count];
        _keys = new ushort[count];
        _scratch = new int[count];

        uint s = seed == 0 ? 1u : seed;
        for (int i = 0; i < count; i++)
        {
            X[i] = Next(ref s) * worldW;
            Y[i] = (Next(ref s) * (worldH - SpriteSheet.Cell)) + SpriteSheet.Cell;
            float speed = 15f + (Next(ref s) * 45f);
            float a = Next(ref s) * MathF.Tau;
            _vx[i] = MathF.Cos(a) * speed;
            _vy[i] = MathF.Sin(a) * speed;
            _phase[i] = Next(ref s) * SpriteSheet.AnimFrames;
            Kind[i] = (byte)(Next(ref s) * SpriteSheet.Kinds);
            Player[i] = (byte)(i % SpriteSheet.Players);
            Order[i] = i;
        }
    }

    public int Count { get; }

    public float WorldW { get; }

    public float WorldH { get; }

    /// <summary>Foot (pivot) position in logical pixels.</summary>
    public float[] X { get; }

    public float[] Y { get; }

    public byte[] Kind { get; }

    public byte[] Player { get; }

    public ushort[] Frame { get; }

    /// <summary>Entity indices in painter's order (back to front), valid after <see cref="Sort"/>.</summary>
    public int[] Order { get; }

    /// <summary>Places entity <paramref name="i"/> at a fixed spot (verification scenes).</summary>
    public void Place(int i, float x, float y, int kind, int dir, int player)
    {
        X[i] = x;
        Y[i] = y;
        _vx[i] = 0;
        _vy[i] = 0;
        Kind[i] = (byte)kind;
        Player[i] = (byte)player;
        _phase[i] = 0;
        Frame[i] = (ushort)SpriteSheet.FrameIndex(kind, dir, 0);
    }

    public void Update(float dt, bool animate)
    {
        float maxX = WorldW;
        float minY = SpriteSheet.Cell;
        float maxY = WorldH;
        for (int i = 0; i < Count; i++)
        {
            float vx = _vx[i];
            float vy = _vy[i];
            if (vx == 0 && vy == 0)
            {
                continue;
            }

            float x = X[i] + (vx * dt);
            float y = Y[i] + (vy * dt);
            if (x < 0 || x > maxX)
            {
                vx = -vx;
                x = Math.Clamp(x, 0, maxX);
            }

            if (y < minY || y > maxY)
            {
                vy = -vy;
                y = Math.Clamp(y, minY, maxY);
            }

            X[i] = x;
            Y[i] = y;
            _vx[i] = vx;
            _vy[i] = vy;

            // 8 directions, 0 = east, counter-clockwise (screen y grows downwards).
            int dir = (int)MathF.Round(MathF.Atan2(-vy, vx) / (MathF.PI / 4f)) & 7;
            float phase = animate ? (_phase[i] + (dt * 8f)) % SpriteSheet.AnimFrames : _phase[i];
            _phase[i] = phase;
            Frame[i] = (ushort)SpriteSheet.FrameIndex(Kind[i], dir, (int)phase);
        }
    }

    /// <summary>Stable two-pass LSD radix sort (8-bit digits) of entity ids by snapped foot line. Allocation-free.</summary>
    public void Sort()
    {
        int n = Count;
        for (int i = 0; i < n; i++)
        {
            _keys[i] = (ushort)Math.Clamp((int)MathF.Round(Y[i]), 0, ushort.MaxValue);
            _scratch[i] = i;
        }

        // Pass 1: low byte, _scratch -> Order. Pass 2: high byte, Order -> _scratch, then copy back.
        RadixPass(_scratch, Order, 0);
        RadixPass(Order, _scratch, 8);
        Array.Copy(_scratch, Order, n);
    }

    /// <summary>Checks the painter's-order invariant (non-decreasing footY, ties by ascending id).</summary>
    public bool IsSorted()
    {
        for (int i = 1; i < Count; i++)
        {
            int a = Order[i - 1];
            int b = Order[i];
            if (_keys[a] > _keys[b] || (_keys[a] == _keys[b] && a > b))
            {
                return false;
            }
        }

        return true;
    }

    private void RadixPass(int[] src, int[] dst, int shift)
    {
        Array.Clear(_counts);
        int n = Count;
        for (int i = 0; i < n; i++)
        {
            _counts[(_keys[src[i]] >> shift) & 0xFF]++;
        }

        int sum = 0;
        for (int b = 0; b < 256; b++)
        {
            int c = _counts[b];
            _counts[b] = sum;
            sum += c;
        }

        for (int i = 0; i < n; i++)
        {
            int id = src[i];
            dst[_counts[(_keys[id] >> shift) & 0xFF]++] = id;
        }
    }

    private static float Next(ref uint s)
    {
        s ^= s << 13;
        s ^= s >> 17;
        s ^= s << 5;
        return (s & 0xFFFFFF) / 16777216f;
    }
}
