using System.Linq;
using Tzar2.Formats.Ini;
using Tzar2.Formats.Text;
using Tzar2.Formats.Tsv;
using Xunit;

namespace Tzar2.Formats.Tests;

public class LegacyTextTests
{
    [Fact]
    public void Decodes_windows_code_pages_with_built_in_tables()
    {
        Assert.Equal("A€ö\u0081", LegacyText.Decode([0x41, 0x80, 0xF6, 0x81], CodePage.Windows1252));
        Assert.Equal("АЯё", LegacyText.Decode([0xC0, 0xDF, 0xB8], CodePage.Windows1251));
        Assert.Equal("ŁąŚ", LegacyText.Decode([0xA3, 0xB9, 0x8C], CodePage.Windows1250));
    }

    [Theory]
    [InlineData(CodePage.Windows1250)]
    [InlineData(CodePage.Windows1251)]
    [InlineData(CodePage.Windows1252)]
    public void Every_byte_round_trips(CodePage codePage)
    {
        byte[] all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        string text = LegacyText.Decode(all, codePage);
        Assert.Equal(256, text.Distinct().Count());
        Assert.Equal(all, LegacyText.Encode(text, codePage));
    }
}

public class IniDocumentTests
{
    private const string Sample =
        "; header comment\r\n" +
        "[accept]\r\n" +
        "allunits\r\n" +
        "[E_Peon_Attack]\r\n" +
        "image = unit\\euro peon attack.bmp ; trailing comment\r\n" +
        "actionrow = 3\r\n" +
        "ActionRow = 7\r\n" +
        "hotspot = 0, 0\r\n" +
        "empty =\r\n" +
        "eq = a=b\r\n" +
        "[accept]\r\n" +
        "[broken\r\n" +
        "x = 1";

    [Fact]
    public void Keeps_order_duplicates_and_bare_values()
    {
        IniDocument ini = IniDocument.Parse(Sample);
        Assert.Equal(["accept", "E_Peon_Attack", "accept", "broken"], ini.Sections.Select(s => s.Name));
        IniSection attack = ini.FindSection("e_peon_attack")!;
        Assert.Equal(["3", "7"], attack.GetValues("ACTIONROW"));
        Assert.Equal("7", attack.GetValue("actionrow"));
        Assert.Equal("unit\\euro peon attack.bmp", attack.GetValue("image"));
        Assert.Equal("0, 0", attack.GetValue("hotspot"));
        Assert.Equal(string.Empty, attack.GetValue("empty"));
        Assert.Equal("a=b", attack.GetValue("eq"));
        Assert.Equal(["allunits"], ini.Sections[0].BareValues());
        Assert.Equal(["accept"], ini.DuplicateSectionNames());
        Assert.Equal(2, ini.FindSections("ACCEPT").Count);
    }

    [Fact]
    public void Reports_unterminated_headers_and_keeps_leading_entries()
    {
        IniDocument ini = IniDocument.Parse("k = v\n[broken\nx = 1\n");
        Assert.Null(ini.Sections[0].Name);
        Assert.Equal("v", ini.Sections[0].GetValue("k"));
        Assert.Equal("broken", ini.Sections[1].Name);
        Assert.Single(ini.Warnings);
        Assert.Equal(2, ini.Warnings[0].Line);
    }
}

public class TsvTableTests
{
    [Fact]
    public void Reads_ragged_rows_quotes_and_separators()
    {
        TsvTable table = TsvTable.Parse("NAME\tCLASS\tCOST\r\nArcher\tarcher\t\"90f, 40g\"\r\n\t\t\r\nKnight\tknight\r\n\r\n");
        Assert.Equal(["NAME", "CLASS", "COST"], table.Header!.Cells);
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal("90f, 40g", table.Rows[0][2]);
        Assert.True(table.Rows[1].IsBlank);
        Assert.Equal(string.Empty, table.Rows[2][2]);
        Assert.Equal(2, table.Rows[2].Cells.Count);
        Assert.True(table.Rows[3].IsBlank);
    }
}
