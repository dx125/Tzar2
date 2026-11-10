using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Editing;
using Tzar2.Game.Campaigns;

namespace Tzar2.Game.Editing;

/// <summary>
/// The player's own maps and campaigns (ADR 0286): the <c>.t2map</c> and <c>.t2camp</c> files of the user folders
/// (<see cref="Folders"/>: the editor saves there by default, the converter writes to <c>imported</c>), read for the
/// single-player menus and the multiplayer lobby. A map's id is its path under the user folder without the extension
/// (<c>maps/ford</c>), so it never collides with an original map's (the file name alone). A file that does not read,
/// or a map with a blocking finding, is reported in the problems and skipped.
/// </summary>
public static class UserLibrary
{
    /// <summary>The folder of the player's maps.</summary>
    public const string MapsFolder = "maps";

    /// <summary>The folder of the player's campaigns.</summary>
    public const string CampaignsFolder = "campaigns";

    /// <summary>The folder the converter writes to.</summary>
    public const string ImportedFolder = "imported";

    /// <summary>The folders read, in this order (only their top level).</summary>
    public static readonly ImmutableArray<string> Folders = [MapsFolder, CampaignsFolder, ImportedFolder];

    /// <summary>The maps of the user folders under <paramref name="root"/>, by title.</summary>
    /// <param name="root">The user data folder.</param>
    /// <param name="rules">Terrain rules by terrain-set id (as the editor's).</param>
    /// <param name="ruleset">The ruleset the maps are checked against.</param>
    /// <param name="problems">Receives a line per skipped file.</param>
    public static List<CustomMapEntry> Maps(string root, Func<string?, TerrainRules> rules, Ruleset ruleset, ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(problems);
        var maps = new List<CustomMapEntry>();
        foreach ((string id, string path) in Files(root, ".t2map"))
        {
            try
            {
                MapDocument doc = MapPackage.Read(File.ReadAllBytes(path), rules, Path.GetFileName(path));
                if (Playtest.Check(doc, ruleset).Issues.FirstOrDefault(i => i.Severity == ValidationSeverity.Blocking) is { } blocking)
                {
                    problems.Add($"{id}: {blocking.Message}");
                    continue;
                }

                MissionContent mission = Playtest.Mission(doc);
                MapProperties p = mission.Scenario.Properties;
                maps.Add(new CustomMapEntry(id, p.Title.Length > 0 ? p.Title : Path.GetFileNameWithoutExtension(path), p.Description, mission));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                problems.Add($"{id}: {e.Message}");
            }
        }

        maps.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase) is var c and not 0 ? c : string.CompareOrdinal(a.Id, b.Id));
        return maps;
    }

    /// <summary>The campaigns of the user folders under <paramref name="root"/>, by name; their maps are built when a mission starts.</summary>
    /// <param name="root">The user data folder.</param>
    /// <param name="rules">Terrain rules by terrain-set id.</param>
    /// <param name="problems">Receives a line per skipped file.</param>
    public static List<ICampaignSource> Campaigns(string root, Func<string?, TerrainRules> rules, ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(problems);
        var campaigns = new List<ICampaignSource>();
        foreach ((string id, string path) in Files(root, ".t2camp"))
        {
            try
            {
                campaigns.Add(new CampaignPlaytest(CampaignPackage.Read(File.ReadAllBytes(path), rules, Path.GetFileName(path))));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                problems.Add($"{id}: {e.Message}");
            }
        }

        campaigns.Sort((a, b) => string.Compare(a.Campaign.Name, b.Campaign.Name, StringComparison.OrdinalIgnoreCase));
        return campaigns;
    }

    // The files with an extension in the folders, as (id, path), in a fixed order.
    private static List<(string Id, string Path)> Files(string root, string extension)
    {
        var files = new List<(string, string)>();
        foreach (string folder in Folders)
        {
            string dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            string[] found = Directory.GetFiles(dir, "*" + extension, SearchOption.TopDirectoryOnly);
            Array.Sort(found, StringComparer.OrdinalIgnoreCase);
            foreach (string path in found)
            {
                files.Add((folder + "/" + Path.GetFileNameWithoutExtension(path), path));
            }
        }

        return files;
    }
}
