using System;
using System.IO;
using System.Linq;
using Tzar2.Import.Editions;
using Xunit;

namespace Tzar2.Import.Tests.Editions;

/// <summary>Finding the original install for the first-run import (TDD §12.2, ADR 0241).</summary>
public sealed class InstallLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tzar2-locator-" + Guid.NewGuid().ToString("N"));

    public InstallLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_folder_is_an_install_when_it_holds_the_games_executable_in_any_case()
    {
        string gog = Install("gog", "Tzar.exe");
        string repack = Install("repack", "TZARHD.EXE");
        string other = Install("other", "readme.txt");
        Assert.True(InstallLocator.IsInstall(gog));
        Assert.True(InstallLocator.IsInstall(repack));
        Assert.False(InstallLocator.IsInstall(other));
        Assert.False(InstallLocator.IsInstall(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Sources_are_searched_in_order_and_an_install_is_listed_once()
    {
        string gog = Install("GOG Games/Tzar", "Tzar.exe");
        string common = Install("Games/Tzar", "Tzar.exe");
        var search = new InstallSearch
        {
            Explicit = [gog + Path.DirectorySeparatorChar],
            GogPaths = [gog, Path.Combine(_root, "nowhere")],
            CommonPaths = [common, Install("Program Files/Tzar", "setup.log")],
        };

        var found = InstallLocator.Find(search);

        Assert.Equal([(Path.GetFullPath(gog), "TZAR2_ORIGIN"), (Path.GetFullPath(common), "folder")], found.Select(f => (f.Path, f.Source)));
    }

    [Fact]
    public void Steam_installs_come_from_the_app_manifest_in_every_library()
    {
        string steam = Install("Steam/steamapps", "libraryfolders.vdf");
        string library = Path.Combine(_root, "SteamLibrary");
        Directory.CreateDirectory(Path.Combine(library, "steamapps"));
        // Valve's KeyValues escape backslashes; the library on another drive holds the game.
        File.WriteAllText(Path.Combine(steam, "libraryfolders.vdf"), "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + Path.GetDirectoryName(steam)!.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + library.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"\n\t}\n}\n");
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_825730.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"825730\"\n\t\"installdir\"\t\t\"Tzar\"\n}\n");
        string game = Install("SteamLibrary/steamapps/common/Tzar", "Tzar.exe");

        Assert.Equal([Path.Combine(library, "steamapps", "common", "Tzar")], InstallLocator.SteamInstalls(Path.GetDirectoryName(steam)!));
        var found = InstallLocator.Find(new InstallSearch { SteamRoots = [Path.GetDirectoryName(steam)!] });
        Assert.Equal(Path.GetFullPath(game), Assert.Single(found).Path);
        Assert.Equal("Steam", found[0].Source);
    }

    [Fact]
    public void Vdf_values_unescape_backslashes_and_quotes()
        => Assert.Equal(["D:\\Steam Library", "a\"b"], InstallLocator.VdfValues("\"path\" \"D:\\\\Steam Library\"\n\"PATH\"\t\"a\\\"b\"", "path"));

    private string Install(string relative, string file)
    {
        string dir = Path.Combine(_root, relative);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), string.Empty);
        return dir;
    }
}
