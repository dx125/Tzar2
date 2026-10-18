using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Tzar2.Content.Editing;

/// <summary>A problem found in a Lua script (1-based line).</summary>
/// <param name="Line">Line.</param>
/// <param name="Message">What is wrong.</param>
public readonly record struct ScriptIssue(int Line, string Message);

/// <summary>A token class of the Lua highlighter.</summary>
public enum LuaToken : byte
{
    /// <summary>A keyword.</summary>
    Keyword,

    /// <summary>A string literal.</summary>
    String,

    /// <summary>A comment.</summary>
    Comment,

    /// <summary>A number.</summary>
    Number,
}

/// <summary>
/// The map's Lua scripts (TDD §18.3 "Lua script editor", §18.4 <c>scripts/*.lua</c>): stored in the document, highlighted
/// by the editor, and checked for unterminated strings and comments, unbalanced brackets and block keywords
/// (<c>function</c>/<c>if</c>/<c>do</c> … <c>end</c>, <c>repeat</c> … <c>until</c>), the errors that are clear without the
/// Lua host. API completion waits for the Lua API v1 (TDD §25 M8).
/// </summary>
public static class LuaScripts
{
    /// <summary>The Lua 5.4 keywords (highlighting).</summary>
    public static readonly ImmutableArray<string> Keywords =
    [
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in", "local", "nil", "not", "or",
        "repeat", "return", "then", "true", "until", "while",
    ];

    /// <summary>Adds or replaces a script (the name is normalised to <c>name.lua</c>, lower case).</summary>
    public static DocumentState SetScript(this DocumentState state, string name, string source)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(source);
        return state with { Scripts = state.Scripts.SetItem(Normalize(name), source.Replace("\r\n", "\n", StringComparison.Ordinal)) };
    }

    /// <summary>Removes a script.</summary>
    public static DocumentState RemoveScript(this DocumentState state, string name)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { Scripts = state.Scripts.Remove(Normalize(name)) };
    }

    /// <summary>The file name of a script: a single normalised segment ending in <c>.lua</c>.</summary>
    public static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        string n = ContentPath.Normalize(name.Replace('/', '_').Replace('\\', '_'));
        return n.EndsWith(".lua", StringComparison.Ordinal) ? n : n + ".lua";
    }

    /// <summary>Checks a script; returns the problems in line order (empty = none found).</summary>
    public static ImmutableArray<ScriptIssue> Check(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var issues = ImmutableArray.CreateBuilder<ScriptIssue>();
        var blocks = new Stack<(string Word, int Line)>();
        var brackets = new Stack<(char Open, int Line)>();
        int line = 1, i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\n')
            {
                line++;
                i++;
            }
            else if (c == '-' && At(source, i, "--"))
            {
                int level = LongBracket(source, i + 2);
                if (level >= 0)
                {
                    int start = line;
                    i = SkipLong(source, i + 2, level, ref line);
                    if (i < 0)
                    {
                        issues.Add(new ScriptIssue(start, "unfinished long comment"));
                        break;
                    }
                }
                else
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        i++;
                    }
                }
            }
            else if (c is '"' or '\'')
            {
                int start = line;
                i++;
                while (i < source.Length && source[i] != c && source[i] != '\n')
                {
                    i += source[i] == '\\' ? 2 : 1;
                }

                if (i >= source.Length || source[i] == '\n')
                {
                    issues.Add(new ScriptIssue(start, "unfinished string"));
                }
                else
                {
                    i++;
                }
            }
            else if (c == '[' && LongBracket(source, i) is >= 0 and var level)
            {
                int start = line;
                i = SkipLong(source, i, level, ref line);
                if (i < 0)
                {
                    issues.Add(new ScriptIssue(start, "unfinished long string"));
                    break;
                }
            }
            else if (c is '(' or '[' or '{')
            {
                brackets.Push((c, line));
                i++;
            }
            else if (c is ')' or ']' or '}')
            {
                char open = c == ')' ? '(' : c == ']' ? '[' : '{';
                if (brackets.Count == 0 || brackets.Peek().Open != open)
                {
                    issues.Add(new ScriptIssue(line, $"unexpected '{c}'"));
                }
                else
                {
                    brackets.Pop();
                }

                i++;
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_'))
                {
                    i++;
                }

                string word = source[start..i];
                switch (word)
                {
                    case "function" or "if" or "do" or "repeat":
                        blocks.Push((word, line));
                        break;
                    case "end":
                        if (blocks.Count == 0 || blocks.Peek().Word == "repeat")
                        {
                            issues.Add(new ScriptIssue(line, "'end' without a block to close"));
                        }
                        else
                        {
                            blocks.Pop();
                        }

                        break;
                    case "until":
                        if (blocks.Count == 0 || blocks.Peek().Word != "repeat")
                        {
                            issues.Add(new ScriptIssue(line, "'until' without 'repeat'"));
                        }
                        else
                        {
                            blocks.Pop();
                        }

                        break;
                }
            }
            else
            {
                i++;
            }
        }

        foreach ((string word, int at) in blocks)
        {
            issues.Add(new ScriptIssue(at, $"'{word}' is not closed with '{(word == "repeat" ? "until" : "end")}'"));
        }

        foreach ((char open, int at) in brackets)
        {
            issues.Add(new ScriptIssue(at, $"'{open}' is not closed"));
        }

        var sorted = issues.ToImmutable().Sort((a, b) => a.Line.CompareTo(b.Line));
        return sorted;
    }

    private static bool At(string s, int i, string what) => string.CompareOrdinal(s, i, what, 0, what.Length) == 0;

    // [[ ]] or [==[ ]==]: returns the level (number of '='), or −1.
    private static int LongBracket(string s, int i)
    {
        if (i >= s.Length || s[i] != '[')
        {
            return -1;
        }

        int j = i + 1, level = 0;
        while (j < s.Length && s[j] == '=')
        {
            level++;
            j++;
        }

        return j < s.Length && s[j] == '[' ? level : -1;
    }

    private static int SkipLong(string s, int i, int level, ref int line)
    {
        string close = "]" + new string('=', level) + "]";
        int j = i + level + 2;
        while (j < s.Length)
        {
            if (s[j] == '\n')
            {
                line++;
            }
            else if (At(s, j, close))
            {
                return j + close.Length;
            }

            j++;
        }

        return -1;
    }
}
