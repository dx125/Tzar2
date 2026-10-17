using System;

namespace Tzar2.Spikes.Lua;

/// <summary>
/// Load-time source checks. The only one so far: the <c>^</c> operator is rejected, because Lua-CSharp evaluates it
/// with <see cref="Math.Pow(double, double)"/> (the platform C library), both at run time and when the compiler folds
/// constants, so it is not guaranteed to be identical across platforms. Integer powers are cheap to write as loops
/// or via <c>fx</c> helpers.
/// </summary>
public static class SourcePolicy
{
    /// <summary>Returns null when the source is acceptable, otherwise a message with the 1-based line.</summary>
    public static string? Check(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int line = 1;
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\n')
            {
                line++;
                i++;
            }
            else if (c == '-' && i + 1 < source.Length && source[i + 1] == '-')
            {
                i += 2;
                int level = LongBracketLevel(source, i);
                if (level >= 0)
                {
                    i = SkipLongBracket(source, i, level, ref line);
                }
                else
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        i++;
                    }
                }
            }
            else if (c == '"' || c == '\'')
            {
                i++;
                while (i < source.Length && source[i] != c)
                {
                    if (source[i] == '\\')
                    {
                        i++;
                    }

                    if (i < source.Length && source[i] == '\n')
                    {
                        line++;
                    }

                    i++;
                }

                i++;
            }
            else if (c == '[' && LongBracketLevel(source, i) >= 0)
            {
                i = SkipLongBracket(source, i, LongBracketLevel(source, i), ref line);
            }
            else if (c == '^')
            {
                return $"line {line}: the '^' operator is not allowed (Math.Pow is not cross-platform deterministic)";
            }
            else
            {
                i++;
            }
        }

        return null;
    }

    // "[", "=" * level, "[" starting at index; -1 when there is no long bracket.
    private static int LongBracketLevel(string s, int index)
    {
        if (index >= s.Length || s[index] != '[')
        {
            return -1;
        }

        int j = index + 1;
        while (j < s.Length && s[j] == '=')
        {
            j++;
        }

        return j < s.Length && s[j] == '[' ? j - index - 1 : -1;
    }

    private static int SkipLongBracket(string s, int index, int level, ref int line)
    {
        int i = index + level + 2;
        while (i < s.Length)
        {
            if (s[i] == '\n')
            {
                line++;
            }
            else if (s[i] == ']')
            {
                int j = i + 1;
                while (j < s.Length && s[j] == '=')
                {
                    j++;
                }

                if (j < s.Length && s[j] == ']' && j - i - 1 == level)
                {
                    return j + 1;
                }
            }

            i++;
        }

        return i;
    }
}
