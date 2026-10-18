using System;
using System.Linq;
using Tzar2.Formats.Maps;
using Tzar2.Formats.Text;
using Tzar2.Tests.Shared;
using Xunit;

namespace Tzar2.Formats.Tests;

/// <summary>The legacy map reader (docs/formats/wmp.md) on synthetic maps built from the spec.</summary>
public class MapFormatTests
{
    private static SyntheticMap Sample()
    {
        var m = new SyntheticMap { Name = "Sample", Side = 8, Title = "Sample title", Author = "Me", TerrainSet = "snow", Players = 3 };
        m.Kingdoms[0].StartX = 2;
        m.Kingdoms[0].StartY = 3;
        m.Kingdoms[0].AiScript = "war.ais";
        m.InitialResearches[0] = ["te_a", "te_b"];
        m.ForbiddenUnits[1] = ["knight"];
        m.Objects.Add(new SyntheticObject { ClassName = "peon", X = 1, Y = 2, Owner = 1, Race = 1, Id = 10 });
        m.Objects.Add(new SyntheticObject { ClassName = "res_gold", X = 4, Y = 4, Owner = 0, Race = 0, Id = 11, Amount = 1234 });
        m.Objects.Add(new SyntheticObject { ClassName = "ground_patch", X = 5, Y = 6, Owner = 0, Race = 0, Image = "go_flowers_1" });
        m.Objects.Add(new SyntheticObject { ClassName = "item_scroll", X = 3, Y = 3, Owner = 0, Race = 0, Id = 12, ScrollText = "Read me" });
        m.Extras.Add((10, "Hero", "USER\\hero.bmp"));
        m.Events.Add(("near", (int)LegacyTriggerType.ObjectInArea, new SyntheticPayload().Named(true, 10).Rect(0, 0, 3, 3).ToArray()));
        m.Events.Add(("army", (int)LegacyTriggerType.ObjectExist, new SyntheticPayload().Typed(0xFFFD, 1, false, "5").ToArray()));
        m.Actions.Add(("quest", (int)LegacyTriggerType.AddQuest, new SyntheticPayload().Str("Find gold").Str(string.Empty).ToArray()));
        m.Actions.Add(("go", (int)LegacyTriggerType.ObjectOrder, new SyntheticPayload().Named(false, 10).AtObject(11, "2").U8(1).ToArray()));
        m.Actions.Add(("keep", (int)LegacyTriggerType.KeepRule, []));
        m.Rules.Add(("start", "near && army", ["quest", "go", "keep"]));
        m.Conversations.Add(("talk", [new SyntheticPhrase { Id = 1, Source = 10, Initial = true, Name = "hi", Text = "Hello", FollowUps = [2] }, new SyntheticPhrase { Id = 2, Source = 10, Name = "bye", Result = "$x = 1", Text = "Bye" }]));
        m.Music.Add("USER\\1.mid");
        m.Files.Add(("USER/1.mid", SyntheticFormats.Mid()));
        return m;
    }

    [Fact]
    public void Reads_every_member()
    {
        LegacyMap map = LegacyMapReader.ReadMap(Sample().Build());
        Assert.Empty(map.Warnings);
        Assert.Equal("Sample", map.MapName);
        Assert.Equal(8, map.Side);
        Assert.Equal("Sample title", map.Header.Title);
        Assert.Equal("snow", map.Header.TerrainSet);
        Assert.Equal(3, map.Header.Players);
        Assert.Equal(150, map.Header.PopulationLimit);
        Assert.Equal("A synthetic map.", map.Description);
        Assert.Equal(8, map.Kingdoms.Length);
        Assert.Equal((2, 3), (map.Kingdoms[0].StartX, map.Kingdoms[0].StartY));
        Assert.Equal(-1, map.Kingdoms[1].StartX);
        Assert.Equal("war.ais", map.Kingdoms[0].AiScript);
        Assert.Equal([100, 200, 300, 400], map.Kingdoms[0].Resources.ToArray());
        Assert.Equal(["te_a", "te_b"], map.InitialResearches[0].ToArray());
        Assert.Equal(["knight"], map.ForbiddenUnits[1].ToArray());
        Assert.Equal(0x48u, map.Diplomacy[0]);
        Assert.Equal(64, map.Tiles.Length);

        Assert.Equal(4, map.Objects.Length);
        LegacyObject peon = map.Objects[0];
        Assert.Equal(("peon", 1, 2, (byte)1, (byte)1, (ushort)10), (peon.ClassName, peon.X, peon.Y, peon.Owner, peon.Race, peon.Id));
        Assert.Equal(1234, map.Objects[1].Amount);
        Assert.Equal("go_flowers_1", map.Objects[2].ImageName);
        Assert.Equal((165, 199), map.Objects[2].PixelPosition);
        Assert.Equal("Read me", map.Objects[3].Item!.Text);
        Assert.True(map.Objects[3].Item!.AutoClose);
        Assert.Equal("Hero", map.ObjectExtras.Single().Title);

        Assert.Equal(2, map.Events.Length);
        LegacyObjectSpec named = map.Events[0].Field<LegacyObjectSpec>(0);
        Assert.False(named.Typed);
        Assert.True(named.All);
        Assert.Equal([(ushort)10], named.ObjectIds.ToArray());
        Assert.Equal(new LegacyArea(false, 0, 0, 3, 3, 0, "0"), map.Events[0].Field<LegacyArea>(1));
        LegacyObjectSpec typed = map.Events[1].Field<LegacyObjectSpec>(0);
        Assert.True(typed.Typed);
        Assert.Equal((ushort)0xFFFD, typed.ClassFilter);
        Assert.Equal("5", typed.Count);
        Assert.Equal("Find gold", map.Actions[0].Field<string>(0));
        Assert.Equal(1, map.Actions[1].Field<int>(2));
        Assert.True(map.Actions[2].IsKnown);
        Assert.Equal(new[] { "quest", "go", "keep" }, map.Rules.Single().ActionNames.ToArray());
        Assert.Equal("near && army", map.Rules.Single().Condition);
        LegacyConversation talk = map.Conversations.Single();
        Assert.Equal(2, talk.Phrases.Length);
        Assert.Equal([(ushort)2], talk.Phrases[0].FollowUps.ToArray());
        Assert.Equal("$x = 1", talk.Phrases[1].Result);
        Assert.Equal(["USER\\1.mid"], map.Music.ToArray());
        Assert.Contains(map.Files, f => f.Path == "USER/1.mid");
    }

    [Fact]
    public void Class_encoding_round_trips_for_every_class()
    {
        for (int i = 1; i < LegacyClasses.Names.Count; i++)
        {
            Assert.Equal(i, LegacyClasses.Decode(LegacyClasses.Encode(i)));
        }

        Assert.Equal(-1, LegacyClasses.Decode(0x00090000));
        Assert.Equal(LegacyClasses.IndexOf("cow"), LegacyClasses.Decode(0x00060024));
    }

    [Fact]
    public void Unknown_trigger_types_and_bad_payloads_are_kept_raw_with_a_warning()
    {
        SyntheticMap m = Sample();
        m.Actions.Add(("odd", 77, [1, 2, 3]));
        m.Actions.Add(("short", (int)LegacyTriggerType.ChangeDiplomacy, [1]));
        LegacyMap map = LegacyMapReader.ReadMap(m.Build());
        Assert.Equal(2, map.Warnings.Length);
        Assert.False(map.Actions[3].IsKnown);
        Assert.Equal([(byte)1, 2, 3], map.Actions[3].Payload.ToArray());
        Assert.True(map.Actions[4].Fields.IsEmpty);
    }

    [Fact]
    public void Text_is_decoded_with_the_given_code_page_and_samples_are_raw()
    {
        SyntheticMap m = Sample();
        m.CodePage = CodePage.Windows1251;
        m.Title = "Карта";
        byte[] data = m.Build();
        Assert.Equal("Карта", LegacyMapReader.ReadMap(data, CodePage.Windows1251).Header.Title);
        Assert.Contains(LegacyMapReader.TextSamples(data), s => s.Length == 5 && s.All(b => b >= 0x80));
    }

    [Fact]
    public void Campaigns_list_maps_with_entry_flags()
    {
        byte[] map = Sample().Build();
        byte[] wcm = SyntheticMap.Campaign("My campaign", "Line one\r\nLine two", [("Sample", true, map), ("Other", false, map)]);
        LegacyCampaign c = LegacyMapReader.ReadCampaign(wcm);
        Assert.Equal("My campaign", c.Title);
        Assert.Equal("Line one\r\nLine two", c.Description);
        Assert.Equal([("Sample", true), ("Other", false)], c.Maps.ToArray());
        Assert.Equal(2, c.MapFiles.Length);
        Assert.Equal("Sample", LegacyMapReader.ReadMap(c.MapFiles[0].Data).MapName);
    }

    [Fact]
    public void Malformed_maps_throw_only_legacy_format_exceptions()
    {
        byte[] good = Sample().Build();
        Assert.Throws<LegacyFormatException>(() => LegacyMapReader.ReadMap(SyntheticFormats.Hpfs([("MAPS/x.txt", [1])])));
        Assert.Throws<LegacyFormatException>(() => LegacyMapReader.ReadCampaign(good));
        var rng = new Random(1234);
        for (int trial = 0; trial < 300; trial++)
        {
            byte[] bad = (byte[])good.Clone();
            for (int k = 0; k < 8; k++)
            {
                bad[rng.Next(bad.Length)] = (byte)rng.Next(256);
            }

            try
            {
                LegacyMapReader.ReadMap(bad);
            }
            catch (LegacyFormatException)
            {
            }
        }
    }
}
