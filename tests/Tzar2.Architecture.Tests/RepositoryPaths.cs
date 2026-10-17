using System;
using System.IO;

namespace Tzar2.Architecture.Tests;

internal static class RepositoryPaths
{
    /// <summary>The repository root (the directory containing Tzar2.sln), found from the test binaries upward.</summary>
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
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
