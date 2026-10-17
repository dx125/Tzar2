using System;
using System.Collections.Generic;
using System.IO;

namespace Tzar2.Import.Editions;

/// <summary>Result of <see cref="EditionDetector.Detect"/>.</summary>
/// <param name="Edition">The best-matching known edition, or null when nothing matches well enough.</param>
/// <param name="SizeMatchPerMille">Share of the edition's files present with the expected size, in per mille.</param>
/// <param name="Verification">MD5 comparison against the edition's manifest (null when no edition matched).</param>
public sealed record EditionMatch(KnownEdition? Edition, int SizeMatchPerMille, InstallVerificationReport? Verification)
{
    /// <summary>Whether every file of the edition is present and byte-identical.</summary>
    public bool IsExact => Edition is not null && Verification is { MatchesReference: true };
}

/// <summary>
/// Fingerprints an install (REQ-IMP-01, RE-13 baseline): every known edition is scored by how many of its files are
/// present with the right size (cheap, no hashing); the best one above <see cref="MinimumPerMille"/> is then verified by
/// MD5 with <see cref="InstallVerifier"/>. Ties go to the edition with more files.
/// </summary>
public static class EditionDetector
{
    /// <summary>Minimum size-match share (per mille) for an edition to be reported.</summary>
    public const int MinimumPerMille = 900;

    /// <summary>Detects the edition of <paramref name="installRoot"/> among <paramref name="editions"/> (default: <see cref="EditionCatalog.Editions"/>).</summary>
    public static EditionMatch Detect(string installRoot, IReadOnlyList<KnownEdition>? editions = null)
    {
        ArgumentNullException.ThrowIfNull(installRoot);
        editions ??= EditionCatalog.Editions;
        if (!Directory.Exists(installRoot))
        {
            throw new DirectoryNotFoundException($"Install directory '{installRoot}' does not exist.");
        }

        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None };
        foreach (string file in Directory.EnumerateFiles(installRoot, "*", options))
        {
            sizes.TryAdd(Path.GetRelativePath(installRoot, file).Replace('\\', '/'), new FileInfo(file).Length);
        }

        KnownEdition? best = null;
        int bestScore = -1;
        foreach (KnownEdition edition in editions)
        {
            int matches = 0;
            foreach (ManifestEntry entry in edition.Manifest.Entries)
            {
                if (sizes.TryGetValue(entry.Path, out long size) && size == entry.Size)
                {
                    matches++;
                }
            }

            int score = edition.Manifest.Entries.Count == 0 ? 0 : (int)(matches * 1000L / edition.Manifest.Entries.Count);
            if (score > bestScore || (score == bestScore && best is not null && edition.Manifest.Entries.Count > best.Manifest.Entries.Count))
            {
                best = edition;
                bestScore = score;
            }
        }

        if (best is null || bestScore < MinimumPerMille)
        {
            return new EditionMatch(null, Math.Max(bestScore, 0), null);
        }

        return new EditionMatch(best, bestScore, InstallVerifier.Verify(installRoot, best.Manifest));
    }
}
