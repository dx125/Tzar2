using System.Collections.Generic;

namespace Tzar2.Content.Vfs;

/// <summary>A mountable set of files addressed by normalised content paths (see <see cref="ContentPath"/>).</summary>
public interface IContentSource
{
    /// <summary>Display name for reports (folder or package file).</summary>
    string Name { get; }

    /// <summary>All file paths, normalised and sorted ordinally.</summary>
    IReadOnlyList<string> Paths { get; }

    /// <summary>Whether the source holds <paramref name="path"/> (normalised).</summary>
    bool Contains(string path);

    /// <summary>Reads a file.</summary>
    /// <exception cref="System.IO.FileNotFoundException">The path is not in this source.</exception>
    byte[] ReadAllBytes(string path);

    /// <summary>Size of a file in bytes.</summary>
    long GetSize(string path);
}
