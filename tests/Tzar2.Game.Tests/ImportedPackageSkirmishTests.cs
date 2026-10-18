using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Vfs;
using Tzar2.Game.Maps;
using Tzar2.Game.Session;
using Tzar2.Sim.Events;
using Xunit;
using Xunit.Abstractions;

namespace Tzar2.Game.Tests;

/// <summary>Runs only with an imported package (<c>TZAR2_PACKAGE</c> = a <c>t2 import</c> output); CI has none.</summary>
public sealed class PackageFactAttribute : FactAttribute
{
    public PackageFactAttribute()
    {
        if (ImportedPackageSkirmishTests.PackagePath is null)
        {
            Skip = "Set TZAR2_PACKAGE to an imported base package to run skirmishes on the classic ruleset.";
        }
    }
}

public sealed class ImportedPackageSkirmishTests(ITestOutputHelper output)
{
    public static string? PackagePath { get; } =
        Environment.GetEnvironmentVariable("TZAR2_PACKAGE") is { Length: > 0 } p && File.Exists(p) ? p : null;

    [PackageFact]
    public void Computer_kingdoms_of_every_civilization_play_a_skirmish_on_the_classic_ruleset()
    {
        using PackageSource package = PackageSource.Open(PackagePath!);
        Ruleset ruleset = RulesetLoader.Compile(package).Ruleset;
        var setup = new MatchSetup
        {
            Map = new SkirmishTestMap(96),
            Players =
            [
                new PlayerSetup(0, "euro", ControllerKind.Computer, Difficulty.Hard),
                new PlayerSetup(1, "asia", ControllerKind.Computer, Difficulty.Hard),
                new PlayerSetup(2, "arab", ControllerKind.Computer, Difficulty.Hard),
            ],
        };
        using var session = new GameSession(setup, ruleset);
        var events = new List<SimEvent>();
        int delivered = 0, trained = 0, built = 0, started = 0, rejected = 0;
        for (int i = 0; i < 60; i++)
        {
            session.Step(100);
            session.DrainEvents(events);
            foreach (SimEvent e in events)
            {
                switch (e.Kind)
                {
                    case SimEventKind.ResourceDelivered: delivered += e.Arg; break;
                    case SimEventKind.UnitTrained: trained++; break;
                    case SimEventKind.ConstructionCompleted: built++; break;
                    case SimEventKind.ConstructionStarted: started++; break;
                    case SimEventKind.CommandRejected: rejected++; break;
                }
            }

            events.Clear();
        }

        GameFrame frame = session.AcquireFrame(out _)!;
        foreach (IGrouping<(byte, ushort), FrameEntity> g in frame.Entities.ToArray().Where(e => e.Owner < 8).GroupBy(e => (e.Owner, e.TypeId)))
        {
            EntityTypeDefinition t = ruleset.EntityTypes[g.Key.Item2];
            output.WriteLine($"  p{g.Key.Item1} {t.Key} class={t.Traits.LegacyClass} x{g.Count()} acts={string.Join(",", g.Select(e => e.Activity))}");
        }

        output.WriteLine($"delivered {delivered}, trained {trained}, started {started}, built {built}, rejected {rejected}, entities {frame.EntityCount}");
        for (int p = 0; p < 3; p++)
        {
            output.WriteLine($"player {p}: pop {frame.Players[p].Population}/{frame.Players[p].PopulationCap}, res {string.Join(",", Enumerable.Range(0, 5).Select(r => frame.Players[p].Resources[r]))}");
        }

        Assert.True(delivered > 0);
        Assert.True(trained > 0);
        Assert.True(started > 0);
    }
}
