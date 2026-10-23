using System.Collections.Generic;
using Tzar2.Game.Ui;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>Quitting, resigning and disconnecting ask first, in the original's words (SHELL-027).</summary>
public class ConfirmationsTests
{
    private static readonly Dictionary<(string, string), string> Strings = new()
    {
        [("q", "quit_title")] = "Exit T",
        [("q", "quit")] = "Quit?",
        [("q", "resign")] = "Resign?",
        [("q", "disconnect")] = "Disconnect?",
        [("menu", "quit")] = "QUIT",
        [("menu", "disconnect")] = "DISCONNECT",
        [("general", "yes")] = "Yes",
        [("general", "no")] = "No",
    };

    private static string Text(string section, string key, string fallback) => Strings.TryGetValue((section, key), out string? s) ? s : "?" + fallback;

    [Fact]
    [Trait("Rule", "SHELL-027")]
    public void Each_way_out_asks_its_own_question_from_the_strings()
    {
        Assert.Equal(new Confirmation(ConfirmKind.Quit, "Exit T", "Quit?", "Yes", "No"), Confirmations.For(ConfirmKind.Quit, Text));
        Assert.Equal(new Confirmation(ConfirmKind.Resign, "QUIT", "Resign?", "Yes", "No"), Confirmations.For(ConfirmKind.Resign, Text));
        Assert.Equal(new Confirmation(ConfirmKind.Disconnect, "DISCONNECT", "Disconnect?", "Yes", "No"), Confirmations.For(ConfirmKind.Disconnect, Text));
        Assert.Equal("Are you sure you want to quit the game?", Confirmations.For(ConfirmKind.Quit, null).Question); // without the package
    }

    [Theory]
    [InlineData(false, ConfirmKind.Resign)]
    [InlineData(true, ConfirmKind.Disconnect)]
    [Trait("Rule", "SHELL-027")]
    public void The_game_menus_quit_resigns_alone_and_disconnects_in_multiplayer(bool multiplayer, ConfirmKind kind)
        => Assert.Equal(kind, Confirmations.GameMenuQuit(multiplayer));
}
