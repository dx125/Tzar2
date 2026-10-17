using System;
using System.IO;
using Tzar2.Import.Editions;
using Xunit;

namespace Tzar2.Import.Tests.Editions;

public class ReferenceManifestTests
{
    [Fact]
    public void Parses_entries_and_ignores_extra_columns()
    {
        const string Text = "path\tsize\tmd5\tprovenance\nDATA/A.INI\t12\t0123456789ABCDEF0123456789abcdef\tnote\n\nMaps/b.wmp\t0\td41d8cd98f00b204e9800998ecf8427e\t\n";
        ReferenceManifest manifest = ReferenceManifest.Parse(new StringReader(Text), "test.tsv");
        Assert.Equal("test.tsv", manifest.Name);
        Assert.Equal(2, manifest.Entries.Count);
        Assert.Equal(new ManifestEntry("DATA/A.INI", 12, "0123456789abcdef0123456789abcdef"), manifest.Entries[0]);
        Assert.Equal(12, manifest.TotalSize);
    }

    [Fact]
    public void Column_order_is_taken_from_the_header()
    {
        ReferenceManifest manifest = ReferenceManifest.Parse(new StringReader("md5\tpath\tsize\nd41d8cd98f00b204e9800998ecf8427e\tx\t0\n"), "t");
        Assert.Equal(new ManifestEntry("x", 0, "d41d8cd98f00b204e9800998ecf8427e"), manifest.Entries[0]);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("name\tsize\tmd5\n", ":1:")]
    [InlineData("path\tsize\tmd5\na\t1\n", ":2:")]
    [InlineData("path\tsize\tmd5\na\tx\td41d8cd98f00b204e9800998ecf8427e\n", "invalid size")]
    [InlineData("path\tsize\tmd5\na\t-1\td41d8cd98f00b204e9800998ecf8427e\n", "invalid size")]
    [InlineData("path\tsize\tmd5\na\t1\txyz\n", "invalid MD5")]
    [InlineData("path\tsize\tmd5\na\\b\t1\td41d8cd98f00b204e9800998ecf8427e\n", "relative path")]
    [InlineData("path\tsize\tmd5\n../a\t1\td41d8cd98f00b204e9800998ecf8427e\n", "relative path")]
    [InlineData("path\tsize\tmd5\n/a\t1\td41d8cd98f00b204e9800998ecf8427e\n", "relative path")]
    [InlineData("path\tsize\tmd5\na//b\t1\td41d8cd98f00b204e9800998ecf8427e\n", "relative path")]
    [InlineData("path\tsize\tmd5\n./a\t1\td41d8cd98f00b204e9800998ecf8427e\n", "relative path")]
    [InlineData("path\tsize\tmd5\na/./b\t1\td41d8cd98f00b204e9800998ecf8427e\n", "relative path")]
    [InlineData("path\tsize\tmd5\na/\t1\td41d8cd98f00b204e9800998ecf8427e\n", "relative path")]
    [InlineData("path\tsize\tmd5\n.\t1\td41d8cd98f00b204e9800998ecf8427e\n", "relative path")]
    [InlineData("path\tsize\tmd5\nA\t1\td41d8cd98f00b204e9800998ecf8427e\na\t1\td41d8cd98f00b204e9800998ecf8427e\n", "duplicate")]
    public void Malformed_manifests_are_rejected_with_location(string text, string messagePart)
    {
        FormatException ex = Assert.Throws<FormatException>(() => ReferenceManifest.Parse(new StringReader(text), "m.tsv"));
        Assert.Contains(messagePart, ex.Message, StringComparison.Ordinal);
    }
}
