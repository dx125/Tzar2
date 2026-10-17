using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Tzar2.Import.Editions;

/// <summary>A reference file whose local copy differs.</summary>
public sealed record ModifiedFile(string Path, long ExpectedSize, long ActualSize, string ExpectedMd5, string? ActualMd5)
{
    /// <summary>Whether the sizes already differ (the MD5 is then not computed).</summary>
    public bool SizeDiffers => ExpectedSize != ActualSize;
}

/// <summary>Result of comparing a local install with a <see cref="ReferenceManifest"/>. All lists are sorted by path (ordinal).</summary>
public sealed record InstallVerificationReport(
    IReadOnlyList<string> Identical,
    IReadOnlyList<ModifiedFile> Modified,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Extra,
    IReadOnlyList<string> Ignored)
{
    /// <summary>Every reference file is present and byte-identical (extra files are allowed).</summary>
    public bool MatchesReference => Modified.Count == 0 && Missing.Count == 0;
}

/// <summary>Options for <see cref="InstallVerifier"/>.</summary>
public sealed class InstallVerifierOptions
{
    /// <summary>
    /// File-name prefixes of root-level files that are neither compared nor reported as extra. Default: GOG Galaxy
    /// metadata (<c>goggame-*</c>), which the reference manifest excludes (docs/original-build/README.md §2).
    /// </summary>
    public IReadOnlyList<string> IgnoredRootFilePrefixes { get; init; } = ["goggame"];
}

/// <summary>
/// Compares a local original install with a reference manifest by path, size and MD5 (TDD REQ-RE-03, <c>t2 verify-original</c>).
/// Paths match case-insensitively, as the original is a Windows game; an exact-case match wins when several local
/// files differ only in case.
/// </summary>
public static class InstallVerifier
{
    /// <summary>Verifies <paramref name="installRoot"/> against <paramref name="manifest"/>.</summary>
    /// <exception cref="DirectoryNotFoundException"><paramref name="installRoot"/> does not exist.</exception>
    public static InstallVerificationReport Verify(string installRoot, ReferenceManifest manifest, InstallVerifierOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(installRoot);
        ArgumentNullException.ThrowIfNull(manifest);
        options ??= new InstallVerifierOptions();
        if (!Directory.Exists(installRoot))
        {
            throw new DirectoryNotFoundException($"Install directory '{installRoot}' does not exist.");
        }

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.None,
            IgnoreInaccessible = false,
        };

        var ignored = new List<string>();
        var localByPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(installRoot, "*", enumeration))
        {
            string relative = Path.GetRelativePath(installRoot, file).Replace('\\', '/');
            if (IsIgnored(relative, options))
            {
                ignored.Add(relative);
                continue;
            }

            if (!localByPath.TryGetValue(relative, out List<string>? candidates))
            {
                candidates = [];
                localByPath.Add(relative, candidates);
            }

            candidates.Add(relative);
        }

        var identical = new List<string>();
        var modified = new List<ModifiedFile>();
        var missing = new List<string>();
        foreach (ManifestEntry entry in manifest.Entries)
        {
            if (!localByPath.TryGetValue(entry.Path, out List<string>? candidates) || candidates.Count == 0)
            {
                missing.Add(entry.Path);
                continue;
            }

            string match = candidates.Contains(entry.Path, StringComparer.Ordinal)
                ? entry.Path
                : candidates.Min(StringComparer.Ordinal)!;
            candidates.Remove(match);

            string fullPath = Path.Combine(installRoot, match);
            long size = new FileInfo(fullPath).Length;
            if (size != entry.Size)
            {
                modified.Add(new ModifiedFile(entry.Path, entry.Size, size, entry.Md5, null));
                continue;
            }

            string md5 = ComputeMd5(fullPath);
            if (md5 == entry.Md5)
            {
                identical.Add(entry.Path);
            }
            else
            {
                modified.Add(new ModifiedFile(entry.Path, entry.Size, size, entry.Md5, md5));
            }
        }

        var extra = localByPath.Values.SelectMany(paths => paths).ToList();

        identical.Sort(StringComparer.Ordinal);
        modified.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        missing.Sort(StringComparer.Ordinal);
        extra.Sort(StringComparer.Ordinal);
        ignored.Sort(StringComparer.Ordinal);
        return new InstallVerificationReport(identical, modified, missing, extra, ignored);
    }

    /// <summary>MD5 of a file as lowercase hex.</summary>
    public static string ComputeMd5(string path)
    {
        using FileStream stream = File.OpenRead(path);
#pragma warning disable CA5351 // MD5 is what the GOG manifests use; it identifies files, it does not secure anything.
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
#pragma warning restore CA5351
    }

    private static bool IsIgnored(string relativePath, InstallVerifierOptions options)
    {
        if (relativePath.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string prefix in options.IgnoredRootFilePrefixes)
        {
            if (relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
