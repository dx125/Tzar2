using System;
using System.Collections.Generic;
using Tzar2.Core.Ids;
using Tzar2.Game.Input;
using Tzar2.Game.Session;
using Tzar2.Sim.Commands;

namespace Tzar2.Game.Ui;

/// <summary>What a command-card button does (ORDERS.INI actions and Tzar2's own).</summary>
public enum CardAction
{
    /// <summary>Not supported by this build (shown disabled).</summary>
    Unsupported = 0,
#pragma warning disable CS1591 // Named after ORDERS.INI actions.
    Move, Attack, Harvest, Patrol, Guard, Repair, Stop, Train, Build, Submenu, Back, Cancel,
#pragma warning restore CS1591

    /// <summary>Research technology <see cref="CardButton.Argument"/> (its key) in the selected building.</summary>
    Research,

    /// <summary>Buy upgrade <see cref="CardButton.Argument"/> (its key) for the selected towers, walls, gates or ships.</summary>
    Upgrade,

    /// <summary>Peasants capture the clicked enemy building.</summary>
    Capture,

    /// <summary>Move to the clicked point fighting what comes into sight (Ctrl + right click).</summary>
    MoveAdvance,

    /// <summary>Move to the clicked point without stopping to fight (Alt + right click).</summary>
    MoveSneak,

    /// <summary>Combat stance <see cref="CardButton.Argument"/>: 0 aggressive, 1 stand ground, 2 hold fire.</summary>
    Stance,

    /// <summary>Gate control <see cref="CardButton.Argument"/>: open, close, auto, manual.</summary>
    Gate,

    /// <summary>Explore unexplored ground.</summary>
    Explore,

    /// <summary>Destroy the selected own units or buildings (Delete).</summary>
    Delete,

    /// <summary>Let the units out of a castle, outpost or ship.</summary>
    Unload,

    /// <summary>Turn the selected units into class <see cref="CardButton.Argument"/> (become priest, become Jihad warrior).</summary>
    Become,

    /// <summary>Siege weapons attack the clicked ground.</summary>
    AttackGround,

    /// <summary>Select the next idle peasant.</summary>
    NextIdle,

    /// <summary>Show the quest log.</summary>
    Quests,

    /// <summary>AI assistance <see cref="CardButton.Argument"/>: 0 none, 1 economy, 2 war, 3 full (ADR 0183).</summary>
    AiAssist,

    /// <summary>Opens a list of the kingdom's <see cref="CardButton.Argument"/>: <c>heroes</c>, <c>spies</c> or <c>items</c> (HUD-033).</summary>
    KingdomList,

    /// <summary>Selects unit <see cref="CardButton.Argument"/> (<c>EntityId.Value</c>) and shows it: an entry of a kingdom list.</summary>
    SelectUnit,

    /// <summary>The next page of a card with more orders than slots (Tzar2's; <see cref="CardButton.Argument"/> = "2/3").</summary>
    Page,

    /// <summary>Cast spell <see cref="CardButton.Argument"/> (its key; the ninjas' cloak and murder and the necromancers'
    /// raise dead are spells named by their order): at once on themselves, else on the clicked unit or ground.</summary>
    Spell,

    /// <summary>A loan of resource <see cref="CardButton.Argument"/> in the selected tavern, or "return" to repay it.</summary>
    Loan,

    /// <summary>Peasants bribe the clicked enemy unit.</summary>
    Bribe,

    /// <summary>The selected cathedral preaches a crusade at the clicked point.</summary>
    Crusade,

    /// <summary>The selected units pick up the clicked item.</summary>
    PickItem,

    /// <summary>Use the default item: on the clicked target, or "self" at once.</summary>
    UseItem,

    /// <summary>Drop the first item where the unit stands.</summary>
    DropItem,

    /// <summary>Gambling in the selected tavern (its dialog in the card's place).</summary>
    Gamble,

    /// <summary>Exchanging resources in the selected tavern, ten for one (its dialog).</summary>
    Exchange,

    /// <summary>A trade route of the selected traders between a near and a far market (its dialog, then a click on the far one).</summary>
    Trade,

    /// <summary>The magic workshop makes an item (order <c>build_item</c>): its dialog picks the kind and the gold.</summary>
    Craft,

    /// <summary>Hire tavern offer <see cref="CardButton.Argument"/> (its slot): a mercenary group or a hero (Tzar2 fills the
    /// original's empty <c>tavern_merc</c> / <c>tavern_hero</c> menus with the offers of the period).</summary>
    Hire,
}

/// <summary>A button of a command card (for the original: a MENU.INI entry and its ORDERS.INI order).</summary>
/// <param name="Order">The order's name (ORDERS.INI section).</param>
/// <param name="Hotkey">Letter, or '\0'.</param>
/// <param name="Action">What it does.</param>
/// <param name="Argument">Class to train or build, the submenu's name, a technology, a resource...</param>
/// <param name="ImageIndex">Cell of the order buttons' sheet, or −1.</param>
/// <param name="Tooltip">The order's name for people (STRINGS.INI <c>[orders_names]</c>).</param>
public sealed record CardButton(string Order, char Hotkey, CardAction Action, string Argument, int ImageIndex, string Tooltip);

/// <summary>One item of a card button's price in its tooltip.</summary>
/// <param name="Icon">The icon asset (a resource's, or the hourglass).</param>
/// <param name="Value">The amount or the time.</param>
/// <param name="Short">Whether the kingdom has less of the resource than this (drawn red).</param>
public readonly record struct PriceItem(string Icon, string Value, bool Short = false);

/// <summary>A command-card slot as the client draws it.</summary>
/// <param name="Button">The button.</param>
/// <param name="Enabled">Whether it can be used now.</param>
/// <param name="Active">Whether its order is pending (highlighted).</param>
/// <param name="Detail">What the tooltip adds in words: why the button is greyed out, a mana cost, a page.</param>
/// <param name="Count">How many of its item the selected buildings have queued, shown on the button (ADR 0180).</param>
/// <param name="Price">The tooltip's price: resource and time icons with their values.</param>
/// <param name="Description">What the order does (STRINGS.INI <c>orders_add</c>), or empty.</param>
public readonly record struct CardSlot(CardButton Button, bool Enabled, bool Active, string Detail = "", int Count = 0, IReadOnlyList<PriceItem>? Price = null, string Description = "")
{
    /// <summary>Equality by what is shown (the price rows by value).</summary>
    public bool Equals(CardSlot other)
        => Button == other.Button && Enabled == other.Enabled && Active == other.Active && Detail == other.Detail && Count == other.Count
            && Description == other.Description && SamePrice(Price, other.Price);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Button, Enabled, Active, Detail, Count, Description);

    private static bool SamePrice(IReadOnlyList<PriceItem>? a, IReadOnlyList<PriceItem>? b)
    {
        if (a is null || b is null)
        {
            return (a?.Count ?? 0) == (b?.Count ?? 0);
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Where the command card's buttons and the names it shows come from: for the original, its imported MENU.INI,
/// ORDERS.INI, STRINGS.INI and object INIs (the client's <c>PresentationCatalog</c>); a new game supplies its own.
/// </summary>
public interface ICardSource
{
    /// <summary>The buttons of card (menu) <paramref name="menu"/>, in order; empty when there is none.</summary>
    IReadOnlyList<CardButton> Card(string? menu);

    /// <summary>
    /// Whether the letters of the buttons in <paramref name="menu"/>'s submenus work from the menu itself (MENU.INI
    /// <c>capturekeys</c>: in the build menu, the letter of a building in a category builds it at once).
    /// </summary>
    bool CapturesKeys(string? menu);

    /// <summary>The menu of an entity type's card, or null.</summary>
    string? Menu(ushort typeId);

    /// <summary>
    /// The kingdom's card, shown with nothing selected (lists of heroes, spies and items, quests, the next idle peasant;
    /// with <paramref name="aiAssist"/> also AI assistance), or null.
    /// </summary>
    string? KingdomMenu(bool aiAssist);

    /// <summary>The civilization an entity type belongs to (empty for the common ones).</summary>
    string Civilization(ushort typeId);

    /// <summary>The name of an entity type for people.</summary>
    string Name(ushort typeId);

    /// <summary>The entity type of class <paramref name="cls"/> for a civilization (its own, else the common one), or −1.</summary>
    int TypeOfClass(string civilization, string cls);

    /// <summary>The order-button image that trains entity type <paramref name="typeId"/>, or −1.</summary>
    int TrainImage(ushort typeId);

    /// <summary>A text of the game (STRINGS.INI), or <paramref name="fallback"/>.</summary>
    string Text(string section, string key, string fallback);
}

/// <summary>What the command card is built for: the frame, the selection and the order waiting for a target.</summary>
/// <param name="Frame">The newest frame.</param>
/// <param name="Selection">The selected entities, in order.</param>
/// <param name="Focus">The focused entity of the selection (Tab), whose card shows.</param>
/// <param name="IsOwn">Whether the selection is the local kingdom's.</param>
/// <param name="Placing">Whether a building is being placed (no card then).</param>
/// <param name="Mode">The order waiting for a click on the world.</param>
/// <param name="ModeArgument">Its argument (a spell).</param>
/// <param name="HasQuests">Whether the map has a quest log.</param>
/// <param name="Picked">The unit picked in the list of those inside the selected building (ORD-015), or none.</param>
/// <param name="AiAssist">Whether the match offers AI assistance (ADR 0183).</param>
public sealed record CardContext(
    GameFrame Frame,
    IReadOnlyList<EntityId> Selection,
    int Focus = 0,
    bool IsOwn = true,
    bool Placing = false,
    OrderMode Mode = OrderMode.None,
    int ModeArgument = 0,
    bool HasQuests = false,
    EntityId Picked = default,
    bool AiAssist = false);

/// <summary>A dialog a card button opens in the card's place.</summary>
public enum CardDialogKind
{
    /// <summary>None.</summary>
    None,

    /// <summary>Gambling in a tavern.</summary>
    Gamble,

    /// <summary>Exchanging resources in a tavern.</summary>
    Exchange,

    /// <summary>A trader's route (then a click on the far market).</summary>
    Trade,

    /// <summary>Making an item in a magic workshop: its kind and the gold to spend (D_BUILD).</summary>
    Craft,
}

/// <summary>What pressing a card button asks of the client; the card's own menus and pages are already updated.</summary>
/// <param name="Commands">Commands to submit, or null.</param>
/// <param name="Mode">An order that waits for a click on the world, or null.</param>
/// <param name="ModeArgument">Its argument (a spell).</param>
/// <param name="Placing">A building type to place, or null.</param>
/// <param name="Dialog">A dialog to open in the card's place.</param>
/// <param name="Message">A message for the player, or null.</param>
/// <param name="NextIdlePeasant">Select the next idle peasant.</param>
/// <param name="ShowQuests">Show the quest log.</param>
/// <param name="DeleteSelected">Delete the selection (as the Delete key).</param>
/// <param name="Select">Select this unit and show it (an entry of a kingdom list).</param>
public sealed record CardPress(
    CommandList? Commands = null,
    OrderMode? Mode = null,
    int ModeArgument = 0,
    ushort? Placing = null,
    CardDialogKind Dialog = CardDialogKind.None,
    string? Message = null,
    bool NextIdlePeasant = false,
    bool ShowQuests = false,
    bool DeleteSelected = false,
    EntityId? Select = null)
{
    /// <summary>Nothing to do (the card may have changed menu or page).</summary>
    public static CardPress Nothing { get; } = new();
}
