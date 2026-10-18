using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Tzar2.AI.Legacy.Loading;

/// <summary>
/// Where script texts come from (REQ-ARCH-01: the AI does no I/O). The game passes a view of the content VFS (the
/// imported base package keeps the original scripts as <c>text/data/ai/*</c>, docs/formats/t2pkg.md); tests and tools
/// pass texts in memory. Names are looked up normalised (<see cref="AiScriptNames.Normalize"/>).
/// </summary>
public interface IAiScriptSource
{
    /// <summary>Reads a script by its normalised name (e.g. <c>e_common.ai</c>).</summary>
    /// <param name="name">The normalised name.</param>
    /// <param name="text">The text, or null when there is no such script.</param>
    /// <returns>Whether it exists.</returns>
    bool TryRead(string name, [NotNullWhen(true)] out string? text);
}

/// <summary>Script name rules: the original resolves includes case-insensitively relative to <c>DATA\AI</c>.</summary>
public static class AiScriptNames
{
    /// <summary>
    /// Lower-case ASCII, forward slashes, no leading <c>./</c> or slash, no <c>DATA/AI/</c> prefix
    /// (<c>"DATA\AI\E_Common.AI"</c> → <c>e_common.ai</c>).
    /// </summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            builder.Append(c == '\\' ? '/' : char.IsAsciiLetterUpper(c) ? (char)(c + 32) : c);
        }

        string s = builder.ToString();
        while (s.StartsWith("./", StringComparison.Ordinal) || s.StartsWith('/'))
        {
            s = s[(s.StartsWith('/') ? 1 : 2)..];
        }

        // "./" segments carry no meaning; ".." is kept for IsSafe to reject.
        var parts = new System.Collections.Generic.List<string>();
        foreach (string part in s.Split('/'))
        {
            if (part.Length > 0 && part != ".")
            {
                parts.Add(part);
            }
        }

        s = string.Join('/', parts);
        const string Prefix = "data/ai/";
        return s.StartsWith(Prefix, StringComparison.Ordinal) ? s[Prefix.Length..] : s;
    }

    /// <summary>Whether a normalised name stays inside the AI script folder (no <c>..</c> segment, not empty, no drive).</summary>
    public static bool IsSafe(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        if (normalized.Length == 0 || normalized.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string part in normalized.Split('/'))
        {
            if (part == "..")
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>An in-memory <see cref="IAiScriptSource"/>.</summary>
public sealed class MemoryAiScriptSource : IAiScriptSource
{
    private readonly SortedDictionary<string, string> _scripts = new(StringComparer.Ordinal);

    /// <summary>Adds or replaces a script.</summary>
    /// <param name="name">Any spelling of the name; it is normalised.</param>
    /// <param name="text">The text.</param>
    /// <returns>This source.</returns>
    public MemoryAiScriptSource Add(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _scripts[AiScriptNames.Normalize(name)] = text;
        return this;
    }

    /// <summary>The normalised names, in ordinal order.</summary>
    public IEnumerable<string> Names => _scripts.Keys;

    /// <inheritdoc />
    public bool TryRead(string name, [NotNullWhen(true)] out string? text) => _scripts.TryGetValue(AiScriptNames.Normalize(name), out text);
}

/// <summary>One entry of <c>AI.INI</c> <c>[scripts]</c>: the name shown in the lobby and the script file.</summary>
/// <param name="DisplayName">E.g. <c>Magic, Religion</c>.</param>
/// <param name="Script">The normalised script name, e.g. <c>magicrel.ais</c>.</param>
public sealed record AiScriptChoice(string DisplayName, string Script);

/// <summary>
/// <c>DATA/AI/AI.INI</c> (Appendix D.1): <c>[scripts]</c> maps display names to strategy scripts. Lines starting with
/// <c>;</c> are comments (the shipped file comments out <c>Deathmatch = Dm.ais</c>; the game uses <c>dm.ais</c> for
/// deathmatch anyway, and <c>random.ais</c> by default).
/// </summary>
public static class AiIni
{
    /// <summary>The script of a skirmish AI with no choice (Appendix D.1).</summary>
    public const string DefaultScript = "random.ais";

    /// <summary>The script of a deathmatch AI.</summary>
    public const string DeathmatchScript = "dm.ais";

    /// <summary>Parses the text of <c>AI.INI</c>; malformed lines are skipped.</summary>
    public static IReadOnlyList<AiScriptChoice> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<AiScriptChoice>();
        bool inScripts = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                inScripts = string.Equals(line.TrimEnd(), "[scripts]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            int eq = line.LastIndexOf('=');
            if (!inScripts || eq <= 0 || eq == line.Length - 1)
            {
                continue;
            }

            result.Add(new AiScriptChoice(line[..eq].Trim(), AiScriptNames.Normalize(line[(eq + 1)..].Trim())));
        }

        return result;
    }
}
