using System.Linq;
using Tzar2.Client.Logic;
using Xunit;

namespace Tzar2.Client.Tests;

/// <summary>The files of the online help as the import stores them (invented texts in the original's form).</summary>
public class HelpTextTests
{
    [Fact]
    [Trait("Rule", "HUD-020")]
    public void The_topics_file_reads_as_sections_with_keys()
    {
        var sections = HelpText.Sections(";---\n;  UNITS\n\n[U_E_PEASANT]\nrollover = About = this\nbrief = Peasant\nbrief = second\n\r\n[B_E_CASTLE]\r\nicon = 2,1\nno equals here\n");
        Assert.Equal(["U_E_PEASANT", "B_E_CASTLE"], sections.Select(s => s.Name));
        Assert.Equal("About = this", sections[0].Keys["rollover"]);
        Assert.Equal("Peasant", sections[0].Keys["BRIEF"]); // the first value wins, keys ignore case
        Assert.Equal("2,1", sections[1].Keys["icon"]);
    }

    [Fact]
    [Trait("Rule", "SHELL-021")]
    [Trait("Rule", "HUD-020")]
    public void The_hot_keys_lines_carry_their_highlighted_keys()
    {
        var lines = HelpText.HotKeyLines("@1F1@2 - Menu screen\r\n\r\n@1F4@2 - Scores\n\n");
        Assert.Equal(["@1F1@2 - Menu screen", "@1F4@2 - Scores"], lines);
        Assert.Equal([(true, "F1"), (false, " - Menu screen")], HelpText.Parts(lines[0]));
        Assert.Equal([(false, "plain")], HelpText.Parts("plain"));
        Assert.Equal([(false, "a "), (true, "B"), (false, " c @3")], HelpText.Parts("a @1B@2 c @3"));
    }
}
