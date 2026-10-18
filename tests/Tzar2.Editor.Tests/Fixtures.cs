using System;
using Tzar2.Content.Compiled;
using Tzar2.Content.Editing;
using Tzar2.Content.Rules;
using Tzar2.Formats.Maps;
using Tzar2.Formats.Text;
using Tzar2.Tests.Shared;

namespace Tzar2.Editor.Tests;

/// <summary>Shared fixtures: the synthetic classic ruleset and invented legacy maps (REQ-LEGAL-03).</summary>
internal static class Fixtures
{
    public static Ruleset Classic { get; } = SyntheticLegacyContent.CompileRuleset();

    /// <summary>Painting rules for a set id (no art: no transition checks).</summary>
    public static TerrainRules RulesFor(string? set) => set is null ? TerrainRules.WithoutSet(Classic) : TerrainRules.ForSet(set, null, Classic);

    /// <summary>An invented legacy map with objects, user resources, events, actions and a rule.</summary>
    public static SyntheticMap LegacyMap(string name, string? next = null, CodePage codePage = CodePage.Windows1252, string title = "")
    {
        var m = new SyntheticMap { Name = name, Side = 12, Title = title.Length > 0 ? title : name + " map", CodePage = codePage };
        ushort[] tiles = new ushort[144];
        Array.Fill(tiles, SyntheticMap.Solid(1));
        tiles[0] = SyntheticMap.Solid(0);
        tiles[20] = SyntheticMap.Solid(1, tree: 9);
        m.Tiles = tiles;
        m.Kingdoms[0].StartX = 3;
        m.Kingdoms[0].StartY = 3;
        m.Objects.Add(new SyntheticObject { ClassName = "knight", X = 2, Y = 2, Owner = 1, Race = 1, Id = 1, Flags = 0x1000, Health = 250 });
        m.Objects.Add(new SyntheticObject { ClassName = "castle", X = 6, Y = 6, Owner = 2, Race = 1, Id = 2 });
        m.Objects.Add(new SyntheticObject { ClassName = "ground_patch", X = 9, Y = 9, Owner = 0, Race = 0, Image = "daisies" });
        m.Extras.Add((1, "Sir Pell", "USER\\Pell.bmp"));
        m.Files.Add(("USER/Pell.bmp", SyntheticFormats.Bmp(2, 2, 16, [0x1F, 0x7C, 0xE0, 0x03, 0x00, 0x00, 0xFF, 0x7F])));
        m.Files.Add(("USER/Theme.mid", SyntheticFormats.Mid()));
        m.Music.Add("USER\\Theme.mid");
        SyntheticPayload P() => new(codePage);
        m.Events.Add(("castle_down", (int)LegacyTriggerType.ObjectDestroyed, P().Named(true, 2).ToArray()));
        m.Actions.Add(("brief", (int)LegacyTriggerType.AddQuest, P().Str("Take the castle of " + name).Str(string.Empty).ToArray()));
        m.Actions.Add(("clock", (int)LegacyTriggerType.SetTimer, P().Str("60000").U8(1).ToArray()));
        m.Actions.Add((next is null ? "won" : "onward", (int)LegacyTriggerType.PlayerWin, P().U16(1).Str(next is null ? "|" : "MAPS\\" + next + ".wmp|").ToArray()));
        m.Rules.Add(("start", "1", ["brief", "clock"]));
        m.Rules.Add(("finish", "castle_down", [next is null ? "won" : "onward"]));
        return m;
    }
}
