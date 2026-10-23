using System.IO;
using System.Linq;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>The AI resign flag of the random map and multiplayer screens (patch 1.09; ADR 0262).</summary>
public class AiResignFlagTests
{
    [Fact]
    [Trait("Rule", "SHELL-013")]
    public void The_flag_is_a_match_setting_that_keeps_older_bytes_while_it_is_up()
    {
        var flag = new MatchSettings { Seed = 5 };
        Assert.True(flag.AiResign);
        Assert.Equal(1, flag.ToBytes()[0]); // as before the flag: older descriptions and replay headers keep their bytes

        MatchSettings sword = flag with { AiResign = false };
        byte[] bytes = sword.ToBytes();
        Assert.Equal(MatchSettings.Version, bytes[0]);
        Assert.Equal(sword, MatchSettings.Parse(bytes));

        MatchSettings custom = sword with { Map = MapSourceKind.CustomMap, CustomMapId = "m.t2map", CustomMapHash = 9, UseMapSettings = false };
        Assert.Equal(custom, MatchSettings.Parse(custom.ToBytes()));
        MatchSettings customFlag = custom with { AiResign = true };
        Assert.Equal(2, customFlag.ToBytes()[0]);
        Assert.Equal(customFlag, MatchSettings.Parse(customFlag.ToBytes()));
    }

    [Fact]
    [Trait("Rule", "SHELL-013")]
    public void A_version_or_extras_this_build_does_not_know_is_refused_plainly()
    {
        byte[] bytes = new MatchSettings { AiResign = false }.ToBytes();
        byte[] newer = [.. bytes];
        newer[0] = MatchSettings.Version + 1;
        Assert.Contains("newer Tzar2", Assert.Throws<InvalidDataException>(() => MatchSettings.Parse(newer)).Message, System.StringComparison.Ordinal);

        byte[] extras = [.. bytes];
        extras[^1] = 0x80;
        Assert.Throws<InvalidDataException>(() => MatchSettings.Parse(extras));

        byte[] claimsCustom = [.. bytes];
        claimsCustom[^1] = 3; // a custom map the map source does not name
        Assert.Throws<InvalidDataException>(() => MatchSettings.Parse(claimsCustom));
    }

    [Fact]
    [Trait("Rule", "SHELL-013")]
    public void The_flag_reaches_the_match_from_a_description()
    {
        var players = new[] { new MatchPlayer(0, "A", "euro", ControllerKind.Human, Difficulty.Normal, 0), new MatchPlayer(1, "", "arab", ControllerKind.Computer, Difficulty.Normal, 0) };
        var d = new MatchDescriptor(new MatchSettings { AiResign = false }, [.. players]);
        MatchSetup setup = d.ToSetup(SkirmishAi.EngineDefault, multiplayer: false, localSlot: null);
        Assert.False(setup.AiResign);
        Assert.False(MatchDescriptor.Parse(d.ToBytes()).Settings.AiResign);
        Assert.False(MatchDescriptor.Of(setup with { Descriptor = null })!.Settings.AiResign);
    }

    [Fact]
    [Trait("Rule", "SHELL-013")]
    public void The_random_map_screen_offers_the_flag_and_keeps_it()
    {
        var m = new RandomMapScreenModel(["euro", "arab", "asia"], [new AiScriptOption("Random", string.Empty)]);
        Assert.True(m.IsOn(RandomMapOption.AiResign));
        m.Change(RandomMapOption.AiResign, +1);
        Assert.False(m.Describe(1).Settings.AiResign);
        Assert.False(new RandomMapScreenModel(["euro"], [], m.ToBytes()).IsOn(RandomMapOption.AiResign));
    }
}
