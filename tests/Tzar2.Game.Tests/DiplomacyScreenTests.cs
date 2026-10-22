using System;
using Tzar2.Content.Compiled;
using Tzar2.Content.Rules;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Kingdoms;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>The diplomacy and tribute screens' models (HUD-012, HUD-013; ADR 0182).</summary>
public sealed class DiplomacyScreenTests : IDisposable
{
    private static readonly Lazy<Ruleset> Rules = new(() => SyntheticLegacyContent.CompileRuleset());
    private readonly GameSession _session = new(SessionTests.Setup(players: 3) with { ComputerAi = false }, Rules.Value, fog: false);

    public void Dispose() => _session.Dispose();

    [Fact]
    [Trait("Rule", "HUD-012")]
    public void The_diplomacy_screen_shows_both_sides_and_accept_sends_only_the_changes()
    {
        GameFrame frame = Frame();
        var screen = new DiplomacyScreen(frame, 0);
        Assert.Equal([1, 2], [screen.Rows[0].Slot, screen.Rows[1].Slot]);
        Assert.False(string.IsNullOrEmpty(screen.Rows[0].Civilization));
        Assert.Null(screen.Accept()); // nothing changed

        // Kingdom 1 proposes an alliance with vision; the player answers.
        Submit(1, CommandKind.SetDiplomacy, x: 0, arg0: (int)Stance.Ally, arg1: (int)Sharing.Vision);
        screen = new DiplomacyScreen(Frame(), 0);
        DiplomacyRow one = screen.Rows[0];
        Assert.Equal((Stance.Ally, Sharing.Vision), (one.Theirs, one.TheirOffers));
        Assert.Equal(2, DiplomacyScreen.StanceFrame(one, Stance.Ally)); // only the upper right: the other kingdom's choice
        Assert.Equal(3, DiplomacyScreen.SharingFrame(one, Sharing.Vision)); // off, with the other's green glow
        screen.Choose(one, Stance.Ally);
        screen.Toggle(one, Sharing.Vision);
        screen.AlliedVictory = true;
        Assert.Equal(3, DiplomacyScreen.StanceFrame(one, Stance.Ally));
        Assert.Equal(2, DiplomacyScreen.SharingFrame(one, Sharing.Vision));
        CommandList accept = screen.Accept()!;
        Assert.Equal(2, accept.Count); // shared victory is offered to every kingdom
        Assert.Equal((CommandKind.SetDiplomacy, 1, (int)Stance.Ally, (int)(Sharing.Vision | Sharing.Victory)), (accept[0].Kind, accept[0].X, accept[0].Arg0, accept[0].Arg1));
        _session.Submit(accept);
        _session.Step(2);
        GameFrame after = Frame();
        Assert.Equal(Stance.Ally, after.StanceOf(0, 1));
        Assert.Equal(Sharing.Vision | Sharing.Victory, after.OffersOf(0, 1));
        Assert.True(new DiplomacyScreen(after, 0).AlliedVictory);
    }

    [Fact]
    [Trait("Rule", "HUD-013")]
    public void The_tribute_screen_steps_amounts_within_the_stockpile_and_sends_them_all_at_once()
    {
        int gold = Rules.Value.ResourceId("gold"), food = Rules.Value.ResourceId("food");
        GameFrame frame = Frame();
        int stock = frame.Players[0].Resources[gold];
        var screen = new TributeScreen(frame, 0, [gold, food], step: 100);
        Assert.Equal([1, 2], screen.Recipients);
        for (int i = 0; i < 1000 && screen.Amount(1, 0) < stock; i++)
        {
            screen.Click(1, 0, +1);
        }

        Assert.Equal(stock, screen.Amount(1, 0));
        screen.Click(2, 0, +1);
        Assert.Equal(0, screen.Amount(2, 0)); // the stockpile is spent on kingdom 1
        screen.Click(1, 0, -1);
        screen.Click(2, 0, +1);
        screen.Click(2, 1, +1);
        Assert.Equal(Math.Min(100, stock), screen.Amount(2, 0));

        int foodBefore = frame.Players[2].Resources[food];
        _session.Submit(screen.Send()!);
        _session.Step(2);
        GameFrame after = Frame();
        Assert.Equal(0, after.Players[0].Resources[gold]);
        Assert.Equal(foodBefore + 100, after.Players[2].Resources[food]);
    }

    private GameFrame Frame()
    {
        _session.Step(1);
        return _session.AcquireFrame(out _)!;
    }

    private void Submit(int player, CommandKind kind, int x = 0, int arg0 = 0, int arg1 = 0)
    {
        var list = new CommandList();
        list.Add(kind, x: x, arg0: arg0, arg1: arg1);
        _session.SubmitAs((byte)player, list);
        _session.Step(2);
    }
}
