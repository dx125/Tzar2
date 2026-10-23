using System.Collections.Generic;
using Tzar2.Client.Logic;
using Xunit;

namespace Tzar2.Client.Tests;

// UI review findings UI-A3, UI-A10, UI-A11, UI-A12 (ADR 0200): the dialogs place their contents in the art's boxes.
public class DialogBoxesTests
{
    private static void Inside((int Width, int Height) art, params ArtBox[] boxes)
    {
        foreach (ArtBox b in boxes)
        {
            Assert.True(b.X >= 0 && b.Y >= 0 && b.Right <= art.Width && b.Bottom <= art.Height, $"{b} leaves the {art.Width}x{art.Height} art");
        }

        for (int i = 0; i < boxes.Length; i++)
        {
            for (int j = i + 1; j < boxes.Length; j++)
            {
                ArtBox a = boxes[i], c = boxes[j];
                Assert.False(a.X < c.Right && c.X < a.Right && a.Y < c.Bottom && c.Y < a.Bottom, $"{a} and {c} overlap");
            }
        }
    }

    [Fact]
    public void Each_dialogs_boxes_lie_inside_its_art_and_apart()
    {
        Inside(DialogBoxes.Message, DialogBoxes.MessageImage, DialogBoxes.MessageText, DialogBoxes.MessageButton);
        var quests = new List<ArtBox> { DialogBoxes.QuestPagePrevious, DialogBoxes.QuestPageField, DialogBoxes.QuestPageNext, DialogBoxes.QuestButton };
        for (int row = 0; row < DialogBoxes.QuestsPerPage; row++)
        {
            quests.Add(DialogBoxes.QuestImage(row));
            quests.Add(DialogBoxes.QuestText(row));
        }

        Inside(DialogBoxes.Quests, [.. quests]);
        Inside(DialogBoxes.Load, DialogBoxes.LoadTypePrevious, DialogBoxes.LoadTypeField, DialogBoxes.LoadTypeNext);
        Inside(DialogBoxes.Story, DialogBoxes.StoryImage, DialogBoxes.StoryText, DialogBoxes.StoryButton);
    }

    [Fact]
    public void Buttons_take_one_state_of_their_strips()
    {
        // D_MESSAGE/btn and D_QUESTS/btn are 420 x 27 (two states), S_SCRPLAY/ok 600 x 25 (four).
        Assert.Equal((210, 27), (DialogBoxes.MessageButton.Width, DialogBoxes.MessageButton.Height));
        Assert.Equal((210, 27), (DialogBoxes.QuestButton.Width, DialogBoxes.QuestButton.Height));
        Assert.Equal((150, 25), (DialogBoxes.StoryButton.Width, DialogBoxes.StoryButton.Height));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    public void The_quest_log_shows_two_quests_a_page(int quests, int pages) => Assert.Equal(pages, DialogBoxes.QuestPages(quests));
}
