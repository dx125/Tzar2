using System;
using System.IO;
using Xunit;

namespace Tzar2.Tests.Shared;

/// <summary>
/// Locations of local-only test inputs. The original install is copyrighted and never in the repository
/// (REQ-LEGAL-01): tests that need it read <c>TZAR2_ORIGIN</c> (or <c>TZAR2_ORIGINAL_PATH</c>, TDD §22.5) and are
/// skipped when it is unset, so public CI stays green.
/// </summary>
internal static class OriginPaths
{
    /// <summary>The original install, or null.</summary>
    public static string? Origin
    {
        get
        {
            string? path = Environment.GetEnvironmentVariable("TZAR2_ORIGIN");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Environment.GetEnvironmentVariable("TZAR2_ORIGINAL_PATH");
            }

            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// Golden sprites produced by <c>tools/re/make_goldens.py</c>: <c>TZAR2_GOLDENS</c> or
    /// <c>tests/local-goldens/sprites</c> (git-ignored).
    /// </summary>
    public static string GoldensDirectory
    {
        get
        {
            string? path = Environment.GetEnvironmentVariable("TZAR2_GOLDENS");
            return !string.IsNullOrWhiteSpace(path) ? path : Path.Combine(RepositoryRoot, "tests", "local-goldens", "sprites");
        }
    }

    /// <summary>The directory containing <c>Tzar2.sln</c>.</summary>
    public static string RepositoryRoot
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Tzar2.sln")))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException("Could not find Tzar2.sln above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>The original install; only call from tests marked <see cref="OriginFactAttribute"/>.</summary>
    public static string RequireOrigin() => Origin ?? throw new InvalidOperationException("TZAR2_ORIGIN is not set.");
}

/// <summary>A fact that needs the original install; skipped when <c>TZAR2_ORIGIN</c> is unset.</summary>
internal sealed class OriginFactAttribute : FactAttribute
{
    public OriginFactAttribute()
    {
        if (OriginPaths.Origin is null)
        {
            Skip = "Needs the original install: set TZAR2_ORIGIN to a GOG v1.10 EN folder.";
        }
    }
}

/// <summary>A theory that needs the original install; skipped when <c>TZAR2_ORIGIN</c> is unset.</summary>
internal sealed class OriginTheoryAttribute : TheoryAttribute
{
    public OriginTheoryAttribute()
    {
        if (OriginPaths.Origin is null)
        {
            Skip = "Needs the original install: set TZAR2_ORIGIN to a GOG v1.10 EN folder.";
        }
    }
}

/// <summary>A fact that needs the original install and the local goldens.</summary>
internal sealed class GoldenFactAttribute : FactAttribute
{
    public GoldenFactAttribute()
    {
        if (OriginPaths.Origin is null)
        {
            Skip = "Needs the original install: set TZAR2_ORIGIN to a GOG v1.10 EN folder.";
        }
        else if (!File.Exists(Path.Combine(OriginPaths.GoldensDirectory, "goldens.json")))
        {
            Skip = $"No goldens in {OriginPaths.GoldensDirectory}: run python3 -I tools/re/make_goldens.py $TZAR2_ORIGIN {OriginPaths.GoldensDirectory}";
        }
    }
}
