using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tzar2.Tools.Cli.Fidelity.Capture;

/// <summary>
/// The private copy of the install a session runs against (ADR 0030): the operator's install is never modified.
/// The copy receives the session's <c>tzar.ini</c>, the optional DirectDraw wrapper and the game's snapshots, and is
/// writable without administrator rights or UAC file virtualisation. Re-syncs copy only changed files.
/// </summary>
internal static class WorkCopy
{
    /// <summary>Top-level folders of the install that are not copied (runtime output, not game data).</summary>
    public static readonly string[] SkippedFolders = ["ScrnShots", "Shots", "Screenshots", "Save", "Logs", "__support"];

    public sealed record SyncResult(int Copied, int Unchanged, long BytesCopied);

    public static SyncResult Sync(string source, string target)
    {
        if (Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar))
        {
            throw new ArgumentException("The work copy must not be the install itself.", nameof(target));
        }

        Directory.CreateDirectory(target);
        int copied = 0, unchanged = 0;
        long bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.System, IgnoreInaccessible = false };
        foreach (string file in Directory.EnumerateFiles(source, "*", options))
        {
            string relative = Path.GetRelativePath(source, file);
            string first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (relative.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                SkippedFolders.Any(s => string.Equals(s, first, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string destination = Path.Combine(target, relative);
            var src = new FileInfo(file);
            var dst = new FileInfo(destination);
            if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc)
            {
                unchanged++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            File.SetLastWriteTimeUtc(destination, src.LastWriteTimeUtc);
            copied++;
            bytes += src.Length;
        }

        return new SyncResult(copied, unchanged, bytes);
    }

    /// <summary>Finds the game executable (<c>Tzar.exe</c>, any letter case) in an install.</summary>
    public static string? FindExe(string install) => Directory.Exists(install)
        ? Directory.EnumerateFiles(install).FirstOrDefault(f => string.Equals(Path.GetFileName(f), "Tzar.exe", StringComparison.OrdinalIgnoreCase))
        : null;

    /// <summary>Files of the install that are DirectDraw wrappers or their settings.</summary>
    public static IReadOnlyList<string> WrapperFiles(string install) => Directory.Exists(install)
        ? Directory.EnumerateFiles(install)
            .Where(f => Path.GetFileName(f).ToLowerInvariant() is "ddraw.dll" or "ddraw.ini" or "dxwrapper.dll" or "dxwrapper.ini" or "d3d9.dll" or "dgvoodoo.conf")
            .ToList()
        : [];
}
