using System;

namespace Tzar2.Client.Logic;

/// <summary>A box of a dialog's art, relative to the art's top-left corner.</summary>
/// <param name="X">Left.</param>
/// <param name="Y">Top.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct ArtBox(int X, int Y, int Width, int Height)
{
    /// <summary>Right edge (exclusive).</summary>
    public int Right => X + Width;

    /// <summary>Bottom edge (exclusive).</summary>
    public int Bottom => Y + Height;
}

/// <summary>
/// The boxes the original's dialog art draws, measured on the GOG v1.10 bitmaps (the insides of their bevels; numbers
/// only, REQ-LEGAL-01). The dialogs put their texts, pictures and buttons in them (UI review UI-A3, UI-A10, UI-A11,
/// UI-A12; ADR 0200). Which text goes in which box is ours where the review asks the owner (question 7).
/// </summary>
public static class DialogBoxes
{
    /// <summary><c>D_MESSAGE/back</c>: 500 × 320.</summary>
    public static readonly (int Width, int Height) Message = (500, 320);

    /// <summary>The message's picture box (left).</summary>
    public static readonly ArtBox MessageImage = new(43, 45, 101, 96);

    /// <summary>The message's text box (right): the title, then the text.</summary>
    public static readonly ArtBox MessageText = new(170, 48, 285, 178);

    /// <summary>The message's button slot (a 210 × 27 state of <c>D_MESSAGE/btn</c>).</summary>
    public static readonly ArtBox MessageButton = new(205, 258, 210, 27);

    /// <summary><c>D_QUESTS/back</c>: 500 × 368.</summary>
    public static readonly (int Width, int Height) Quests = (500, 368);

    /// <summary>The quests on one page of the log: the art has two rows.</summary>
    public const int QuestsPerPage = 2;

    /// <summary>A quest row's picture box.</summary>
    public static ArtBox QuestImage(int row) => new(44, 63 + (row * 120), 94, 98);

    /// <summary>A quest row's text box.</summary>
    public static ArtBox QuestText(int row) => new(173, 63 + (row * 120), 282, 98);

    /// <summary>The page selector's left arrow.</summary>
    public static readonly ArtBox QuestPagePrevious = new(38, 305, 25, 27);

    /// <summary>The page selector's field.</summary>
    public static readonly ArtBox QuestPageField = new(68, 305, 122, 27);

    /// <summary>The page selector's right arrow.</summary>
    public static readonly ArtBox QuestPageNext = new(191, 305, 25, 27);

    /// <summary>The quest log's button slot (a 210 × 27 state of <c>D_QUESTS/btn</c>).</summary>
    public static readonly ArtBox QuestButton = new(245, 305, 210, 27);

    /// <summary><c>D_LOAD/back</c>: 550 × 320.</summary>
    public static readonly (int Width, int Height) Load = (550, 320);

    /// <summary>The load dialog's type selector: left arrow, field, right arrow.</summary>
    public static readonly ArtBox LoadTypePrevious = new(331, 33, 26, 25);

    /// <summary>The type selector's field.</summary>
    public static readonly ArtBox LoadTypeField = new(362, 33, 122, 25);

    /// <summary>The type selector's right arrow.</summary>
    public static readonly ArtBox LoadTypeNext = new(486, 33, 26, 25);

    /// <summary><c>S_SCRPLAY/back</c>: 800 × 600.</summary>
    public static readonly (int Width, int Height) Story = (800, 600);

    /// <summary>The story screen's picture box (left).</summary>
    public static readonly ArtBox StoryImage = new(94, 76, 309, 404);

    /// <summary>The story screen's text, on the parchment (right).</summary>
    public static readonly ArtBox StoryText = new(515, 190, 235, 360);

    /// <summary>The story screen's CLOSE slot (a 150 × 25 state of <c>S_SCRPLAY/ok</c>).</summary>
    public static readonly ArtBox StoryButton = new(166, 538, 150, 25);

    /// <summary><c>D_ENDGAME/back2</c>: 500 × 415.</summary>
    public static readonly (int Width, int Height) Endgame = (500, 415);

    /// <summary>The end-of-game dialog's button slots, top to bottom (a 210 × 27 state of <c>D_ENDGAME/btn</c>; UI-A4).</summary>
    public static ArtBox EndgameSlot(int slot) => new(233, 113 + (slot * 48), 210, 27);

    /// <summary><c>D_OPTIONS/back</c>: 800 × 600.</summary>
    public static readonly (int Width, int Height) Options = (800, 600);

    /// <summary>A Menu Layout box of the options' interface panel (a 38 × 27 state of <c>layout1</c>-<c>3</c>; UI-A9).</summary>
    public static ArtBox OptionsLayout(int box) => new(469 + (box * 54), 417, 38, 27);

    /// <summary><c>S_CREDITS/back</c>: 800 × 600.</summary>
    public static readonly (int Width, int Height) Credits = (800, 600);

    /// <summary>The credits' text, on the book's right page (light from x 475 to 745 and y 140 to 575, less a margin).</summary>
    public static readonly ArtBox CreditsPage = new(490, 160, 240, 400);

    /// <summary>The number of pages of a quest log of <paramref name="quests"/> quests (at least one).</summary>
    public static int QuestPages(int quests) => Math.Max(1, (quests + QuestsPerPage - 1) / QuestsPerPage);
}
