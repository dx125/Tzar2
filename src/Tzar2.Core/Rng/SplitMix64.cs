namespace Tzar2.Core.Rng;

/// <summary>SplitMix64 (Steele, Lea, Flood; reference code by S. Vigna), used to expand and derive seeds.</summary>
public struct SplitMix64
{
    private const ulong Gamma = 0x9E3779B97F4A7C15UL;

    private ulong _state;

    /// <summary>Creates a generator with the given state.</summary>
    public SplitMix64(ulong seed) => _state = seed;

    /// <summary>Returns the next 64-bit output.</summary>
    public ulong Next()
    {
        _state = unchecked(_state + Gamma);
        return Mix(_state);
    }

    /// <summary>The SplitMix64 output finalizer: a bijective 64-bit mixing function.</summary>
    public static ulong Mix(ulong z)
    {
        unchecked
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
