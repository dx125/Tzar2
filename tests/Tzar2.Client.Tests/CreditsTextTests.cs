using Tzar2.Client.Logic;
using Xunit;

namespace Tzar2.Client.Tests;

[Trait("Rule", "SHELL-020")]
public class CreditsTextTests
{
    [Fact]
    public void A_font_switch_holds_for_the_lines_after_it()
    {
        // Invented credits in the original's form.
        var lines = CreditsText.Parse("@2GAME\r\n@1A subtitle\n\n@0ROLE\n@1First Maker\nSecond Maker\n\n\n");
        Assert.Equal(
            [new(2, "GAME"), new(1, "A subtitle"), new(1, string.Empty), new(0, "ROLE"), new(1, "First Maker"), new(1, "Second Maker")],
            lines);
        Assert.Equal([new(0, "plain")], CreditsText.Parse("plain"));
        Assert.Equal([new(12, "x")], CreditsText.Parse("@12x"));
    }

    [Theory]
    [InlineData(@"LOCAL\FONTS\Arial14B_aa.fnt", "fonts/arial14b_aa.json")]
    [InlineData(@"LOCAL\FONTS\cgb_22b.fnt", "fonts/cgb_22b.json")]
    [InlineData("small", "fonts/small.json")]
    [InlineData("  ", null)]
    public void Fonts_of_creditsfnt_map_to_the_imported_fonts(string entry, string? asset)
        => Assert.Equal(asset, CreditsText.FontAsset(entry));
}
