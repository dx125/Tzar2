using System;

namespace Tzar2.Import.Pipeline;

/// <summary>What the importer does with a file of the original install.</summary>
public enum SourceKind
{
    /// <summary>Not content (executables, manuals, installer and GOG files, user settings).</summary>
    Ignored,

    /// <summary>Content that M1 does not convert yet (movies, demos); listed in the report.</summary>
    Deferred,

    /// <summary><c>.RLE</c> sprite sheet.</summary>
    Sprite,

    /// <summary><c>.DAT</c> sprite metadata.</summary>
    Dat,

    /// <summary><c>.BMP</c> bitmap.</summary>
    Bitmap,

    /// <summary><c>GAME.PAL</c>.</summary>
    Palette,

    /// <summary><c>.FNT</c> font.</summary>
    Font,

    /// <summary><c>.WAV</c> sound.</summary>
    Sound,

    /// <summary><c>.MID</c> music.</summary>
    Music,

    /// <summary><c>.INI</c> table.</summary>
    Ini,

    /// <summary>Tab-separated unit/building table (<c>DATA/* UNITS.TXT</c>, <c>* BUILDINGS.TXT</c>).</summary>
    Tsv,

    /// <summary>Other text (notes, help, AI scripts), stored as UTF-8.</summary>
    Text,

    /// <summary>HPFS map or campaign (validated; converted in M4).</summary>
    Container,
}

/// <summary>Maps normalised install paths to <see cref="SourceKind"/>.</summary>
public static class SourceClassifier
{
    /// <summary>Classifies <paramref name="path"/> (normalised, lower-case).</summary>
    public static SourceKind Classify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith("__support/", StringComparison.Ordinal) || path == "tzar.ini" || path.StartsWith("goggame", StringComparison.Ordinal))
        {
            return SourceKind.Ignored;
        }

        int dot = path.LastIndexOf('.');
        string ext = dot >= 0 && dot > path.LastIndexOf('/') ? path[dot..] : string.Empty;
        return ext switch
        {
            ".rle" => SourceKind.Sprite,
            ".dat" when path.StartsWith("images/", StringComparison.Ordinal) => SourceKind.Dat,
            ".bmp" => SourceKind.Bitmap,
            ".pal" => SourceKind.Palette,
            ".fnt" => SourceKind.Font,
            ".wav" => SourceKind.Sound,
            ".mid" => SourceKind.Music,
            ".ini" => SourceKind.Ini,
            ".txt" when path.StartsWith("data/", StringComparison.Ordinal)
                && (path.EndsWith(" units.txt", StringComparison.Ordinal) || path.EndsWith(" buildings.txt", StringComparison.Ordinal)) => SourceKind.Tsv,
            ".txt" or ".ai" or ".ais" => SourceKind.Text,
            ".wmp" or ".wcm" => SourceKind.Container,
            ".ddv" or ".wdm" => SourceKind.Deferred,
            _ => SourceKind.Ignored,
        };
    }

    /// <summary>
    /// The package path an asset of <paramref name="kind"/> from <paramref name="source"/> is stored at, or null when the
    /// kind produces no single file. Art under <c>IMAGES/</c> lives in <c>art/</c> keyed by its IMAGES.INI logical path.
    /// </summary>
    public static string? PackagePath(string source, SourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(source);
        return kind switch
        {
            SourceKind.Sprite => ArtPath(source, ".t2spr"),
            SourceKind.Bitmap => ArtPath(source, ".t2img"),
            SourceKind.Palette => "palettes/" + Content.ContentPath.ChangeExtension(LastSegment(source), ".json"),
            SourceKind.Font => "fonts/" + Content.ContentPath.ChangeExtension(LastSegment(source), ".json"),
            SourceKind.Sound => source.StartsWith("sound/", StringComparison.Ordinal) ? source : "sound-extra/" + source,
            SourceKind.Music => source.StartsWith("music/", StringComparison.Ordinal) ? source : "music-extra/" + source,
            SourceKind.Ini or SourceKind.Tsv => "tables/" + source + ".json",
            SourceKind.Text => "text/" + source,
            _ => null,
        };
    }

    private static string ArtPath(string source, string extension)
        => source.StartsWith("images/", StringComparison.Ordinal)
            ? "art/" + Content.ContentPath.ChangeExtension(source["images/".Length..], extension)
            : "art-extra/" + Content.ContentPath.ChangeExtension(source, extension);

    private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];
}
