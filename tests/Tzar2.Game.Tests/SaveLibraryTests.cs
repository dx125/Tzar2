using System;
using System.IO;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Game.Campaigns;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>Save games of every single player game, the library and the save and load screens (SHELL-014, SHELL-021, SHELL-022).</summary>
public sealed class SaveLibraryTests : IDisposable
{
    private static readonly Lazy<Ruleset> Rules = new(() => SyntheticLegacyContent.CompileRuleset());
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tzar2-saves-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    [Trait("Rule", "SHELL-014")]
    [Trait("Rule", "SHELL-022")]
    public void A_skirmish_saved_under_a_name_is_listed_and_continues_where_it_was()
    {
        using var session = new GameSession(SessionTests.Setup(2, 64), Rules.Value, fog: false);
        session.Step(40);
        var library = new SaveLibrary(_folder);
        var save = new SaveGameScreen(library);
        Assert.Equal("Game 1", save.Name);
        Assert.False(save.Overwrites);
        Assert.True(save.Save(SaveGame.CaptureMatch(session, "the label")));
        ulong hash = session.Simulation.ComputeFullHash();
        int tick = session.Simulation.World.Tick.Value;
        Assert.Equal("Game 2", new SaveGameScreen(library).Name);

        var load = new LoadGameScreen(library);
        Assert.Single(load.Entries); // a single player game
        load.SwitchType();
        Assert.Empty(load.Entries); // no campaign saves
        load.SwitchType();
        load.Select(0);
        Assert.Equal(("Game 1", "the label", SaveKind.Skirmish), (load.Picked!.Name, load.Picked.Label, load.Picked.Kind));
        SaveGame loaded = load.Load()!;

        using GameSession restored = loaded.ResumeSkirmish(Rules.Value, SkirmishAi.EngineDefault);
        Assert.Equal((tick, hash), (restored.Simulation.World.Tick.Value, restored.Simulation.ComputeFullHash()));
        session.Step(30);
        restored.Step(30);
        Assert.Equal(session.Simulation.ComputeFullHash(), restored.Simulation.ComputeFullHash()); // the same match goes on

        load.Delete();
        Assert.Empty(load.Entries);
        Assert.Empty(library.List());
    }

    [Fact]
    [Trait("Rule", "SHELL-021")]
    public void A_custom_map_save_keeps_its_setup_and_names_are_plain_file_names()
    {
        var custom = new CustomMapSettings { Difficulty = Difficulty.Hard, Civilization = "arab", StartingResources = 2, PopulationLimit = 150, NoFog = true, Explored = false };
        var save = new SaveGame(string.Empty, "MP1", "label", 120, null, [1, 2, 3]) { Kind = SaveKind.CustomMap, Custom = custom };
        SaveGame back = SaveGame.FromBytes(save.ToBytes());
        Assert.Equal((SaveKind.CustomMap, "MP1", 120), (back.Kind, back.MapId, back.Tick));
        Assert.Equal(custom, back.Custom);
        Assert.Throws<InvalidDataException>(() => back.ResumeSkirmish(Rules.Value, SkirmishAi.EngineDefault));

        Assert.Equal("Game 1", SaveLibrary.Clean("  Game 1 "));
        Assert.Null(SaveLibrary.Clean("../escape"));
        Assert.Null(SaveLibrary.Clean(new string('a', SaveLibrary.MaxName + 1)));
        Assert.Null(SaveLibrary.Clean("   "));

        var library = new SaveLibrary(_folder);
        var screen = new SaveGameScreen(library) { Name = "Mine" };
        Assert.True(screen.Save(save));
        screen.Select(0);
        Assert.Equal("Mine", screen.Name);
        Assert.True(screen.Overwrites);
        screen.Name = "bad/name";
        Assert.False(screen.CanSave);
        Assert.False(screen.Save(save));
        screen.Select(0);
        screen.Delete();
        Assert.Empty(screen.Entries);
    }
}
