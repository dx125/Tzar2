using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Tzar2.Import.Editions;
using Tzar2.Import.Pipeline;

namespace Tzar2.Client.Logic;

/// <summary>
/// The first-run import (TDD §12, REQ-IMP; ADR 0241), Godot-free: find the original install on this machine, import it
/// into the user's data (REQ-PLAT-02: never next to the executable, so an uninstall keeps it) and hand back the package.
/// </summary>
public static class FirstRunImport
{
    /// <summary>The folder under the user's data that holds the imported package and its report.</summary>
    public const string Folder = "import";

    /// <summary>Where the import writes for the user's data folder <paramref name="userDir"/>.</summary>
    public static string OutputDirectory(string userDir) => Path.Combine(userDir, Folder);

    /// <summary>The installs found on this machine (or by <paramref name="search"/>).</summary>
    public static IReadOnlyList<FoundInstall> FindInstalls(InstallSearch? search = null)
        => InstallLocator.Find(search ?? InstallSearch.ForThisMachine());

    /// <summary>
    /// Imports <paramref name="origin"/> on a worker thread, leaving a core free for the window; <paramref name="log"/>
    /// receives progress lines (from the worker thread). The original install is only read (REQ-IMP-04).
    /// </summary>
    public static Task<ImportResult> RunAsync(string origin, string userDir, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(userDir);
        var options = new ImportOptions
        {
            OriginPath = origin,
            OutputDirectory = OutputDirectory(userDir),
            MaxParallelism = Math.Max(1, Environment.ProcessorCount - 1),
        };
        return Task.Run(() => ImportPipeline.Run(options, log));
    }

    /// <summary>The line shown when an import ends.</summary>
    public static string Summary(ImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ImportReport report = result.Report;
        return report.Succeeded
            ? string.Create(CultureInfo.InvariantCulture, $"Imported {report.PackageEntries} files in {report.TotalTime.TotalSeconds:0} s.")
            : $"The import failed with {report.Errors.Count} error(s); see {Path.Combine(Path.GetDirectoryName(result.PackagePath) ?? string.Empty, ImportPipeline.ReportText)}.";
    }
}
