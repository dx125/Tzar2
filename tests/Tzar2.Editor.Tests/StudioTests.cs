using System.Linq;
using Tzar2.Content.Editing;
using Tzar2.Import.Editing;
using Xunit;

namespace Tzar2.Editor.Tests;

/// <summary>The §18.3 SHOULDs: the read-only rules browser and the "authentic" image import.</summary>
public class StudioTests
{
    [Fact]
    public void The_rules_browser_lists_types_technologies_and_upgrades_and_filters()
    {
        var all = RulesBrowser.Rows(Fixtures.Classic);
        Assert.Contains(all, r => r.Key == "euro.knight" && r.Kind == "unit" && r.Summary.Contains("attack", System.StringComparison.Ordinal));
        Assert.Contains(all, r => r.Key == "euro.castle" && r.Summary.Contains("footprint 5×5", System.StringComparison.Ordinal));
        Assert.Contains(all, r => r.Kind == "technology");
        Assert.Contains(all, r => r.Kind == "upgrade");
        Assert.All(RulesBrowser.Rows(Fixtures.Classic, "wall"), r => Assert.Contains("wall", r.Key, System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Authentic_import_quantises_to_rgb555_flags_foreign_colours_and_casts_a_shadow()
    {
        // 8×8: a 2×2 opaque square of an exact palette colour and one of a foreign colour, the rest transparent.
        byte[] rgba = new byte[8 * 8 * 4];
        void Put(int x, int y, byte r, byte g, byte b)
        {
            int i = ((y * 8) + x) * 4;
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, 255);
        }

        Put(0, 0, 0x3F, 0x80, 0x10);
        Put(1, 0, 0xFF, 0x00, 0xFF);
        AuthenticResult r = AuthenticImage.Convert(rgba, 8, 8, [(0x39, 0x84, 0x10)]);
        Assert.Equal((0x39, 0x84, 0x10), (r.Rgba[0], r.Rgba[1], r.Rgba[2]));
        Assert.Equal(1, r.OutsidePalette);
        Assert.Equal(128, r.Shadow![(((AuthenticImage.ShadowOffset * 8) + AuthenticImage.ShadowOffset) * 4) + 3]);
        Assert.Equal(0, r.Shadow[3]);
        Assert.All(Enumerable.Range(0, 64), p => Assert.True(r.Rgba[(p * 4) + 3] is 0 or 255));
    }
}
