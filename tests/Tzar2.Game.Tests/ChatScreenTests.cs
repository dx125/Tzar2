using System.Linq;
using Tzar2.Game.Session;
using Tzar2.Game.Ui;
using Xunit;

namespace Tzar2.Game.Tests;

/// <summary>The chat screen: its line, recipients and the list of kingdoms.</summary>
public class ChatScreenTests
{
    private static GameFrame Frame()
    {
        var frame = new GameFrame { Players = new FramePlayer[4] };
        frame.Stances = new byte[16];
        frame.Stances[(0 * 4) + 1] = (byte)Tzar2.Sim.Kingdoms.Stance.Ally;
        frame.Stances[(0 * 4) + 2] = (byte)Tzar2.Sim.Kingdoms.Stance.Neutral;
        frame.Stances[(0 * 4) + 3] = (byte)Tzar2.Sim.Kingdoms.Stance.Enemy;
        return frame;
    }

    [Fact]
    [Trait("Rule", "HUD-011")]
    public void A_line_goes_to_everyone_the_allies_the_enemies_or_the_kingdoms_picked_from_the_list()
    {
        var chat = new ChatScreen(Frame(), 0);
        Assert.Equal([1, 2, 3], chat.Kingdoms);
        Assert.False(chat.Large);
        Assert.Null(chat.Send()); // an empty line sends nothing
        chat.Write("  hello  ");
        Assert.Equal(("hello", ChatTarget.All, (byte)0), chat.Send());
        chat.Address(ChatTarget.Enemies);
        Assert.Equal(ChatTarget.Enemies, chat.Send()!.Value.To);

        chat.Address(ChatTarget.Custom);
        Assert.True(chat.Large); // the large window lists the kingdoms
        Assert.Equal([ChatShield.Ally, ChatShield.Neutral, ChatShield.Enemy], chat.Rows.Select(r => r.Shield));
        Assert.Null(chat.Send()); // to nobody
        chat.Toggle(3);
        chat.Toggle(1);
        chat.Toggle(0); // not on the list
        Assert.Equal(("hello", ChatTarget.Custom, (byte)0b1010), chat.Send());
        chat.Write(new string('x', 500));
        Assert.Equal(ChatScreen.MaxLength, chat.Text.Length);
    }
}
