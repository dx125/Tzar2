using System;
using System.Numerics;
using FsCheck.Xunit;
using Tzar2.Core.Numerics;
using Xunit;

namespace Tzar2.Core.Tests.Numerics;

public class FixedTests
{
    [Fact]
    public void Constants_have_expected_raw_values()
    {
        Assert.Equal(0, Fixed.Zero.Raw);
        Assert.Equal(65536, Fixed.One.Raw);
        Assert.Equal(32768, Fixed.Half.Raw);
        Assert.Equal(1, Fixed.Epsilon.Raw);
        Assert.Equal(long.MaxValue, Fixed.MaxValue.Raw);
        Assert.Equal(long.MinValue, Fixed.MinValue.Raw);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(Fixed.MaxInteger)]
    [InlineData(Fixed.MinInteger)]
    public void FromInt_round_trips(long value)
    {
        Fixed f = Fixed.FromInt(value);
        Assert.Equal(value, f.ToInt());
        Assert.Equal(value * Fixed.OneRaw, f.Raw);
    }

    [Theory]
    [InlineData(Fixed.MaxInteger + 1)]
    [InlineData(Fixed.MinInteger - 1)]
    public void FromInt_out_of_range_follows_the_overflow_policy(long value)
    {
        if (OverflowPolicy.Mode == OverflowMode.Throw)
        {
            Assert.Throws<OverflowException>(() => Fixed.FromInt(value));
        }
        else
        {
            Assert.Equal(value < 0 ? Fixed.MinValue : Fixed.MaxValue, Fixed.FromInt(value));
        }
    }

    [Theory]
    [InlineData(1, 2, 32768)]
    [InlineData(-1, 2, -32768)]
    [InlineData(1, 3, 21845)] // 0.33333 truncated
    [InlineData(-1, 3, -21845)] // truncated toward zero, not floored
    [InlineData(7, 1, 7 * 65536)]
    public void FromRatio_truncates_toward_zero(long numerator, long denominator, long expectedRaw)
        => Assert.Equal(expectedRaw, Fixed.FromRatio(numerator, denominator).Raw);

    [Fact]
    public void Arithmetic_on_simple_values()
    {
        Fixed a = Fixed.FromRatio(3, 2);
        Fixed b = Fixed.FromInt(2);
        Assert.Equal(Fixed.FromRatio(7, 2), a + b);
        Assert.Equal(Fixed.FromRatio(-1, 2), a - b);
        Assert.Equal(Fixed.FromInt(3), a * b);
        Assert.Equal(Fixed.FromRatio(3, 4), a / b);
        Assert.Equal(Fixed.FromRatio(-3, 2), -a);
        Assert.Equal(Fixed.FromInt(6), a * 4);
        Assert.Equal(Fixed.FromRatio(3, 8), a / 4);
        Assert.Equal(Fixed.FromRatio(3, 2), Fixed.FromRatio(7, 2) % b);
    }

    [Fact]
    public void Multiplication_truncates_toward_zero()
    {
        // 1/65536 * 1/2 = 1/131072, below the resolution: truncates to 0 for both signs.
        Assert.Equal(0, (Fixed.Epsilon * Fixed.Half).Raw);
        Assert.Equal(0, (-Fixed.Epsilon * Fixed.Half).Raw);
        Assert.Equal(-1, (Fixed.FromRaw(-3) * Fixed.Half).Raw);
    }

    [Fact]
    public void Operators_follow_the_overflow_policy()
    {
        (Func<Fixed> Operation, Fixed Saturated)[] cases =
        [
            (() => Fixed.MaxValue + Fixed.Epsilon, Fixed.MaxValue),
            (() => Fixed.MinValue - Fixed.Epsilon, Fixed.MinValue),
            (() => Fixed.MinValue + -Fixed.One, Fixed.MinValue),
            (() => -Fixed.MinValue, Fixed.MaxValue),
            (() => Fixed.Abs(Fixed.MinValue), Fixed.MaxValue),
            (() => Fixed.MaxValue * Fixed.FromInt(2), Fixed.MaxValue),
            (() => Fixed.MaxValue * Fixed.FromInt(-2), Fixed.MinValue),
            (() => Fixed.MaxValue * 2L, Fixed.MaxValue),
            (() => -3L * Fixed.MaxValue, Fixed.MinValue),
            (() => Fixed.MaxValue / Fixed.Half, Fixed.MaxValue),
            (() => Fixed.MinValue / -1L, Fixed.MaxValue),
            (() => Fixed.FromRatio(long.MaxValue, 1), Fixed.MaxValue),
        ];

        long before = OverflowPolicy.SaturationCount;
        foreach ((Func<Fixed> operation, Fixed saturated) in cases)
        {
            if (OverflowPolicy.Mode == OverflowMode.Throw)
            {
                Assert.Throws<OverflowException>(() => operation());
            }
            else
            {
                Assert.Equal(saturated, operation());
            }
        }

        if (OverflowPolicy.Mode == OverflowMode.Saturate)
        {
            Assert.True(OverflowPolicy.SaturationCount - before >= cases.Length);
        }

        // A zero divisor is a bug, not an overflow: it throws under both policies.
        Assert.Throws<DivideByZeroException>(() => Fixed.One / Fixed.Zero);
        Assert.Throws<DivideByZeroException>(() => Fixed.One / 0L);
    }

    [Fact]
    public void Overflow_policy_matches_the_build_configuration()
    {
#if DEBUG
        Assert.Equal(OverflowMode.Throw, OverflowPolicy.Mode);
#else
        Assert.Equal(OverflowMode.Saturate, OverflowPolicy.Mode);
#endif
    }

    [Fact]
    public void Operators_in_range_are_exact_at_the_edges()
    {
        Assert.Equal(Fixed.MaxValue, Fixed.FromRaw(long.MaxValue - 1) + Fixed.Epsilon);
        Assert.Equal(Fixed.MinValue, Fixed.FromRaw(long.MinValue + 1) - Fixed.Epsilon);
        Assert.Equal(Fixed.FromRaw(-long.MaxValue), -Fixed.MaxValue);
        Assert.Equal(Fixed.FromRaw(long.MinValue), Fixed.FromRaw(long.MinValue / 2) * 2L);
    }

    [Fact]
    public void Saturating_helpers_clamp()
    {
        Assert.Equal(Fixed.MaxValue, Fixed.SaturatingAdd(Fixed.MaxValue, Fixed.One));
        Assert.Equal(Fixed.MinValue, Fixed.SaturatingAdd(Fixed.MinValue, -Fixed.One));
        Assert.Equal(Fixed.MaxValue, Fixed.SaturatingSubtract(Fixed.MaxValue, -Fixed.One));
        Assert.Equal(Fixed.MinValue, Fixed.SaturatingSubtract(Fixed.MinValue, Fixed.One));
        Assert.Equal(Fixed.MaxValue, Fixed.SaturatingMultiply(Fixed.MaxValue, Fixed.FromInt(2)));
        Assert.Equal(Fixed.MinValue, Fixed.SaturatingMultiply(Fixed.MaxValue, Fixed.FromInt(-2)));
        Assert.Equal(Fixed.MaxValue, Fixed.SaturatingDivide(Fixed.MaxValue, Fixed.Half));
        Assert.Equal(Fixed.FromInt(3), Fixed.SaturatingAdd(Fixed.One, Fixed.FromInt(2)));
    }

    [Theory]
    [InlineData(65536 * 5 / 2, 2, 2, 3, 3)] // 2.5
    [InlineData(-65536 * 5 / 2, -2, -3, -2, -3)] // -2.5
    [InlineData(65536 + 1, 1, 1, 2, 1)]
    [InlineData(-65536 - 1, -1, -2, -1, -1)]
    [InlineData(65536 * 3, 3, 3, 3, 3)]
    public void Integer_conversions(long raw, long toInt, long floor, long ceiling, long round)
    {
        Fixed f = Fixed.FromRaw(raw);
        Assert.Equal(toInt, f.ToInt());
        Assert.Equal(floor, f.FloorToInt());
        Assert.Equal(ceiling, f.CeilingToInt());
        Assert.Equal(round, f.RoundToInt());
        Assert.Equal(floor * Fixed.OneRaw, f.Floor().Raw);
        Assert.Equal(toInt * Fixed.OneRaw, f.Truncate().Raw);
        Assert.Equal(raw - (toInt * Fixed.OneRaw), f.Fraction().Raw);
    }

    [Fact]
    public void Min_max_clamp_abs_sign()
    {
        Fixed a = Fixed.FromInt(-3);
        Fixed b = Fixed.FromInt(5);
        Assert.Equal(a, Fixed.Min(a, b));
        Assert.Equal(b, Fixed.Max(a, b));
        Assert.Equal(Fixed.Zero, Fixed.Clamp(Fixed.Zero, a, b));
        Assert.Equal(b, Fixed.Clamp(Fixed.FromInt(9), a, b));
        Assert.Equal(Fixed.FromInt(3), Fixed.Abs(a));
        Assert.Equal(-1, a.Sign);
        Assert.Equal(0, Fixed.Zero.Sign);
        Assert.Throws<ArgumentException>(() => Fixed.Clamp(Fixed.Zero, b, a));
        Assert.Equal(Fixed.FromInt(1), Fixed.Lerp(a, b, Fixed.Half));
    }

    [Theory]
    [InlineData(4, 2)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(1_000_000, 1000)]
    public void Sqrt_of_perfect_squares_is_exact(long value, long root)
        => Assert.Equal(Fixed.FromInt(root), Fixed.Sqrt(Fixed.FromInt(value)));

    [Fact]
    public void Sqrt_of_two_matches_known_digits()
        => Assert.Equal(92681, Fixed.Sqrt(Fixed.FromInt(2)).Raw); // floor(sqrt(2) * 65536) = 92681

    [Fact]
    public void Sqrt_of_negative_throws() => Assert.Throws<ArgumentOutOfRangeException>(() => Fixed.Sqrt(-Fixed.One));

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(65536L, "1")]
    [InlineData(-98304L, "-1.5")]
    [InlineData(1L, "0.0000152587890625")]
    [InlineData(long.MaxValue, "140737488355327.9999847412109375")]
    [InlineData(long.MinValue, "-140737488355328")]
    public void ToString_is_exact_and_invariant(long raw, string expected)
        => Assert.Equal(expected, Fixed.FromRaw(raw).ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("G")]
    [InlineData("g")]
    public void Supported_formats_are_exact(string? format)
    {
        Fixed value = Fixed.FromRaw(-98304);
        Assert.Equal("-1.5", value.ToString(format, System.Globalization.CultureInfo.GetCultureInfo("de-DE")));
        Span<char> buffer = stackalloc char[8];
        Assert.True(value.TryFormat(buffer, out int written, format, null));
        Assert.Equal("-1.5", buffer[..written].ToString());
        Assert.Equal("x=-1.5", FormattableString.Invariant($"x={value}"));
    }

    [Theory]
    [InlineData("F2")]
    [InlineData("N")]
    [InlineData("X")]
    [InlineData("G5")]
    public void Unsupported_formats_throw(string format)
    {
        Fixed value = Fixed.One;
        Assert.Throws<FormatException>(() => value.ToString(format, null));
        Assert.Throws<FormatException>(() => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:" + format + "}", value));
        Assert.Throws<FormatException>(() => value.TryFormat(new char[64], out _, format, null));
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("+2", 131072L)]
    [InlineData("-0.5", -32768L)]
    [InlineData(".25", 16384L)]
    [InlineData("3.", 196608L)]
    [InlineData("0.33333", 21845L)]
    [InlineData("-140737488355328", long.MinValue)]
    [InlineData("0.00001525878906249999999999", 0L)] // just below Epsilon: truncated
    public void Parse_valid(string text, long expectedRaw) => Assert.Equal(expectedRaw, Fixed.Parse(text).Raw);

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData(".")]
    [InlineData("1e5")]
    [InlineData("1,5")]
    [InlineData(" 1")]
    [InlineData("140737488355328")]
    [InlineData("-140737488355328.5")]
    public void Parse_invalid(string text)
    {
        Assert.False(Fixed.TryParse(text, out _));
        Assert.Throws<FormatException>(() => Fixed.Parse(text));
    }

    [Fact]
    public void Equality_and_comparison()
    {
        Fixed a = Fixed.FromInt(1);
        Fixed b = Fixed.FromRaw(65536);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(Fixed.Half < a && a > Fixed.Half && a <= b && a >= b);
        Assert.Equal(0, a.CompareTo((object)b));
        Assert.Equal(1, a.CompareTo(null));
        Assert.Throws<ArgumentException>(() => a.CompareTo("1"));
    }

    // Property tests against an arbitrary-precision reference model.

    [Property(MaxTest = 2000)]
    public bool ToString_Parse_round_trips(long raw) => Fixed.Parse(Fixed.FromRaw(raw).ToString()).Raw == raw;

    [Property(MaxTest = 2000)]
    public bool Multiply_matches_reference(long a, long b)
    {
        // Keep the product in range: |a|,|b| < 2^39 gives |a*b/2^16| < 2^62.
        Fixed x = Fixed.FromRaw(a >> 24);
        Fixed y = Fixed.FromRaw(b >> 24);
        BigInteger expected = BigInteger.Divide((BigInteger)x.Raw * y.Raw, Fixed.OneRaw);
        return (x * y).Raw == (long)expected && (x * y) == (y * x);
    }

    [Property(MaxTest = 2000)]
    public bool Divide_matches_reference(long a, long b)
    {
        Fixed x = Fixed.FromRaw(a >> 17);
        Fixed y = Fixed.FromRaw((b >> 17) | 1); // never zero
        BigInteger expected = BigInteger.Divide((BigInteger)x.Raw * Fixed.OneRaw, y.Raw);
        if (expected > long.MaxValue || expected < long.MinValue)
        {
            return Fixed.SaturatingDivide(x, y) == (expected > 0 ? Fixed.MaxValue : Fixed.MinValue);
        }

        return (x / y).Raw == (long)expected;
    }

    [Property(MaxTest = 2000)]
    public bool Addition_is_invertible(long a, long b)
    {
        Fixed x = Fixed.FromRaw(a >> 1);
        Fixed y = Fixed.FromRaw(b >> 1);
        return (x + y - y) == x && (x + y) == (y + x);
    }

    [Property(MaxTest = 2000)]
    public bool Saturating_add_matches_clamped_reference(long a, long b)
    {
        BigInteger exact = (BigInteger)a + b;
        long expected = exact > long.MaxValue ? long.MaxValue : exact < long.MinValue ? long.MinValue : (long)exact;
        return Fixed.SaturatingAdd(Fixed.FromRaw(a), Fixed.FromRaw(b)).Raw == expected;
    }

    [Property(MaxTest = 2000)]
    public bool Saturating_subtract_matches_clamped_reference(long a, long b)
    {
        BigInteger exact = (BigInteger)a - b;
        long expected = exact > long.MaxValue ? long.MaxValue : exact < long.MinValue ? long.MinValue : (long)exact;
        return Fixed.SaturatingSubtract(Fixed.FromRaw(a), Fixed.FromRaw(b)).Raw == expected;
    }

    [Property(MaxTest = 2000)]
    public bool Sqrt_is_floor_of_exact_root(long a)
    {
        long raw = a == long.MinValue ? long.MaxValue : Math.Abs(a);
        long root = Fixed.Sqrt(Fixed.FromRaw(raw)).Raw;
        BigInteger scaled = (BigInteger)raw << Fixed.FractionBits;
        return (BigInteger)root * root <= scaled && (BigInteger)(root + 1) * (root + 1) > scaled;
    }

    [Property(MaxTest = 2000)]
    public bool Floor_and_ceiling_bracket_value(long raw)
    {
        Fixed f = Fixed.FromRaw(raw >> 1);
        return f.Floor() <= f && f.Floor() > f - Fixed.One && f.CeilingToInt() - f.FloorToInt() == (f.Fraction() == Fixed.Zero ? 0 : 1);
    }
}
