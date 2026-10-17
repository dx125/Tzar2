using System.IO;
using Tzar2.Import.Editions;
using Xunit;

namespace Tzar2.Import.Tests.Editions;

public class InstallVerifierTests
{
    private static ReferenceManifest ManifestOf(SyntheticInstall install)
        => ReferenceManifest.Parse(new StringReader(install.ManifestText()), "synthetic.tsv");

    [Fact]
    public void Identical_install_matches()
    {
        using var install = new SyntheticInstall().Add("Tzar.exe", "exe").Add("DATA/RES.INI", "[res]").Add("IMAGES/UNIT/A.RLE", "rle");
        InstallVerificationReport report = InstallVerifier.Verify(install.Root, ManifestOf(install));
        Assert.True(report.MatchesReference);
        Assert.Equal(["DATA/RES.INI", "IMAGES/UNIT/A.RLE", "Tzar.exe"], report.Identical);
        Assert.Empty(report.Modified);
        Assert.Empty(report.Missing);
        Assert.Empty(report.Extra);
    }

    [Fact]
    public void Reports_modified_missing_and_extra_files()
    {
        using var install = new SyntheticInstall().Add("a.txt", "aaaa").Add("b.txt", "bbbb").Add("c.txt", "cccc").Add("d/e.txt", "e");
        ReferenceManifest manifest = ManifestOf(install);
        install.Write("a.txt", "AAAA"u8.ToArray()); // same size, different content
        install.Write("b.txt", "bbbbb"u8.ToArray()); // different size
        install.Delete("c.txt");
        install.Write("extra/new.txt", "x"u8.ToArray());

        InstallVerificationReport report = InstallVerifier.Verify(install.Root, manifest);
        Assert.False(report.MatchesReference);
        Assert.Equal(["d/e.txt"], report.Identical);
        Assert.Equal(["c.txt"], report.Missing);
        Assert.Equal(["extra/new.txt"], report.Extra);
        Assert.Collection(
            report.Modified,
            a =>
            {
                Assert.Equal("a.txt", a.Path);
                Assert.False(a.SizeDiffers);
                Assert.NotNull(a.ActualMd5);
                Assert.NotEqual(a.ExpectedMd5, a.ActualMd5);
            },
            b =>
            {
                Assert.Equal("b.txt", b.Path);
                Assert.True(b.SizeDiffers);
                Assert.Equal(5, b.ActualSize);
                Assert.Null(b.ActualMd5);
            });
    }

    [Fact]
    public void Extra_files_alone_still_match_the_reference()
    {
        using var install = new SyntheticInstall().Add("a.txt", "a");
        ReferenceManifest manifest = ManifestOf(install);
        install.Write("readme.txt", "hd patch"u8.ToArray());
        InstallVerificationReport report = InstallVerifier.Verify(install.Root, manifest);
        Assert.True(report.MatchesReference);
        Assert.Equal(["readme.txt"], report.Extra);
    }

    [Fact]
    public void Paths_match_case_insensitively()
    {
        using var install = new SyntheticInstall().Add("Campaign/TZARTUTORIAL.WCM", "campaign");
        ReferenceManifest manifest = ManifestOf(install);
        install.Delete("Campaign/TZARTUTORIAL.WCM");
        install.Write("campaign/TzarTutorial.wcm", "campaign"u8.ToArray());

        InstallVerificationReport report = InstallVerifier.Verify(install.Root, manifest);
        Assert.True(report.MatchesReference);
        Assert.Equal(["Campaign/TZARTUTORIAL.WCM"], report.Identical);
    }

    [Fact]
    public void Gog_galaxy_metadata_is_ignored()
    {
        using var install = new SyntheticInstall().Add("a.txt", "a");
        ReferenceManifest manifest = ManifestOf(install);
        install.Write("goggame-1207659252.info", "{}"u8.ToArray());
        install.Write("sub/goggame-x.txt", "not root"u8.ToArray());

        InstallVerificationReport report = InstallVerifier.Verify(install.Root, manifest);
        Assert.Equal(["goggame-1207659252.info"], report.Ignored);
        Assert.Equal(["sub/goggame-x.txt"], report.Extra);
    }

    [Fact]
    public void Missing_directory_throws()
    {
        using var install = new SyntheticInstall().Add("a.txt", "a");
        Assert.Throws<DirectoryNotFoundException>(() => InstallVerifier.Verify(Path.Combine(install.Root, "nope"), ManifestOf(install)));
    }

    [Fact]
    public void ComputeMd5_matches_known_vector()
    {
        using var install = new SyntheticInstall().Add("abc.txt", "abc");
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", InstallVerifier.ComputeMd5(Path.Combine(install.Root, "abc.txt")));
    }
}
