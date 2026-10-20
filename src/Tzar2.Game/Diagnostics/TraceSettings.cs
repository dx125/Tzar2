using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tzar2.Core.Diagnostics;

namespace Tzar2.Game.Diagnostics;

/// <summary>
/// The level of every trace area (ADR 0176). Written as text: a level for every area (<c>debug</c>), or a list of
/// <c>area=level</c> pairs where <c>*</c> stands for every area and later pairs win (<c>*=info,ai=verbose</c>); separators are
/// commas, semicolons, spaces and line breaks, and <c>#</c> starts a comment.
/// </summary>
public sealed class TraceSettings
{
    /// <summary>The environment variable that overrides the settings file.</summary>
    public const string EnvironmentVariable = "TZAR2_TRACE";

    private static readonly TraceArea[] Areas = Enum.GetValues<TraceArea>();
    private readonly LogLevel[] _levels;

    private TraceSettings(LogLevel[] levels) => _levels = levels;

    /// <summary>
    /// Debug for every area: until the first play-tests are understood, the trace errs on the side of too much (owner
    /// request, 2026-10-19). Verbose (every hit, every tick's hash) is opt-in.
    /// </summary>
    public static TraceSettings Default { get; } = Uniform(LogLevel.Debug);

    /// <summary>The level of an area.</summary>
    public LogLevel this[TraceArea area] => (int)area < _levels.Length ? _levels[(int)area] : LogLevel.Off;

    /// <summary>The same level for every area.</summary>
    public static TraceSettings Uniform(LogLevel level) => new([.. Areas.Select(_ => level)]);

    /// <summary>
    /// Parses settings text over <paramref name="fallback"/>; returns the result and the parts it could not read (they are
    /// ignored, so a typo never turns tracing off).
    /// </summary>
    public static (TraceSettings Settings, IReadOnlyList<string> Errors) Parse(string? text, TraceSettings fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        var levels = (LogLevel[])fallback._levels.Clone();
        var errors = new List<string>();
        // A '#' starts a comment that runs to the end of its line.
        IEnumerable<string> parts = (text ?? string.Empty).Split('\n')
            .Select(line => line.Split('#')[0])
            .SelectMany(line => line.Split([',', ';', ' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries));
        foreach (string raw in parts)
        {
            string part = raw.Trim();
            string[] kv = part.Split('=', 2);
            string areaText = kv.Length == 2 ? kv[0].Trim() : "*";
            string levelText = kv.Length == 2 ? kv[1].Trim() : kv[0].Trim();
            if (!Enum.TryParse(levelText, ignoreCase: true, out LogLevel level) || !Enum.IsDefined(level))
            {
                errors.Add(part);
                continue;
            }

            if (areaText == "*")
            {
                Array.Fill(levels, level);
            }
            else if (Enum.TryParse(areaText, ignoreCase: true, out TraceArea area) && Enum.IsDefined(area))
            {
                levels[(int)area] = level;
            }
            else
            {
                errors.Add(part);
            }
        }

        return (new TraceSettings(levels), errors);
    }

    /// <summary>The settings as text, every area spelled out (<c>session=debug,commands=debug,...</c>).</summary>
    public override string ToString() =>
        string.Join(",", Areas.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.ToString().ToLowerInvariant()}={this[a].ToString().ToLowerInvariant()}")));
}
