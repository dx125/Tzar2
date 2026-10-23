using System.Collections.Generic;
using System.Linq;
using Tzar2.Game.Ui;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>The online help: chapters, civilizations, topics and their links (invented texts in the original's form).</summary>
public class OnlineHelpTests
{
    private static (string, IReadOnlyDictionary<string, string>) S(string name, params (string Key, string Value)[] keys)
        => (name, keys.ToDictionary(k => k.Key, k => k.Value));

    private static OnlineHelp Help() => new(
        [
            S("U_E_PEASANT", ("brief", "Peasant"), ("rollover", "About the peasant"), ("detail", "Cost-50 food ~ Builds things."), ("also", "WI_UNIT,HT_BUILD"), ("related", "B_E_CASTLE"), ("icon", "1,26")),
            S("U_R_PEASANT", ("brief", "Arab peasant"), ("related", "B_R_CASTLE"), ("icon", "1,27")),
            S("B_E_CASTLE", ("brief", "Castle"), ("row1", "T_E_FEUDALISM"), ("row2", "U_E_PEASANT,U_E_SPY"), ("icon", "2,1")),
            S("B_C_GATEWAY", ("brief", "Gateway"), ("icon", "2,9")),
            S("T_E_FEUDALISM", ("brief", "Feudalism"), ("related", "B_E_CASTLE"), ("requires", "T_E_NOTHERE"), ("allows", "U_E_PEASANT"), ("icon", "3,1")),
            S("I_WEAPON", ("brief", "Weapon Item"), ("also", "WI_ITEMS;HT_USE_ITEMS;??????????"), ("icon", "4,1")),
            S("WI_UNIT", ("brief", "What is a unit"), ("also", "U_E_PEASANT"), ("icon", "7")),
            S("HT_BUILD", ("brief", "How to build"), ("icon", "6")),
            S("help_notes", ("text", "not a topic")),
        ],
        ["@1F1@2 - Menu screen", "@1F4@2 - Scores"]);

    [Fact]
    [Trait("Rule", "HUD-020")]
    public void Topics_fall_into_chapters_and_civilizations_by_their_keys()
    {
        OnlineHelp help = Help();
        Assert.Equal(8, help.Topics.Length); // the section that is no topic is left out
        HelpTopic peasant = help.Find("u_e_peasant")!;
        Assert.Equal((HelpCategory.Units, "euro", "Peasant", 1, 25), (peasant.Category, peasant.Race, peasant.Brief, peasant.IconFile, peasant.IconIndex));
        Assert.Equal(["Cost-50 food", "Builds things."], peasant.Paragraphs.ToArray());
        Assert.Equal("arab", help.Find("U_R_PEASANT")!.Race);
        Assert.Equal(string.Empty, help.Find("B_C_GATEWAY")!.Race);
        Assert.Equal((7, 0), (help.Find("WI_UNIT")!.IconFile, help.Find("WI_UNIT")!.IconIndex));

        Assert.Equal(["U_E_PEASANT"], help.Entries.Select(t => t.Key)); // units, European
        help.NextRace(+1);
        Assert.Equal(["U_R_PEASANT"], help.Entries.Select(t => t.Key));
        help.Open(HelpCategory.Buildings);
        help.NextRace(-2); // arab -> euro -> common
        Assert.Equal(["B_C_GATEWAY"], help.Entries.Select(t => t.Key));
        help.Open(HelpCategory.Items);
        Assert.False(help.HasRaces);
        Assert.Equal(["I_WEAPON"], help.Entries.Select(t => t.Key));
        help.Open(HelpCategory.HotKeys);
        Assert.Empty(help.Entries);
        Assert.Equal(2, help.HotKeys.Length);
    }

    [Fact]
    [Trait("Rule", "HUD-020")]
    public void A_topic_links_to_what_makes_needs_and_allows_it_and_back_returns()
    {
        OnlineHelp help = Help();
        HelpTopic castle = help.Find("B_E_CASTLE")!;
        Assert.Equal(HelpLinkKind.Makes, castle.Links[0].Kind);
        Assert.Equal(["T_E_FEUDALISM", "U_E_PEASANT"], castle.Links[0].Topics.ToArray()); // U_E_SPY is not in the file
        HelpTopic feudalism = help.Find("T_E_FEUDALISM")!;
        Assert.Equal([HelpLinkKind.ResearchedIn, HelpLinkKind.Allows], feudalism.Links.Select(l => l.Kind));
        Assert.Equal(["HT_BUILD", "WI_UNIT"], help.Find("U_E_PEASANT")!.Links.Single(l => l.Kind == HelpLinkKind.SeeAlso).Topics.Order());
        Assert.Empty(help.Find("I_WEAPON")!.Links); // links to topics the file lacks are left out

        help.Open(HelpCategory.WhatIs);
        help.Show("WI_UNIT");
        help.Show("U_E_PEASANT");
        help.Show("B_E_CASTLE");
        help.Show("NOT_A_TOPIC");
        Assert.Equal("B_E_CASTLE", help.Topic!.Key);
        help.Back();
        Assert.Equal(("U_E_PEASANT", HelpCategory.Units), (help.Topic!.Key, help.Category));
        help.Back();
        help.Back();
        Assert.Null(help.Topic);
        Assert.Equal(HelpCategory.WhatIs, help.Category);
        help.Back();
        Assert.False(help.CanGoBack);
    }
}
