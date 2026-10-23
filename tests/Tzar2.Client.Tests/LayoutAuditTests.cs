using System.Collections.Generic;
using System.Linq;
using Tzar2.Client.Logic;
using Xunit;

namespace Tzar2.Client.Tests;

// ADR 0200: the engine-free parts of the UI smoke test.
public class LayoutAuditTests
{
    private static AuditBox Box(int id, int parent, AuditKind kind, int x, int y, int w, int h, string label = "", int layer = 0)
        => new(id, parent, $"Screen/{kind}{id}", kind, x, y, w, h, label, 0, layer);

    [Fact]
    public void A_control_leaving_the_window_is_reported_but_a_backdrop_cut_by_it_is_not()
    {
        List<AuditBox> boxes =
        [
            Box(0, -1, AuditKind.Art, 0, 0, 1024, 768),
            Box(1, 0, AuditKind.Button, 1000, 10, 40, 20, "QUIT"),
            Box(2, 0, AuditKind.Button, 10, 700, 40, 30, "OK"),
        ];

        List<AuditIssue> wide = LayoutAudit.Check(1024, 768, boxes);
        Assert.Equal([AuditProblem.OffScreen], wide.Select(i => i.Problem));
        Assert.Contains("'QUIT'", wide[0].Detail, System.StringComparison.Ordinal);

        // A window that shows only the top 720 rows: the backdrop may be cut, the OK button may not.
        List<AuditIssue> cut = LayoutAudit.Check(1024, 768, (0, 0, 1024, 720), boxes);
        Assert.Equal(["'QUIT'", "'OK'"], cut.Select(i => i.Detail.Split(' ')[0]));
    }

    [Fact]
    public void Texts_buttons_and_fields_must_not_cover_one_another_on_one_layer()
    {
        List<AuditBox> boxes =
        [
            Box(0, -1, AuditKind.Frame, 0, 0, 800, 600),
            Box(1, 0, AuditKind.Button, 100, 100, 200, 25, "PLAY"),
            Box(2, 1, AuditKind.Text, 150, 105, 60, 13, "PLAY"),      // its own label: fine
            Box(3, 0, AuditKind.Text, 104, 104, 40, 13, "over"),      // drawn over the button, inside it: a label too
            Box(4, 0, AuditKind.Field, 290, 110, 100, 31),            // runs into the button
            Box(5, 0, AuditKind.Art, 100, 100, 300, 300),             // pictures never overlap
            Box(6, 0, AuditKind.Button, 100, 112, 200, 25, "BACK", layer: 1), // another layer (a dialog over it)
        ];

        List<AuditIssue> issues = LayoutAudit.Check(800, 600, boxes);
        AuditIssue overlap = Assert.Single(issues);
        Assert.Equal(AuditProblem.Overlap, overlap.Problem);
        Assert.Contains("'PLAY'", overlap.Detail, System.StringComparison.Ordinal);
        Assert.Contains("Field4", overlap.Detail, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Text_must_stay_inside_its_button_and_the_panel_it_starts_on()
    {
        List<AuditBox> boxes =
        [
            Box(0, -1, AuditKind.Frame, 0, 0, 1024, 768),
            Box(1, 0, AuditKind.Button, 100, 100, 100, 25, "x"),
            Box(2, 1, AuditKind.Text, 90, 105, 120, 13, "A LONG LABEL"),
            Box(3, 0, AuditKind.Art, 400, 300, 300, 140),
            Box(4, 0, AuditKind.Text, 500, 340, 260, 13, "a phrase that runs out of the box"),
            Box(5, 0, AuditKind.Text, 410, 310, 200, 13, "a phrase that fits"),
            new AuditBox(6, 0, "Screen/Label", AuditKind.Text, 10, 10, 50, 20, "Dropped players", 40),
        ];

        List<AuditIssue> issues = LayoutAudit.Check(1024, 768, boxes);
        Assert.Equal(3, issues.Count);
        Assert.All(issues, i => Assert.Equal(AuditProblem.TextOverflow, i.Problem));
        Assert.Contains(issues, i => i.Detail.Contains("leaves its button", System.StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Detail.Contains("runs out of the panel", System.StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Detail.Contains("40 px wider", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1024, 768, false, 0, 1, 1024, 768)]
    [InlineData(1280, 720, false, 0, 1, 1280, 720)]
    [InlineData(1366, 768, false, 0, 1, 1366, 768)]
    [InlineData(1920, 1080, false, 0, 1, 1920, 1080)]
    [InlineData(2560, 1440, false, 0, 2, 1280, 720)]
    [InlineData(3440, 1440, false, 0, 2, 1720, 720)]
    [InlineData(3840, 2160, false, 0, 3, 1280, 720)]
    [InlineData(3840, 2160, false, 2, 2, 1920, 1080)]
    [InlineData(3840, 2160, false, 4, 3, 1280, 720)] // 4x is more than the window allows: clamped to 3
    [InlineData(1920, 1200, true, 0, 1, 1024, 768)]
    [InlineData(2560, 1440, true, 0, 1, 1024, 768)]
    [InlineData(3840, 2160, true, 0, 2, 1024, 768)]
    public void Each_display_gives_the_logical_screen_of_ADR_0177(int w, int h, bool authentic, int setting, int scale, int lw, int lh)
    {
        DisplayCase d = DisplayCases.For(w, h, authentic, setting);
        Assert.Equal((scale, lw, lh), (d.Scale, d.Width, d.Height));
        Assert.Equal((0, 0, lw, lh), d.Visible);
    }

    [Fact]
    public void A_window_lower_than_the_logical_screen_shows_its_top_part()
    {
        // Godot's integer stretch does not go below 1 (measured): Authentic 1024 x 768 in a 720-high window loses 48 rows.
        DisplayCase d = DisplayCases.For(1280, 720, authentic: true, 0);
        Assert.Equal((1, 1024, 768), (d.Scale, d.Width, d.Height));
        Assert.Equal((0, 0, 1024, 720), d.Visible);
    }

    [Fact]
    public void The_ui_smoke_options_parse()
    {
        ViewerArgs args = ViewerArgs.Parse(["--ui-smoke", "--ui-shots", "shots", "--ui-only", "1920x1080"]);
        Assert.Equal((true, "shots", "1920x1080"), (args.UiSmoke, args.UiShots, args.UiOnly));
        Assert.Empty(args.Errors);
        Assert.False(args.IsViewerMode);
    }

    [Fact]
    public void The_review_covers_every_resolution_in_Wide_and_Authentic_without_duplicates()
    {
        List<DisplayCase> all = DisplayCases.All();
        Assert.Equal(DisplayCases.Resolutions.Length, all.Count(d => d.Authentic));
        Assert.Equal(DisplayCases.Resolutions.Length, all.Count(d => !d.Authentic && d.Setting == 0));
        Assert.Equal(all.Count, all.Select(d => (d.WindowWidth, d.WindowHeight, d.Authentic, d.Width, d.Height)).Distinct().Count());
        Assert.Contains(all, d => d.Name == "3840x2160-wide-1x" && d.Width == 3840);
        Assert.Contains(all, d => d.Name == "3840x2160-wide-2x" && d.Width == 1920);
        Assert.Equal(22, all.Count);
    }
}
