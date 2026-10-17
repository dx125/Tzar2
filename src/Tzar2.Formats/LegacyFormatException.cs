using System;

namespace Tzar2.Formats;

/// <summary>
/// The only exception the decoders of this assembly throw for malformed input (truncated data, bad magic, values out
/// of range). Callers that import untrusted files catch this type; any other exception is a decoder bug.
/// </summary>
public sealed class LegacyFormatException : FormatException
{
    /// <summary>Creates an exception for <paramref name="format"/> (e.g. <c>"RLE"</c>).</summary>
    public LegacyFormatException(string format, string message)
        : base($"{format}: {message}")
    {
        Format = format;
    }

    /// <summary>Creates an exception without a format name.</summary>
    public LegacyFormatException()
        : this("unknown", "malformed data")
    {
    }

    /// <summary>Creates an exception without a format name.</summary>
    public LegacyFormatException(string message)
        : this("unknown", message)
    {
    }

    /// <summary>Creates an exception without a format name.</summary>
    public LegacyFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
        Format = "unknown";
    }

    /// <summary>Short name of the format whose data was malformed.</summary>
    public string Format { get; } = "unknown";
}

/// <summary>A non-fatal irregularity found while decoding (e.g. a surplus row or an unterminated INI section).</summary>
/// <param name="Line">1-based line, record or row number, or 0 when it does not apply.</param>
/// <param name="Message">Human-readable description.</param>
public readonly record struct FormatWarning(int Line, string Message);
