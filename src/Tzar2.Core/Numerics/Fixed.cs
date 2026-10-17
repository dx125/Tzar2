using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Tzar2.Core.Numerics;

/// <summary>
/// Deterministic Q47.16 fixed-point scalar stored in a <see cref="long"/> (TDD §8.2).
/// All arithmetic is integer-only and identical on every platform.
/// </summary>
/// <remarks>
/// <para>Rounding: multiplication, division and conversions to integers truncate toward zero unless the
/// method name says otherwise (TDD §8.2 default, revisited per formula once UNK-08 is resolved).</para>
/// <para>Overflow: the operators and conversions follow the build-time <see cref="OverflowPolicy"/> (ADR 0014):
/// they throw <see cref="OverflowException"/> in development/debug builds and clamp to the representable range
/// (and report the event) in release builds. Both are deterministic; overflow in the simulation is still a bug.
/// Use the <c>Saturating*</c> helpers where clamping is the intended behaviour in every build.</para>
/// </remarks>
public readonly struct Fixed : IEquatable<Fixed>, IComparable<Fixed>, IComparable, ISpanFormattable
{
    /// <summary>Number of fractional bits.</summary>
    public const int FractionBits = 16;

    /// <summary>Raw value of 1.0.</summary>
    public const long OneRaw = 1L << FractionBits;

    /// <summary>Largest integer that converts to <see cref="Fixed"/> exactly.</summary>
    public const long MaxInteger = long.MaxValue >> FractionBits;

    /// <summary>Smallest integer that converts to <see cref="Fixed"/> exactly.</summary>
    public const long MinInteger = long.MinValue >> FractionBits;

    private const long FractionMask = OneRaw - 1;

    // 5^16: frac / 2^16 == frac * 5^16 / 10^16, so 16 decimal digits represent any fraction exactly.
    private const ulong FractionToDecimal = 152_587_890_625UL;
    private const int FractionDecimalDigits = 16;

    private readonly long _raw;

    private Fixed(long raw) => _raw = raw;

    /// <summary>0.</summary>
    public static Fixed Zero => default;

    /// <summary>1.</summary>
    public static Fixed One => new(OneRaw);

    /// <summary>0.5.</summary>
    public static Fixed Half => new(OneRaw >> 1);

    /// <summary>The smallest positive value, 1/65,536.</summary>
    public static Fixed Epsilon => new(1);

    /// <summary>The largest representable value.</summary>
    public static Fixed MaxValue => new(long.MaxValue);

    /// <summary>The smallest representable value.</summary>
    public static Fixed MinValue => new(long.MinValue);

    /// <summary>The underlying Q47.16 representation.</summary>
    public long Raw => _raw;

    /// <summary>Creates a value from its raw Q47.16 representation.</summary>
    public static Fixed FromRaw(long raw) => new(raw);

    /// <summary>Converts an integer exactly.</summary>
    /// <exception cref="OverflowException"><paramref name="value"/> is outside [<see cref="MinInteger"/>, <see cref="MaxInteger"/>] (policy <see cref="OverflowMode.Throw"/>).</exception>
    public static Fixed FromInt(long value)
    {
        if (value > MaxInteger || value < MinInteger)
        {
            return new(OverflowPolicy.Overflow(OverflowSite.Convert, value < 0 ? long.MinValue : long.MaxValue));
        }

        return new(value << FractionBits);
    }

    /// <summary>Creates <c>numerator / denominator</c>, truncated toward zero.</summary>
    /// <exception cref="DivideByZeroException"><paramref name="denominator"/> is zero.</exception>
    /// <exception cref="OverflowException">The result is outside the Fixed range (policy <see cref="OverflowMode.Throw"/>).</exception>
    public static Fixed FromRatio(long numerator, long denominator)
    {
        Int128 raw = ((Int128)numerator << FractionBits) / denominator;
        return new(Checked(raw, OverflowSite.Convert));
    }

    /// <summary>Integer part, truncated toward zero.</summary>
    public long ToInt() => _raw / OneRaw;

    /// <summary>Largest integer less than or equal to this value.</summary>
    public long FloorToInt() => _raw >> FractionBits;

    /// <summary>Smallest integer greater than or equal to this value.</summary>
    public long CeilingToInt() => (_raw >> FractionBits) + ((_raw & FractionMask) != 0 ? 1 : 0);

    /// <summary>Nearest integer; halves round away from zero.</summary>
    public long RoundToInt()
    {
        long integer = ToInt();
        long fraction = _raw - (integer * OneRaw);
        if (fraction >= OneRaw / 2)
        {
            return integer + 1;
        }

        if (fraction <= -OneRaw / 2)
        {
            return integer - 1;
        }

        return integer;
    }

    /// <summary>Largest whole value less than or equal to this value.</summary>
    public Fixed Floor() => new(_raw & ~FractionMask);

    /// <summary>Whole part, truncated toward zero.</summary>
    public Fixed Truncate() => new(ToInt() * OneRaw);

    /// <summary>Fractional part with the sign of this value (<c>this - Truncate()</c>).</summary>
    public Fixed Fraction() => new(_raw % OneRaw);

    /// <summary>-1, 0 or 1.</summary>
    public int Sign => Math.Sign(_raw);

    /// <summary>Absolute value.</summary>
    /// <exception cref="OverflowException">The value is <see cref="MinValue"/> (policy <see cref="OverflowMode.Throw"/>).</exception>
    public static Fixed Abs(Fixed value)
        => value._raw == long.MinValue ? new(OverflowPolicy.Overflow(OverflowSite.Negate, long.MaxValue)) : new(Math.Abs(value._raw));

    /// <summary>The smaller of two values.</summary>
    public static Fixed Min(Fixed a, Fixed b) => a._raw <= b._raw ? a : b;

    /// <summary>The larger of two values.</summary>
    public static Fixed Max(Fixed a, Fixed b) => a._raw >= b._raw ? a : b;

    /// <summary>Clamps <paramref name="value"/> into [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public static Fixed Clamp(Fixed value, Fixed min, Fixed max)
    {
        if (min._raw > max._raw)
        {
            throw new ArgumentException("min must not be greater than max.", nameof(min));
        }

        return new(Math.Clamp(value._raw, min._raw, max._raw));
    }

    /// <summary>Square root, truncated toward zero, by integer Newton iteration.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public static Fixed Sqrt(Fixed value)
    {
        if (value._raw < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Square root of a negative value.");
        }

        // sqrt(raw / 2^16) * 2^16 == sqrt(raw * 2^16)
        return new((long)IntMath.ISqrt((UInt128)(ulong)value._raw << FractionBits));
    }

    /// <summary>Addition that clamps to the representable range instead of overflowing.</summary>
    public static Fixed SaturatingAdd(Fixed a, Fixed b)
    {
        long result = unchecked(a._raw + b._raw);
        // Overflow happened if both operands have the same sign and the result's sign differs.
        if (((a._raw ^ result) & (b._raw ^ result)) < 0)
        {
            return a._raw < 0 ? MinValue : MaxValue;
        }

        return new(result);
    }

    /// <summary>Subtraction that clamps to the representable range instead of overflowing.</summary>
    public static Fixed SaturatingSubtract(Fixed a, Fixed b)
    {
        long result = unchecked(a._raw - b._raw);
        if (((a._raw ^ b._raw) & (a._raw ^ result)) < 0)
        {
            return a._raw < 0 ? MinValue : MaxValue;
        }

        return new(result);
    }

    /// <summary>Multiplication (truncated toward zero) that clamps to the representable range.</summary>
    public static Fixed SaturatingMultiply(Fixed a, Fixed b) => new(Saturate(MultiplyRaw(a._raw, b._raw)));

    /// <summary>Division (truncated toward zero) that clamps to the representable range.</summary>
    /// <exception cref="DivideByZeroException"><paramref name="b"/> is zero.</exception>
    public static Fixed SaturatingDivide(Fixed a, Fixed b) => new(Saturate(DivideRaw(a._raw, b._raw)));

    /// <summary>Linear interpolation <c>a + (b - a) * t</c>.</summary>
    public static Fixed Lerp(Fixed a, Fixed b, Fixed t) => a + ((b - a) * t);

    /// <summary>Addition; overflow follows <see cref="OverflowPolicy"/>.</summary>
    public static Fixed operator +(Fixed a, Fixed b)
    {
        long result = unchecked(a._raw + b._raw);
        if (((a._raw ^ result) & (b._raw ^ result)) < 0)
        {
            return new(OverflowPolicy.Overflow(OverflowSite.Add, a._raw < 0 ? long.MinValue : long.MaxValue));
        }

        return new(result);
    }

    /// <summary>Subtraction; overflow follows <see cref="OverflowPolicy"/>.</summary>
    public static Fixed operator -(Fixed a, Fixed b)
    {
        long result = unchecked(a._raw - b._raw);
        if (((a._raw ^ b._raw) & (a._raw ^ result)) < 0)
        {
            return new(OverflowPolicy.Overflow(OverflowSite.Subtract, a._raw < 0 ? long.MinValue : long.MaxValue));
        }

        return new(result);
    }

    /// <summary>Negation; <c>-MinValue</c> follows <see cref="OverflowPolicy"/> (saturates to <see cref="MaxValue"/>).</summary>
    public static Fixed operator -(Fixed a)
        => a._raw == long.MinValue ? new(OverflowPolicy.Overflow(OverflowSite.Negate, long.MaxValue)) : new(-a._raw);

    /// <summary>Identity.</summary>
    public static Fixed operator +(Fixed a) => a;

    /// <summary>Multiplication, truncated toward zero; overflow follows <see cref="OverflowPolicy"/>.</summary>
    public static Fixed operator *(Fixed a, Fixed b) => new(Checked(MultiplyRaw(a._raw, b._raw), OverflowSite.Multiply));

    /// <summary>Multiplication by an integer; overflow follows <see cref="OverflowPolicy"/>.</summary>
    public static Fixed operator *(Fixed a, long b) => new(Checked((Int128)a._raw * b, OverflowSite.Multiply));

    /// <summary>Multiplication by an integer; overflow follows <see cref="OverflowPolicy"/>.</summary>
    public static Fixed operator *(long a, Fixed b) => new(Checked((Int128)a * b._raw, OverflowSite.Multiply));

    /// <summary>Division, truncated toward zero; overflow follows <see cref="OverflowPolicy"/>.</summary>
    /// <exception cref="DivideByZeroException"><paramref name="b"/> is zero (in every build: a zero divisor is not an overflow).</exception>
    public static Fixed operator /(Fixed a, Fixed b) => new(Checked(DivideRaw(a._raw, b._raw), OverflowSite.Divide));

    /// <summary>Division by an integer, truncated toward zero; <c>MinValue / -1</c> follows <see cref="OverflowPolicy"/>.</summary>
    /// <exception cref="DivideByZeroException"><paramref name="b"/> is zero.</exception>
    public static Fixed operator /(Fixed a, long b)
        => new(Checked((Int128)a._raw / b, OverflowSite.Divide));

    /// <summary>Remainder with the sign of the dividend (same semantics as <see cref="long"/>).</summary>
    public static Fixed operator %(Fixed a, Fixed b) => new(a._raw % b._raw);

    /// <summary>Equality.</summary>
    public static bool operator ==(Fixed a, Fixed b) => a._raw == b._raw;

    /// <summary>Inequality.</summary>
    public static bool operator !=(Fixed a, Fixed b) => a._raw != b._raw;

    /// <summary>Less than.</summary>
    public static bool operator <(Fixed a, Fixed b) => a._raw < b._raw;

    /// <summary>Greater than.</summary>
    public static bool operator >(Fixed a, Fixed b) => a._raw > b._raw;

    /// <summary>Less than or equal.</summary>
    public static bool operator <=(Fixed a, Fixed b) => a._raw <= b._raw;

    /// <summary>Greater than or equal.</summary>
    public static bool operator >=(Fixed a, Fixed b) => a._raw >= b._raw;

    /// <inheritdoc />
    public bool Equals(Fixed other) => _raw == other._raw;

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Fixed other && _raw == other._raw;

    /// <inheritdoc />
    public override int GetHashCode() => _raw.GetHashCode();

    /// <inheritdoc />
    public int CompareTo(Fixed other) => _raw.CompareTo(other._raw);

    /// <inheritdoc />
    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        Fixed other => CompareTo(other),
        _ => throw new ArgumentException("Object must be of type Fixed.", nameof(obj)),
    };

    /// <summary>Exact decimal representation using the invariant culture, e.g. <c>-1.5</c> or <c>0.0000152587890625</c>.</summary>
    public override string ToString()
    {
        Span<char> buffer = stackalloc char[MaxFormattedLength];
        TryFormat(buffer, out int written);
        return new string(buffer[..written]);
    }

    /// <summary>
    /// Formats like <see cref="ToString()"/>. The only supported formats are empty/<see langword="null"/> and <c>G</c>
    /// (both exact); the provider is ignored, the output is always invariant.
    /// </summary>
    /// <exception cref="FormatException"><paramref name="format"/> is not supported.</exception>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        ValidateFormat(format);
        return ToString();
    }

    /// <inheritdoc cref="ToString(string?, IFormatProvider?)" />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        ValidateFormat(format);
        return TryFormat(destination, out charsWritten);
    }

    private static void ValidateFormat(ReadOnlySpan<char> format)
    {
        if (!format.IsEmpty && !format.SequenceEqual("G") && !format.SequenceEqual("g"))
        {
            throw new FormatException($"Format '{format.ToString()}' is not supported by Fixed; use \"\" or \"G\" (exact).");
        }
    }

    // "-" + 15 integer digits (2^47) + "." + 16 fraction digits.
    private const int MaxFormattedLength = 1 + 15 + 1 + FractionDecimalDigits;

    private bool TryFormat(Span<char> destination, out int charsWritten)
    {
        charsWritten = 0;
        ulong magnitude = _raw < 0 ? (ulong)(-(Int128)_raw) : (ulong)_raw;
        ulong integer = magnitude >> FractionBits;
        ulong fraction = magnitude & FractionMask;

        int position = 0;
        if (_raw < 0)
        {
            if (destination.Length < 1)
            {
                return false;
            }

            destination[position++] = '-';
        }

        if (!integer.TryFormat(destination[position..], out int integerChars, default, CultureInfo.InvariantCulture))
        {
            return false;
        }

        position += integerChars;
        if (fraction != 0)
        {
            Span<char> digits = stackalloc char[FractionDecimalDigits];
            (fraction * FractionToDecimal).TryFormat(digits, out _, "D16", CultureInfo.InvariantCulture);
            int length = FractionDecimalDigits;
            while (digits[length - 1] == '0')
            {
                length--;
            }

            if (destination.Length < position + 1 + length)
            {
                return false;
            }

            destination[position++] = '.';
            digits[..length].CopyTo(destination[position..]);
            position += length;
        }

        charsWritten = position;
        return true;
    }

    /// <summary>
    /// Parses an invariant decimal string such as <c>12</c>, <c>-0.25</c> or <c>3.14159</c>, using integer
    /// arithmetic only. Fractions that are not exactly representable are truncated toward zero.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out Fixed value)
    {
        value = default;
        int position = 0;
        bool negative = false;
        if (position < text.Length && (text[position] == '-' || text[position] == '+'))
        {
            negative = text[position] == '-';
            position++;
        }

        UInt128 integer = 0;
        int integerDigits = 0;
        while (position < text.Length && char.IsAsciiDigit(text[position]))
        {
            integer = (integer * 10) + (uint)(text[position] - '0');
            if (integer > (UInt128)MaxInteger + 1)
            {
                return false;
            }

            integerDigits++;
            position++;
        }

        // Up to 19 fractional digits are significant (10^19 fits in a ulong); further digits are truncated.
        ulong fractionDigits = 0;
        ulong fractionScale = 1;
        int fractionDigitCount = 0;
        if (position < text.Length && text[position] == '.')
        {
            position++;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                if (fractionDigitCount < 19)
                {
                    fractionDigits = (fractionDigits * 10) + (uint)(text[position] - '0');
                    fractionScale *= 10;
                }

                fractionDigitCount++;
                position++;
            }
        }

        if (position != text.Length || (integerDigits == 0 && fractionDigitCount == 0))
        {
            return false;
        }

        UInt128 magnitude = (integer << FractionBits) + (((UInt128)fractionDigits << FractionBits) / fractionScale);
        if (negative)
        {
            if (magnitude > (UInt128)long.MaxValue + 1)
            {
                return false;
            }

            value = new((long)(-(Int128)magnitude));
            return true;
        }

        if (magnitude > long.MaxValue)
        {
            return false;
        }

        value = new((long)magnitude);
        return true;
    }

    /// <summary>Parses an invariant decimal string; see <see cref="TryParse(ReadOnlySpan{char}, out Fixed)"/>.</summary>
    /// <exception cref="FormatException">The text is not a valid number in the Fixed range.</exception>
    public static Fixed Parse(ReadOnlySpan<char> text)
        => TryParse(text, out Fixed value) ? value : throw new FormatException($"'{text.ToString()}' is not a valid Fixed value.");

    private static Int128 MultiplyRaw(long a, long b) => (Int128)a * b / OneRaw;

    private static Int128 DivideRaw(long a, long b) => ((Int128)a << FractionBits) / b;

    private static long Checked(Int128 value, OverflowSite site)
    {
        if (value > long.MaxValue)
        {
            return OverflowPolicy.Overflow(site, long.MaxValue);
        }

        return value < long.MinValue ? OverflowPolicy.Overflow(site, long.MinValue) : (long)value;
    }

    private static long Saturate(Int128 value)
    {
        if (value > long.MaxValue)
        {
            return long.MaxValue;
        }

        return value < long.MinValue ? long.MinValue : (long)value;
    }
}
