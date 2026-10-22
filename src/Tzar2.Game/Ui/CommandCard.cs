using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Core.Ids;
using Tzar2.Game.Input;
using Tzar2.Game.Session;
using Tzar2.Sim.Commands;

namespace Tzar2.Game.Ui;

/// <summary>
/// The command card as a model, free of any engine (ADR 0182): which buttons the focused entity's card shows, whether
/// each works now and why not, its price, time and queued count, and what pressing it does. The client draws the slots
/// and carries out the <see cref="CardPress"/>; the rules of the card are tested here (docs/rules HUD, PROD, ORD).
/// </summary>
public sealed class CommandCard
{
    /// <summary>The slots of a card; with more orders the arrows by the orders bar turn pages of this many (HUD-019).</summary>
    public const int Slots = 8;

    private readonly Ruleset _rules;
    private readonly ICardSource _source;
    private readonly byte _me;
    private readonly Stack<string> _menus = new();

    // The menu of a kingdom list on the stack (a name no card source uses), followed by the list's kind.
    private const string ListMenu = "\u0001list:";
    private readonly Dictionary<uint, int> _index = [];
    private GameFrame? _indexed;
    private int _page;
    private EntityId _carrier;

    /// <summary>Creates the card of a kingdom.</summary>
    /// <param name="rules">The ruleset of the match.</param>
    /// <param name="source">Where the buttons and names come from.</param>
    /// <param name="localPlayer">The kingdom whose card it is.</param>
    public CommandCard(Ruleset rules, ICardSource source, byte localPlayer)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(source);
        _rules = rules;
        _source = source;
        _me = localPlayer;
    }

    /// <summary>The submenu open on the card (the top of the stack), or null for the entity's own card.</summary>
    public string? OpenMenu => _menus.Count > 0 ? _menus.Peek() : null;

    /// <summary>Every button of the current card, before paging (hotkeys reach every page).</summary>
    public IReadOnlyList<CardSlot> WholeCard { get; private set; } = [];

    /// <summary>The page of the card shown (HUD-019).</summary>
    public int Page { get; private set; }

    /// <summary>How many pages the card has.</summary>
    public int PageCount { get; private set; } = 1;

    /// <summary>Whether the inventory of <see cref="InventoryCarrier"/> fills the card (the eye, HUD-017).</summary>
    public bool InventoryOpen => !_carrier.IsNone;

    /// <summary>The unit whose inventory is open, or none.</summary>
    public EntityId InventoryCarrier => _carrier;

    /// <summary>Goes back to the previous menu (Esc, the card's Back) or closes the inventory; false when the entity's own card is open.</summary>
    public bool Back()
    {
        _page = 0;
        if (InventoryOpen)
        {
            _carrier = EntityId.None;
            return true;
        }

        return _menus.TryPop(out _);
    }

    /// <summary>Back to the entity's own card (a new selection).</summary>
    public void Reset()
    {
        _menus.Clear();
        _page = 0;
        _carrier = EntityId.None;
    }

    /// <summary>The arrows by the orders bar (HUD-019): <paramref name="delta"/> pages on, within the card.</summary>
    public void TurnPage(int delta) => _page = Math.Clamp(_page + delta, 0, Math.Max(0, PageCount - 1));

    /// <summary>
    /// The own unit whose items the eye shows (HUD-017): the focused unit when it carries items, else the first selected
    /// one that does; none when no selected unit of the kingdom carries anything.
    /// </summary>
    public EntityId Carrier(CardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsOwn || context.Selection.Count == 0)
        {
            return EntityId.None;
        }

        EntityId focus = context.Selection[Math.Clamp(context.Focus, 0, context.Selection.Count - 1)];
        if (Carries(context, focus))
        {
            return focus;
        }

        foreach (EntityId id in context.Selection)
        {
            if (Carries(context, id))
            {
                return id;
            }
        }

        return EntityId.None;
    }

    /// <summary>The eye: opens the carrier's inventory on the card, or closes it; true when it is open after.</summary>
    public bool ToggleInventory(CardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _page = 0;
        _carrier = InventoryOpen ? EntityId.None : Carrier(context);
        return InventoryOpen;
    }

    private bool Carries(CardContext context, EntityId unit)
    {
        Dictionary<uint, int> index = Index(context.Frame);
        if (!index.TryGetValue(unit.Value, out int i) || context.Frame.Entities[i].Owner != _me)
        {
            return false;
        }

        foreach (FrameHeld h in context.Frame.Helds)
        {
            if (h.IsItem && h.Holder == unit.Value)
            {
                return true;
            }
        }

        return false;
    }

    // The open inventory: the carrier's items by slot, the default item framed (HUD-017/018).
    private List<CardSlot> InventorySlots(CardContext context)
    {
        var items = new List<FrameHeld>();
        foreach (FrameHeld h in context.Frame.Helds)
        {
            if (h.IsItem && h.Holder == _carrier.Value)
            {
                items.Add(h);
            }
        }

        items.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        var slots = new List<CardSlot>(items.Count);
        foreach (FrameHeld h in items)
        {
            string name = h.Look is { } look ? _source.Text("images", look, _source.Name(h.TypeId)) : _source.Name(h.TypeId);
            if (h.Spell >= 0 && h.Spell < _rules.Spells.Count)
            {
                string spell = _rules.Spells[h.Spell].Key;
                name += ": " + _source.Text("spell_names", spell, spell);
            }

            var button = new CardButton("item", '\0', CardAction.InventoryItem, h.Slot.ToString(CultureInfo.InvariantCulture), _source.ItemIcon(h.TypeId, h.Look), name);
            slots.Add(new CardSlot(button, true, h.IsDefault, string.Empty, Description: "Left click: use (Ctrl: at once); right click: drop; Alt + click: the default item"));
        }

        return slots;
    }

    /// <summary>The slots to draw: the focused own entity's card (or the current submenu), paged; empty otherwise.</summary>
    public IReadOnlyList<CardSlot?> Build(CardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var card = new List<CardSlot?>();
        var whole = new List<CardSlot>();
        WholeCard = whole;
        if (context.Placing)
        {
            return card;
        }

        if (InventoryOpen)
        {
            if (!context.Selection.Contains(_carrier) || !Carries(context, _carrier))
            {
                _carrier = EntityId.None; // the carrier is no longer selected or carries nothing
                _page = 0;
            }
            else
            {
                whole.AddRange(InventorySlots(context));
                return Paged(card, whole);
            }
        }

        FrameEntity e = default;
        string civ = string.Empty;
        string? menu;
        if (context.Selection.Count == 0)
        {
            // Nothing selected: the kingdom's card (HUD-033), with AI assistance where the match offers it (AI-004).
            menu = OpenMenu ?? _source.KingdomMenu(context.AiAssist);
        }
        else if (context.IsOwn && Lead(context) is { } lead)
        {
            e = context.Frame.Entities[lead];
            civ = _source.Civilization(e.TypeId);
            if (e.ConstructionPermille >= 0)
            {
                card.Add(new CardSlot(new CardButton("cancel", 'C', CardAction.Cancel, string.Empty, 109, "Cancel construction"), true, false));
                return card;
            }

            menu = OpenMenu ?? _source.Menu(e.TypeId);
            if (OpenMenu is null && e.Cloaked)
            {
                menu = _source.MenuVariant(menu, "cloaked") ?? menu;
            }
        }
        else
        {
            return card;
        }

        var buttons = (menu?.StartsWith(ListMenu, StringComparison.Ordinal) == true ? KingdomList(context, menu[ListMenu.Length..])
            : TavernMenu(menu) is { } heroes ? HireButtons(context, heroes) : _source.Card(menu)).ToList();
        if (OpenMenu is null && !e.Id.IsNone && ((_rules.EntityTypes[e.TypeId].Traits.Abilities & AbilityFlags.Impersonate) != 0 || e.SpyType >= 0))
        {
            buttons.AddRange(_source.AbilityButtons("impersonate")); // the spy's impersonate and reveal (MAG-012)
        }

        buttons = [.. buttons.Where(b => !Hidden(context, b, e))];
        if (buttons.Count > Slots)
        {
            // What this build cannot do yet goes to the last page.
            buttons = [.. buttons.Where(b => b.Action != CardAction.Unsupported), .. buttons.Where(b => b.Action == CardAction.Unsupported)];
        }

        bool captures = _source.CapturesKeys(menu);
        foreach (CardButton b in buttons)
        {
            (bool enabled, string detail) = Availability(context, b, civ);
            (ImmutableArray<int> cost, int timeMs) = Price(context, b, civ);
            string description = _source.Text("orders_add", b.Order, string.Empty).Trim();
            if (captures && b.Action == CardAction.Submenu && CapturedKeys(b.Argument) is { Length: > 0 } keys)
            {
                // A category of the build menu has no letter of its own; its hint lists the letters of what is in it, which
                // work from the build menu itself (the owner's fourth play-test).
                description = description.Length > 0 ? description + " " + keys : keys;
            }

            whole.Add(new CardSlot(b, enabled, Active(context, b), detail, Queued(context, b, civ), PriceRows(context, cost, timeMs), description));
        }

        return Paged(card, whole);
    }

    // More orders than slots (a peasant has nine): pages of eight, turned by the arrows (HUD-019).
    private List<CardSlot?> Paged(List<CardSlot?> card, List<CardSlot> whole)
    {
        PageCount = Math.Max(1, (whole.Count + Slots - 1) / Slots);
        _page = Math.Clamp(_page, 0, PageCount - 1);
        Page = _page;
        card.AddRange(whole.Skip(_page * Slots).Take(Slots).Select(c => (CardSlot?)c));
        return card;
    }

    /// <summary>
    /// The slot a letter presses on the current card: a button with that letter, or, on a menu that captures its
    /// submenus' keys (the build menu), the button with that letter in one of its submenus. Null when there is none.
    /// </summary>
    /// <param name="context">What the card was built for (after <see cref="Build"/>).</param>
    /// <param name="letter">The letter, upper case.</param>
    public CardSlot? SlotOfKey(CardContext context, char letter)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (CardSlot slot in WholeCard)
        {
            if (slot.Button.Hotkey == letter)
            {
                return slot;
            }
        }

        if (Lead(context) is not { } lead || !_source.CapturesKeys(OpenMenu ?? _source.Menu(context.Frame.Entities[lead].TypeId)))
        {
            return null;
        }

        string civ = _source.Civilization(context.Frame.Entities[lead].TypeId);
        foreach (CardSlot category in WholeCard)
        {
            if (category.Button.Action != CardAction.Submenu)
            {
                continue;
            }

            foreach (CardButton b in _source.Card(category.Button.Argument))
            {
                if (b.Hotkey == letter && !Hidden(context, b, context.Frame.Entities[lead]))
                {
                    (bool enabled, string detail) = Availability(context, b, civ);
                    return new CardSlot(b, enabled, false, detail);
                }
            }
        }

        return null;
    }

    /// <summary>Presses a slot: a left click, or a right click (one queued item less).</summary>
    /// <param name="context">What the card was built for.</param>
    /// <param name="slot">The slot pressed.</param>
    /// <param name="right">A right click.</param>
    /// <param name="shift">Shift held (five of a unit).</param>
    /// <param name="alt">Alt held (an item: make it the default item).</param>
    /// <param name="ctrl">Ctrl held (an item: use it at once, without a target).</param>
    public CardPress Press(CardContext context, CardSlot slot, bool right = false, bool shift = false, bool alt = false, bool ctrl = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        CardButton b = slot.Button;
        if (b.Action == CardAction.InventoryItem && int.TryParse(b.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int itemSlot) && InventoryOpen)
        {
            return PressItem(context, itemSlot, right, alt, ctrl);
        }

        if (b.Action == CardAction.Page)
        {
            _page++;
            return CardPress.Nothing;
        }

        if (!slot.Enabled)
        {
            return new CardPress(Message: slot.Detail.Length > 0 ? $"{b.Tooltip}: {slot.Detail}" : b.Tooltip + ": not available yet");
        }

        List<EntityInfo> selected = Selected(context);
        string civ = Lead(context) is { } lead ? _source.Civilization(context.Frame.Entities[lead].TypeId) : string.Empty;
        switch (b.Action)
        {
            case CardAction.Move:
                return new CardPress(Mode: OrderMode.Move);
            case CardAction.Attack:
                return new CardPress(Mode: OrderMode.Attack);
            case CardAction.Harvest:
                return new CardPress(Mode: OrderMode.Harvest);
            case CardAction.Patrol:
                return new CardPress(Mode: OrderMode.Patrol);
            case CardAction.Guard:
                return new CardPress(Mode: OrderMode.Guard);
            case CardAction.Repair:
                return new CardPress(Mode: OrderMode.Repair);
            case CardAction.Capture:
                return new CardPress(Mode: OrderMode.Capture);
            case CardAction.MoveAdvance:
                return new CardPress(Mode: OrderMode.MoveAdvance);
            case CardAction.MoveSneak:
                return new CardPress(Mode: OrderMode.MoveSneak);
            case CardAction.AttackGround:
                return new CardPress(Mode: OrderMode.AttackGround);
            case CardAction.Bribe:
                return new CardPress(Mode: OrderMode.Bribe);
            case CardAction.Crusade:
                return new CardPress(Mode: OrderMode.Crusade);
            case CardAction.PickItem:
                return new CardPress(Mode: OrderMode.PickItem);
            case CardAction.UseItem when b.Argument != "self":
                return new CardPress(Mode: OrderMode.UseItem);
            case CardAction.Stop:
                return Commands(CommandKind.Stop, OwnIds(selected, e => e.IsMobile));
            case CardAction.Submenu:
                _menus.Push(b.Argument);
                _page = 0;
                return CardPress.Nothing;
            case CardAction.Back:
                Back();
                return CardPress.Nothing;
            case CardAction.Build:
                int building = _source.TypeOfClass(civ, b.Argument);
                return building >= 0 ? new CardPress(Placing: (ushort)building) : CardPress.Nothing;
            case CardAction.Train:
                int unit = _source.TypeOfClass(civ, b.Argument);
                EntityId[] producers = OwnIds(selected, e => e.IsProducer && !e.UnderConstruction);
                if (unit < 0 || producers.Length == 0)
                {
                    return CardPress.Nothing;
                }

                // A right click takes one of this unit off the queue, Shift five, as Shift+click queues five (ADR 0180; the
                // owner, 2026-10-21).
                if (right)
                {
                    var cancel = new CommandList();
                    for (int n = 0; n < (shift ? 5 : 1); n++)
                    {
                        cancel.Add(CommandKind.Cancel, producers, arg0: -1, arg1: unit + 1);
                    }

                    return new CardPress(Commands: cancel);
                }

                return Commands(CommandKind.Train, producers, arg0: unit, arg1: shift ? 5 : 1);
            case CardAction.Cancel:
                return Commands(CommandKind.Cancel, OwnIds(selected, e => e.UnderConstruction));
            case CardAction.Research when _rules.TechnologyId(b.Argument) is >= 0 and var tech:
                return Commands(CommandKind.Research, OwnIds(selected, e => e.IsProducer && !e.UnderConstruction), arg0: tech);
            case CardAction.Upgrade when _rules.UpgradeId(b.Argument) is >= 0 and var upgrade:
                return Commands(CommandKind.Upgrade, OwnIds(selected, e => !e.UnderConstruction), arg0: upgrade);
            case CardAction.Stance when int.TryParse(b.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int stance):
                return Commands(CommandKind.SetStance, OwnIds(selected, e => e.IsArmed), arg0: stance);
            case CardAction.Gate:
                var gates = new CommandList();
                foreach (EntityInfo gate in selected.Where(e => e.Owner == _me))
                {
                    FrameGate state = Index(context.Frame).TryGetValue(gate.Id.Value, out int gi) ? context.Frame.Entities[gi].Gate : FrameGate.None;
                    int mode = b.Argument switch { "open" => 1, "close" => 0, "auto" => 2, _ => (state & FrameGate.Open) != 0 ? 1 : 0 };
                    if (state != FrameGate.None)
                    {
                        gates.Add(CommandKind.SetGate, [gate.Id], arg0: mode);
                    }
                }

                return gates.Count > 0 ? new CardPress(Commands: gates) : CardPress.Nothing;
            case CardAction.Explore when selected.Any(e => e.Owner == _me && e.IsMobile):
                return new CardPress(Mode: OrderMode.Explore); // the area is clicked next (manual: Explore; ORD-007)
            case CardAction.Delete:
                return new CardPress(DeleteSelected: true);
            case CardAction.Unload when selected.Any(e => e.Owner == _me && e.IsMobile && InsideCount(context, e.Id) > 0):
                // Ships and carpets let their units out at the place clicked next (ORDERS.INI unload: PLACE command_unload).
                return new CardPress(Mode: OrderMode.Unload);
            case CardAction.Unload when HolderOfPicked(context) is { } pickedFrom && selected.Any(e => e.Owner == _me && e.Id == pickedFrom):
                // A unit picked in a building's list comes out at the place clicked next (manual: Emerge).
                return new CardPress(Mode: OrderMode.Unload, ModeArgument: unchecked((int)context.Picked.Value));
            case CardAction.Unload:
                // Emerge without a pick: everyone comes out next to the building at once (ORDERS.INI emerge has no place).
                var unload = new CommandList();
                foreach (EntityInfo holder in selected.Where(e => e.Owner == _me && InsideCount(context, e.Id) > 0))
                {
                    if (Index(context.Frame).TryGetValue(holder.Id.Value, out int hi))
                    {
                        unload.Add(CommandKind.Unload, [holder.Id], context.Frame.Entities[hi].X, context.Frame.Entities[hi].Y);
                    }
                }

                return unload.Count > 0 ? new CardPress(Commands: unload) : CardPress.Nothing;
            case CardAction.Become when _source.TypeOfClass(civ, b.Argument) is >= 0 and var into:
                return Commands(CommandKind.ConvertUnit, OwnIds(selected, e => e.IsMobile), arg0: into);
            case CardAction.NextIdle:
                return new CardPress(NextIdlePeasant: true);
            case CardAction.Uncloak:
                return Commands(CommandKind.Uncloak, OwnIds(selected, e => e.IsMobile));
            case CardAction.Impersonate:
                return new CardPress(Mode: OrderMode.Impersonate);
            case CardAction.Reveal:
                return Commands(CommandKind.RevealSelf, OwnIds(selected, e => e.IsMobile));
            case CardAction.KingdomList:
                _menus.Push(ListMenu + b.Argument);
                _page = 0;
                return CardPress.Nothing;
            case CardAction.SelectUnit when uint.TryParse(b.Argument, NumberStyles.None, CultureInfo.InvariantCulture, out uint listed):
                return new CardPress(Select: EntityId.FromValue(listed));
            case CardAction.AiAssist when context.AiAssist && int.TryParse(b.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int assist):
                var help = new CommandList();
                help.Add(CommandKind.SetAiAssist, [], arg0: assist);
                return new CardPress(Commands: help);
            case CardAction.Quests when context.HasQuests:
                return new CardPress(ShowQuests: true);
            case CardAction.Quests:
                return new CardPress(Message: _source.Text("sp_setupmap", "vcond_0", "Standard") + ": destroy every enemy unit and building.");
            case CardAction.Spell when SpellOf(b) is >= 0 and var spell:
                return _rules.Spells[spell].Target == SpellTarget.Self
                    ? Commands(CommandKind.CastSpell, OwnIds(selected, e => CanCast(e, spell)), arg0: spell)
                    : new CardPress(Mode: OrderMode.Spell, ModeArgument: spell);
            case CardAction.Loan when b.Argument == "return":
                return Commands(CommandKind.Loan, OwnIds(selected, e => !e.IsMobile && !e.UnderConstruction), arg1: 1);
            case CardAction.Loan when _rules.ResourceId(b.Argument) is >= 0 and var resource:
                return Commands(CommandKind.Loan, OwnIds(selected, e => !e.IsMobile && !e.UnderConstruction), arg0: resource);
            case CardAction.UseItem:
                var use = new CommandList();
                foreach (EntityInfo user in selected.Where(e => e.Owner == _me && e.IsMobile))
                {
                    use.Add(CommandKind.UseItem, [user.Id], target: user.Id, arg0: -1);
                }

                return use.Count > 0 ? new CardPress(Commands: use) : CardPress.Nothing;
            case CardAction.DropItem:
                return Commands(CommandKind.DropItem, OwnIds(selected, e => e.IsMobile), arg0: 0);
            case CardAction.Hire when int.TryParse(b.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int offer):
                return Commands(CommandKind.Hire, Taverns(context), arg0: offer);
            case CardAction.Gamble:
                return new CardPress(Dialog: CardDialogKind.Gamble);
            case CardAction.Exchange:
                return new CardPress(Dialog: CardDialogKind.Exchange);
            case CardAction.Trade:
                return new CardPress(Dialog: CardDialogKind.Trade);
            case CardAction.Craft:
                return new CardPress(Dialog: CardDialogKind.Craft);
            default:
                return CardPress.Nothing;
        }
    }

    /// <summary>The gamble the tavern dialog asked for, or null without an own tavern selected.</summary>
    public CommandList? Gamble(CardContext context, string resource, int odds, int stake)
        => List(CommandKind.Gamble, Taverns(context), arg0: _rules.ResourceId(resource) | (odds << 8), arg1: stake);

    /// <summary>The exchange the tavern dialog asked for (ten given for one), or null without an own tavern selected.</summary>
    public CommandList? Exchange(CardContext context, string give, string get, int amount)
        => List(CommandKind.Convert, Taverns(context), arg0: _rules.ResourceId(give) | (_rules.ResourceId(get) << 8), arg1: amount);

    /// <summary>
    /// The units inside the focused holder of the selection, if it is the local kingdom's (the selection view's list;
    /// a click picks one for Emerge, ORD-015); empty otherwise.
    /// </summary>
    public IReadOnlyList<FrameHeld> Inside(CardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Lead(context) is not { } lead || context.Frame.Entities[lead] is not { Inside: > 0 } holder || holder.Owner != _me)
        {
            return [];
        }

        var list = new List<FrameHeld>();
        foreach (FrameHeld h in context.Frame.Helds)
        {
            if (h.Holder == holder.Id.Value && !h.IsItem)
            {
                list.Add(h);
            }
        }

        return list;
    }

    /// <summary>The item types the selected magic workshops make (GAME-10), in the rules' order; empty without one.</summary>
    public IReadOnlyList<ushort> CraftableItems(CardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (EntityInfo e in Selected(context))
        {
            if (e.Owner == _me && !e.UnderConstruction && _rules.EntityTypes[e.TypeId].Traits.Crafter is { } crafter)
            {
                return crafter.Items;
            }
        }

        return [];
    }

    /// <summary>
    /// What the selected magic workshops make (ITEM-016): every variant of every item type, by the panel's three kinds
    /// (books, potions, power items), in the rules' order; empty without one.
    /// </summary>
    public IReadOnlyList<CraftOption> CraftOptions(CardContext context)
    {
        var options = new List<CraftOption>();
        foreach (ushort type in CraftableItems(context))
        {
            ItemDefinition item = _rules.EntityTypes[type].Traits.Item!;
            CraftKind kind = item.Kind switch { ItemKind.SpellBook => CraftKind.Book, ItemKind.Potion or ItemKind.PlusPlus => CraftKind.Potion, _ => CraftKind.Power };
            for (int v = 0; v < item.CraftVariantCount; v++)
            {
                CraftVariant what = item.CraftVariantAt(v);
                int spell = what.Spell >= 0 ? what.Spell : item.Spell;
                options.Add(new CraftOption(type, v, kind, what.Effect, spell, what.Look > 0 ? item.Looks[what.Look - 1] : null));
            }
        }

        return options;
    }

    /// <summary>The gold one point of an item's power costs (books cost more, 1.05).</summary>
    public int CraftPricePerPower(ushort itemType)
    {
        CombatSettings c = _rules.Combat;
        return _rules.EntityTypes[itemType].Traits.Item is { Kind: ItemKind.SpellBook } ? c.CraftGoldPerPower * c.CraftBookCostPercent / 100 : c.CraftGoldPerPower;
    }

    /// <summary>The most power an item may be made with.</summary>
    public int CraftMaxPower => _rules.Combat.CraftMaxPower;

    /// <summary>The item the dialog asked for (its type and variant), made by the first idle selected workshop; null without one.</summary>
    public CommandList? Craft(CardContext context, ushort itemType, int gold, int variant = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        Dictionary<uint, int> index = Index(context.Frame);
        foreach (EntityInfo e in Selected(context))
        {
            if (e.Owner == _me && !e.UnderConstruction && _rules.EntityTypes[e.TypeId].Traits.Crafter is not null
                && index.TryGetValue(e.Id.Value, out int i) && context.Frame.Entities[i].CraftPermille < 0)
            {
                return List(CommandKind.CraftItem, [e.Id], x: variant, arg0: itemType, arg1: gold);
            }
        }

        return null;
    }

    /// <summary>
    /// The trade route of the selected traders to the clicked far market or harbor: they load at the own market of their
    /// kind nearest to the first trader (ADR 0103). Returns the commands, or why there are none.
    /// </summary>
    /// <param name="context">The card's context.</param>
    /// <param name="far">The far market or harbor clicked.</param>
    /// <param name="goods">The goods: what is carried there (low byte) and back (next byte).</param>
    /// <param name="load">The load of each trip.</param>
    public (CommandList? Commands, string? Message) Trade(CardContext context, EntityId far, int goods, int load)
    {
        ArgumentNullException.ThrowIfNull(context);
        EntityId[] traders = Traders(context);
        Dictionary<uint, int> index = Index(context.Frame);
        if (traders.Length == 0 || !index.TryGetValue(traders[0].Value, out int first))
        {
            return (null, null);
        }

        FrameEntity t = context.Frame.Entities[first];
        DropOffAccess access = _rules.EntityTypes[t.TypeId].Traits.World.Trader!.Access;
        if (!index.TryGetValue(far.Value, out int fi) || (_rules.EntityTypes[context.Frame.Entities[fi].TypeId].Traits.World.TradeDock & access) == 0
            || context.Frame.Entities[fi].ConstructionPermille >= 0)
        {
            return (null, "Trade: that is not a market of this trader's kind.");
        }

        EntityId near = EntityId.None;
        long best = long.MaxValue;
        foreach (FrameEntity m in context.Frame.Entities)
        {
            if (m.Owner != _me || m.Id == far || m.ConstructionPermille >= 0 || (_rules.EntityTypes[m.TypeId].Traits.World.TradeDock & access) == 0)
            {
                continue;
            }

            long d = ((long)(m.X - t.X) * (m.X - t.X)) + ((long)(m.Y - t.Y) * (m.Y - t.Y));
            if (d < best)
            {
                best = d;
                near = m.Id;
            }
        }

        if (near.IsNone)
        {
            return (null, "Trade: no own market to load at.");
        }

        var list = new CommandList();
        list.Add(CommandKind.Trade, traders, context.Frame.Entities[fi].X, context.Frame.Entities[fi].Y, target: near, arg0: goods, arg1: load);
        return (list, null);
    }

    /// <summary>The kingdom's next idle peasant after the focused entity (the original's "next idle peasant"), or null.</summary>
    public EntityId? NextIdlePeasant(CardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ReadOnlySpan<FrameEntity> all = context.Frame.Entities;
        int start = Lead(context) is { } lead ? lead + 1 : 0;
        for (int k = 0; k < all.Length; k++)
        {
            FrameEntity e = all[(start + k) % all.Length];
            if (e.Owner == _me && e.Activity == UnitActivity.Idle && _rules.EntityTypes[e.TypeId].Traits.Harvester is not null)
            {
                return e.Id;
            }
        }

        return null;
    }

    /// <summary>What the client knows of a frame entity for selection and orders (<see cref="EntityInfo"/>).</summary>
    /// <param name="rules">The ruleset.</param>
    /// <param name="frame">The frame (for the kingdom's technologies).</param>
    /// <param name="e">The entity.</param>
    /// <param name="localPlayer">The local kingdom.</param>
    public static EntityInfo Info(Ruleset rules, GameFrame frame, in FrameEntity e, byte localPlayer)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(frame);
        EntityTypeDefinition def = rules.EntityTypes[e.TypeId];
        EntityTraits t = def.Traits;
        bool capturable = t.Capturable is { } capture
            && (capture.RequiredTechnology < 0 || (localPlayer < frame.Players.Length && frame.Players[localPlayer].HasTechnology(capture.RequiredTechnology)));
        return new EntityInfo(
            e.Id,
            e.TypeId,
            e.Owner,
            t.Category,
            t.Armament is not null && t.Harvester is null,
            (int)e.Activity,
            e.Health,
            e.MaxHealth,
            t.Harvester is not null,
            t.IsBuilder,
            t.Armament is not null,
            t.Producer is not null,
            t.ResourceNode is not null,
            e.ConstructionPermille >= 0,
            def.IsMobile,
            t.IsRepairable,
            t.IsCapturer,
            capturable,
            t.InventorySlots > 0,
            t.Item is not null,
            t.GainsExperience ? e.Level : -1,
            e.X,
            e.Y);
    }

    // An item of the open inventory (HUD-018): right click drops it, Alt + click makes it the default item, a click uses
    // it: an item whose spell needs a target waits for a click on the world (the mode's argument is slot + 1), unless
    // Ctrl makes it instant.
    private CardPress PressItem(CardContext context, int slot, bool right, bool alt, bool ctrl)
    {
        var list = new CommandList();
        if (right)
        {
            list.Add(CommandKind.DropItem, [_carrier], arg0: slot);
            return new CardPress(Commands: list);
        }

        if (alt)
        {
            list.Add(CommandKind.SetDefaultItem, [_carrier], arg0: slot);
            return new CardPress(Commands: list);
        }

        int spell = -1;
        foreach (FrameHeld h in context.Frame.Helds)
        {
            if (h.IsItem && h.Holder == _carrier.Value && h.Slot == slot)
            {
                spell = h.Spell;
            }
        }

        if (!ctrl && spell >= 0 && spell < _rules.Spells.Count && _rules.Spells[spell].Target != SpellTarget.Self)
        {
            return new CardPress(Mode: OrderMode.UseItem, ModeArgument: slot + 1);
        }

        list.Add(CommandKind.UseItem, [_carrier], target: _carrier, arg0: slot);
        return new CardPress(Commands: list);
    }

    /// <summary>Backspace: the selected own units use their default item at once (HK.TXT; GAME-10), or null.</summary>
    public CommandList? UseDefaultItem(CardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var use = new CommandList();
        foreach (EntityInfo user in Selected(context).Where(e => e.Owner == _me && e.IsMobile && e.CanPickItems))
        {
            use.Add(CommandKind.UseItem, [user.Id], target: user.Id, arg0: -1);
        }

        return use.Count > 0 ? use : null;
    }

    // ---- the frame ----------------------------------------------------------------------------------------------------

    private Dictionary<uint, int> Index(GameFrame frame)
    {
        if (!ReferenceEquals(frame, _indexed))
        {
            _index.Clear();
            ReadOnlySpan<FrameEntity> entities = frame.Entities;
            for (int i = 0; i < entities.Length; i++)
            {
                _index[entities[i].Id.Value] = i;
            }

            _indexed = frame;
        }

        return _index;
    }

    // The frame index of the focused entity of the selection.
    private int? Lead(CardContext context)
    {
        if (context.Selection.Count == 0)
        {
            return null;
        }

        EntityId id = context.Selection[Math.Clamp(context.Focus, 0, context.Selection.Count - 1)];
        return Index(context.Frame).TryGetValue(id.Value, out int i) ? i : null;
    }

    private List<EntityInfo> Selected(CardContext context)
    {
        var list = new List<EntityInfo>();
        Dictionary<uint, int> index = Index(context.Frame);
        foreach (EntityId id in context.Selection)
        {
            if (index.TryGetValue(id.Value, out int i))
            {
                list.Add(Info(_rules, context.Frame, context.Frame.Entities[i], _me));
            }
        }

        return list;
    }

    // The kingdom's heroes, spies (and ninjas: the units that detect the invisible) or carried items, each a button that
    // selects the unit (an item: its carrier). The original fills its empty HEROES, SPYS and ITEMS menus at run time.
    private List<CardButton> KingdomList(CardContext context, string kind)
    {
        var list = new List<CardButton>();
        Dictionary<uint, int> index = Index(context.Frame);
        if (kind == "items")
        {
            foreach (FrameHeld h in context.Frame.Helds)
            {
                if (h.IsItem && index.TryGetValue(h.Holder, out int c) && context.Frame.Entities[c].Owner == _me)
                {
                    ushort carrier = context.Frame.Entities[c].TypeId;
                    list.Add(new CardButton("item", '\0', CardAction.SelectUnit, h.Holder.ToString(CultureInfo.InvariantCulture), _source.TrainImage(carrier),
                        $"{_source.Name(h.TypeId)} ({_source.Name(carrier)})"));
                }
            }

            return list;
        }

        foreach (FrameEntity e in context.Frame.Entities)
        {
            bool listed = e.Owner == _me && !e.Remembered && (kind == "heroes" ? e.Hero : kind == "spies" && _rules.EntityTypes[e.TypeId].Traits.World.DetectUnits > 0);
            if (listed)
            {
                list.Add(new CardButton("unit", '\0', CardAction.SelectUnit, e.Id.Value.ToString(CultureInfo.InvariantCulture), _source.TrainImage(e.TypeId),
                    e.Level > 0 ? $"{_source.Name(e.TypeId)}, level {e.Level}" : _source.Name(e.TypeId)));
            }
        }

        return list;
    }

    private int InsideCount(CardContext context, EntityId holder)
        => Index(context.Frame).TryGetValue(holder.Value, out int i) ? context.Frame.Entities[i].Inside : 0;

    // The building (not a ship: they dock first) holding the unit picked in the selection view's list, or null.
    private EntityId? HolderOfPicked(CardContext context)
    {
        if (context.Picked.IsNone)
        {
            return null;
        }

        foreach (FrameHeld h in context.Frame.Helds)
        {
            if (h.Unit == context.Picked && !h.IsItem)
            {
                return Index(context.Frame).TryGetValue(h.Holder, out int i) && !Info(_rules, context.Frame, context.Frame.Entities[i], _me).IsMobile
                    ? EntityId.FromValue(h.Holder)
                    : null;
            }
        }

        return null;
    }

    private FramePlayer Me(CardContext context) => context.Frame.Players.Length > _me ? context.Frame.Players[_me] : default;

    private EntityId[] OwnIds(List<EntityInfo> selected, Func<EntityInfo, bool> which)
        => [.. selected.Where(e => e.Owner == _me && which(e)).Select(e => e.Id)];

    private EntityId[] Taverns(CardContext context)
        => OwnIds(Selected(context), e => !e.IsMobile && !e.UnderConstruction && _rules.EntityTypes[e.TypeId].Traits.World.IsTavern);

    private EntityId[] Traders(CardContext context)
        => OwnIds(Selected(context), e => e.IsMobile && _rules.EntityTypes[e.TypeId].Traits.World.Trader is not null);

    private static CardPress Commands(CommandKind kind, EntityId[] entities, int arg0 = 0, int arg1 = 0)
        => List(kind, entities, arg0: arg0, arg1: arg1) is { } list ? new CardPress(Commands: list) : CardPress.Nothing;

    private static CommandList? List(CommandKind kind, EntityId[] entities, int x = 0, int y = 0, int arg0 = 0, int arg1 = 0)
    {
        if (entities.Length == 0)
        {
            return null;
        }

        var list = new CommandList();
        list.Add(kind, entities, x, y, arg0: arg0, arg1: arg1);
        return list;
    }

    // "Keys: C Castle, Y Warriors' Guild..." for a category whose buttons' letters work from the menu above it.
    private string CapturedKeys(string submenu)
    {
        IEnumerable<string> keys = _source.Card(submenu).Where(b => b.Hotkey != '\0').Select(b => $"{b.Hotkey} {b.Tooltip}");
        string list = string.Join(", ", keys);
        return list.Length > 0 ? "Keys: " + list + "." : string.Empty;
    }

    // The spell of a card button: its key, or (the ninjas' cloak and murder, raise dead) its order's name.
    private int SpellOf(CardButton b) => _rules.SpellId(b.Argument) is >= 0 and var id ? id : _rules.SpellId(b.Order);

    private bool CanCast(EntityInfo e, int spell) => spell is >= 0 and <= ushort.MaxValue && _rules.EntityTypes[e.TypeId].Traits.CanCast((ushort)spell);

    // ---- the buttons --------------------------------------------------------------------------------------------------

    // How many of the button's item the selected own buildings have queued (ADR 0180).
    private int Queued(CardContext context, CardButton b, string civ)
    {
        (FrameQueueKind kind, int id) = b.Action switch
        {
            CardAction.Train => (FrameQueueKind.Unit, _source.TypeOfClass(civ, b.Argument)),
            CardAction.Research => (FrameQueueKind.Research, _rules.TechnologyId(b.Argument)),
            CardAction.Upgrade => (FrameQueueKind.Upgrade, _rules.UpgradeId(b.Argument)),
            _ => (FrameQueueKind.Unit, -1),
        };
        if (id < 0)
        {
            return 0;
        }

        int count = 0;
        foreach (FrameQueueEntry q in context.Frame.QueueEntries)
        {
            if (q.Kind == kind && q.Id == id && context.Selection.Contains(EntityId.FromValue(q.Entity)))
            {
                count += q.Count;
            }
        }

        return count;
    }

    // What the button's order costs and how long it takes (units: their base cost; research and upgrades: theirs; a
    // tavern offer: its price now).
    private (ImmutableArray<int> Cost, int TimeMs) Price(CardContext context, CardButton b, string civ)
    {
        switch (b.Action)
        {
            case CardAction.Train or CardAction.Build or CardAction.Become:
                int type = _source.TypeOfClass(civ, b.Argument);
                return type < 0 ? ([], 0) : (_rules.EntityTypes[type].Traits.Cost, _rules.EntityTypes[type].Traits.BuildTimeMs);
            case CardAction.Research:
                int tech = _rules.TechnologyId(b.Argument);
                return tech < 0 ? ([], 0) : (_rules.Technologies[tech].Cost, _rules.Technologies[tech].TimeMs);
            case CardAction.Upgrade:
                int up = _rules.UpgradeId(b.Argument);
                return up < 0 ? ([], 0) : (_rules.Upgrades[up].Cost, _rules.Upgrades[up].TimeMs);
            case CardAction.Hire when int.TryParse(b.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot):
                GameFrame f = context.Frame;
                for (int i = 0; i < f.TavernOfferCount; i++)
                {
                    if (f.TavernOffers[i].Slot == slot)
                    {
                        return (ImmutableArray.Create(f.TavernCosts, i * f.TavernCostStride, f.TavernCostStride), 0);
                    }
                }

                return ([], 0);
            default:
                return ([], 0);
        }
    }

    // The tooltip's price: the resource icons with the amounts (short ones red), then the hourglass with the time.
    private List<PriceItem>? PriceRows(CardContext context, ImmutableArray<int> cost, int timeMs)
    {
        List<PriceItem>? rows = null;
        FramePlayer me = Me(context);
        for (int r = 0; r < cost.Length && r < _rules.ResourceTypes.Count; r++)
        {
            if (cost[r] > 0)
            {
                (rows ??= []).Add(new PriceItem($"art/icons/res{_rules.ResourceTypes[r]}.t2img", cost[r].ToString(CultureInfo.InvariantCulture), r < 8 && me.Resources[r] < cost[r]));
            }
        }

        if (timeMs > 0)
        {
            (rows ??= []).Add(new PriceItem("art/icons/time.t2img", string.Create(CultureInfo.InvariantCulture, $"{(timeMs + 500) / 1000} s")));
        }

        return rows;
    }

    // The original's "Requires: ..." (STRINGS3.INI req3).
    private string Requires(string what) => $"{_source.Text("misc", "req3", "Requires")}: {what}";

    // The original's tavern menus are empty in MENU.INI: the game lists the period's offers there. True: the heroes' menu.
    private static bool? TavernMenu(string? menu) => menu switch
    {
        _ when string.Equals(menu, "tavern_merc", StringComparison.OrdinalIgnoreCase) => false,
        _ when string.Equals(menu, "tavern_hero", StringComparison.OrdinalIgnoreCase) => true,
        _ => null,
    };

    private List<CardButton> HireButtons(CardContext context, bool heroes)
    {
        var buttons = new List<CardButton>();
        foreach (FrameTavernOffer o in context.Frame.TavernOffers)
        {
            if (o.IsHero != heroes)
            {
                continue;
            }

            string name = _source.Name(o.TypeId);
            string what = o.IsHero ? $"Hire a hero: {name} (level {o.Level})" : $"Hire {o.Count} x {name} (level {o.Level})";
            char key = (char)('1' + buttons.Count);
            buttons.Add(new CardButton(o.IsHero ? "hire_hero" : "hire_merc", key, CardAction.Hire, o.Slot.ToString(CultureInfo.InvariantCulture), _source.TrainImage(o.TypeId), what));
        }

        return buttons;
    }

    private bool Active(CardContext context, CardButton b) => b.Action switch
    {
        CardAction.Move => context.Mode == OrderMode.Move,
        CardAction.Attack => context.Mode == OrderMode.Attack,
        CardAction.Harvest => context.Mode == OrderMode.Harvest,
        CardAction.Patrol => context.Mode == OrderMode.Patrol,
        CardAction.Guard => context.Mode == OrderMode.Guard,
        CardAction.Repair => context.Mode == OrderMode.Repair,
        CardAction.Capture => context.Mode == OrderMode.Capture,
        CardAction.MoveAdvance => context.Mode == OrderMode.MoveAdvance,
        CardAction.MoveSneak => context.Mode == OrderMode.MoveSneak,
        CardAction.AttackGround => context.Mode == OrderMode.AttackGround,
        CardAction.Spell => context.Mode == OrderMode.Spell && context.ModeArgument == SpellOf(b),
        CardAction.Bribe => context.Mode == OrderMode.Bribe,
        CardAction.Crusade => context.Mode == OrderMode.Crusade,
        CardAction.PickItem => context.Mode == OrderMode.PickItem,
        CardAction.UseItem => context.Mode == OrderMode.UseItem,
        CardAction.Unload => context.Mode == OrderMode.Unload,
        CardAction.Impersonate => context.Mode == OrderMode.Impersonate,
        CardAction.AiAssist => Me(context).AiAssist.ToString(CultureInfo.InvariantCulture) == b.Argument,
        CardAction.Stance => Lead(context) is { } l && b.Argument == context.Frame.Entities[l].Stance.ToString(CultureInfo.InvariantCulture),
        CardAction.Gate => Lead(context) is { } g && GateButtonOn(b.Argument, context.Frame.Entities[g].Gate),
        _ => false,
    };

    private static bool GateButtonOn(string argument, FrameGate gate) => argument switch
    {
        "open" => (gate & (FrameGate.Open | FrameGate.Automatic)) == FrameGate.Open,
        "close" => gate != FrameGate.None && (gate & (FrameGate.Open | FrameGate.Automatic)) == 0,
        "auto" => (gate & FrameGate.Automatic) != 0,
        _ => gate != FrameGate.None && (gate & FrameGate.Automatic) == 0,
    };

    // Researched technologies and upgrades the entity has (or does not take) leave the card, as in the original, and so
    // do orders a technology unlocks (TECHTREE [orders]: bribe, spells, loans...) until it is researched.
    private bool Hidden(CardContext context, CardButton b, in FrameEntity lead)
    {
        int gate = _rules.OrderTechnology(b.Order, lead.Id.IsNone ? string.Empty : _source.Civilization(lead.TypeId));
        if (gate >= 0 && !Me(context).HasTechnology(gate))
        {
            return true;
        }

        switch (b.Action)
        {
            case CardAction.Impersonate:
                return lead.SpyType >= 0; // a taken-over unit only reveals itself
            case CardAction.Reveal:
                return lead.SpyType < 0;
            case CardAction.Build or CardAction.Repair when lead.SpyType >= 0:
                return true; // 1.05: a spy in a peasant's body cannot build
            case CardAction.Submenu when lead.SpyType >= 0 && b.Argument.EndsWith("_build", StringComparison.Ordinal):
                return true;
            case CardAction.Research:
                int tech = _rules.TechnologyId(b.Argument);
                return tech >= 0 && Me(context).HasTechnology(tech);
            case CardAction.Upgrade:
                int up = _rules.UpgradeId(b.Argument);
                if (up < 0)
                {
                    return false;
                }

                UpgradeDefinition u = _rules.Upgrades[up];
                bool queued = false;
                foreach (FrameQueueEntry q in context.Frame.QueueEntries)
                {
                    queued |= q.Entity == lead.Id.Value && q.Kind == FrameQueueKind.Upgrade && q.Id == up;
                }

                return ((lead.Upgrades & (1u << up)) != 0 && !queued) || (u.IsConversion && u.ConvertedType(lead.TypeId) < 0 && !queued);
            default:
                return false;
        }
    }

    // Whether a card button works now and what its tooltip adds: why it is greyed out (missing building or technology,
    // too few resources), or a mana cost.
    private (bool Enabled, string Detail) Availability(CardContext context, CardButton b, string civ)
    {
        List<EntityInfo> selected = Selected(context);
        switch (b.Action)
        {
            case CardAction.Unsupported:
                return (false, "not in this build yet");
            case CardAction.Spell:
                int spellId = SpellOf(b);
                if (spellId < 0)
                {
                    return (false, "not in this build yet");
                }

                int manaCost = _rules.Spells[spellId].ManaCost;
                string mana = manaCost > 0 ? string.Create(CultureInfo.InvariantCulture, $"mana {manaCost}") : string.Empty;
                return selected.Any(e => e.Owner == _me && CanCast(e, spellId)) ? (true, mana) : (false, "needs a caster");
            case CardAction.Loan or CardAction.Crusade:
                return selected.Any(e => e.Owner == _me && !e.IsMobile && !e.UnderConstruction) ? (true, string.Empty) : (false, "needs the building");
            case CardAction.Bribe:
                return selected.Any(e => e.Owner == _me && e.CanBuild) ? (true, string.Empty) : (false, "needs a peasant");
            case CardAction.PickItem or CardAction.UseItem or CardAction.DropItem:
                return selected.Any(e => e.Owner == _me && e.IsMobile) ? (true, string.Empty) : (false, "needs a unit");
            case CardAction.Gamble or CardAction.Exchange:
                return selected.Any(e => e.Owner == _me && !e.UnderConstruction && _rules.EntityTypes[e.TypeId].Traits.World.IsTavern) ? (true, string.Empty) : (false, "needs a tavern");
            case CardAction.Hire:
                return HireAvailability(context, b);
            case CardAction.Trade:
                return selected.Any(e => e.Owner == _me && _rules.EntityTypes[e.TypeId].Traits.World.Trader is not null) ? (true, string.Empty) : (false, "needs a trader");
            case CardAction.Unload:
                return selected.Any(e => e.Owner == _me && InsideCount(context, e.Id) > 0) ? (true, string.Empty) : (false, "nobody inside");
            case CardAction.AiAssist:
                return context.AiAssist ? (true, string.Empty) : (false, "this match offers no AI assistance");
            case CardAction.KingdomList:
                return KingdomList(context, b.Argument).Count > 0 ? (true, string.Empty) : (false, "none yet");
            case CardAction.Craft:
                if (!selected.Any(e => e.Owner == _me && !e.UnderConstruction && _rules.EntityTypes[e.TypeId].Traits.Crafter is not null))
                {
                    return (false, "needs a magic workshop");
                }

                return Lead(context) is { } workshop && context.Frame.Entities[workshop].CraftPermille >= 0 ? (false, "making an item") : (true, string.Empty);
            case CardAction.Repair:
                return selected.Any(e => e.Owner == _me && e.CanBuild) ? (true, string.Empty) : (false, "needs a builder");
            case CardAction.Capture:
                return selected.Any(e => e.Owner == _me && _rules.EntityTypes[e.TypeId].Traits.IsCapturer) ? (true, string.Empty) : (false, "needs a peasant");
            case CardAction.Train or CardAction.Build or CardAction.Become:
                int type = _source.TypeOfClass(civ, b.Argument);
                if (type < 0)
                {
                    return (false, "not available to this kingdom");
                }

                if (MissingRequirement(context, type, civ) is { } missing)
                {
                    return (false, Requires(missing));
                }

                return Shortfall(context, _rules.EntityTypes[type].Traits.Cost) is { } shortfall ? (false, "not enough " + shortfall) : (true, string.Empty);
            case CardAction.Research:
                int tech = _rules.TechnologyId(b.Argument);
                if (tech < 0)
                {
                    return (false, "not in this ruleset");
                }

                TechnologyDefinition t = _rules.Technologies[tech];
                if (Me(context).IsResearching(tech))
                {
                    return (false, "being researched");
                }

                if (context.Frame.Researchable.Length > tech && context.Frame.Researchable[tech] == 0)
                {
                    return (false, Requires(MissingIn(context, t.Requirements, civ) ?? "requirements"));
                }

                return Shortfall(context, t.Cost) is { } lacking ? (false, "not enough " + lacking) : (true, string.Empty);
            case CardAction.Upgrade:
                return UpgradeAvailability(context, b, civ);
            case CardAction.Submenu when TavernMenu(b.Argument) is { } heroes:
                // The tavern's offers show (with their prices) once the technology is there, affordable or not.
                string offersTech = heroes ? "te_heroesquest" : "te_mercenaries";
                if (_rules.TechnologyId(offersTech) is >= 0 and var offersGate && !Me(context).HasTechnology(offersGate))
                {
                    return (false, heroes ? "needs Heroes' Quest" : "needs Mercenaries");
                }

                return HireButtons(context, heroes).Count > 0 ? (true, string.Empty) : (false, "no offer now");
            case CardAction.Submenu:
                foreach (CardButton child in _source.Card(b.Argument))
                {
                    if (child.Action is CardAction.Build or CardAction.Train or CardAction.Submenu && _source.TypeOfClass(civ, child.Argument) >= 0)
                    {
                        return (true, string.Empty);
                    }

                    // Any other order that works now (research, loans, hiring...) opens the menu too.
                    if (child.Action is not (CardAction.Build or CardAction.Train or CardAction.Back or CardAction.Unsupported) && Availability(context, child, civ).Enabled)
                    {
                        return (true, string.Empty);
                    }
                }

                return (false, "nothing to build there yet");
            default:
                return (true, string.Empty);
        }
    }

    private (bool Enabled, string Detail) HireAvailability(CardContext context, CardButton b)
    {
        GameFrame f = context.Frame;
        if (!int.TryParse(b.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
        {
            return (false, string.Empty);
        }

        for (int i = 0; i < f.TavernOfferCount; i++)
        {
            FrameTavernOffer o = f.TavernOffers[i];
            if (o.Slot != slot)
            {
                continue;
            }

            if (o.Taken)
            {
                return (false, "hired this period");
            }

            string hireTech = o.IsHero ? "te_heroesquest" : "te_mercenaries";
            if (_rules.TechnologyId(hireTech) is >= 0 and var hireGate && !Me(context).HasTechnology(hireGate))
            {
                return (false, o.IsHero ? "needs Heroes' Quest" : "needs Mercenaries");
            }

            ImmutableArray<int> price = ImmutableArray.Create(f.TavernCosts, i * f.TavernCostStride, f.TavernCostStride);
            return Shortfall(context, price) is { } shortOf ? (false, $"not enough {shortOf}") : (true, string.Empty);
        }

        return (false, "no offer now");
    }

    private (bool Enabled, string Detail) UpgradeAvailability(CardContext context, CardButton b, string civ)
    {
        int up = _rules.UpgradeId(b.Argument);
        if (up < 0 || Lead(context) is not { } lead)
        {
            return (false, "not in this ruleset");
        }

        UpgradeDefinition u = _rules.Upgrades[up];
        FrameEntity entity = context.Frame.Entities[lead];
        if (Queued(context, b, civ) > 0)
        {
            return (false, "being made");
        }

        foreach (ushort before in u.After)
        {
            if ((entity.Upgrades & (1u << before)) == 0)
            {
                return (false, Requires(_rules.Upgrades[before].Name));
            }
        }

        if (MissingIn(context, u.Requirements, _source.Civilization(entity.TypeId)) is { } prerequisite)
        {
            return (false, Requires(prerequisite));
        }

        return Shortfall(context, u.Cost) is { } low ? (false, "not enough " + low) : (true, string.Empty);
    }

    // The first resource the kingdom lacks, as "food (150 of 200)", or null when affordable.
    private string? Shortfall(CardContext context, ImmutableArray<int> cost)
    {
        if (context.Frame.Players.Length <= _me)
        {
            return "resources";
        }

        FramePlayer p = context.Frame.Players[_me];
        for (int r = 0; r < cost.Length && r < 8; r++)
        {
            if (p.Resources[r] < cost[r])
            {
                return $"{(r < _rules.ResourceTypes.Count ? _rules.ResourceTypes[r] : "resources")} ({p.Resources[r]} of {cost[r]})";
            }
        }

        return null;
    }

    // What the type still needs (a building or a technology), or null when its requirements are met. The sim's verdict
    // comes with the frame (GameFrame.Makeable); the names come from the type's requirement sets for the civilization.
    private string? MissingRequirement(CardContext context, int type, string civ)
    {
        GameFrame f = context.Frame;
        if (f.Makeable.Length > type && f.Makeable[type] != 0)
        {
            return null;
        }

        EntityTraits t = _rules.EntityTypes[type].Traits;
        if (MissingIn(context, t.Requirements, civ) is { } missing)
        {
            return missing;
        }

        foreach (ushort building in t.RequiredTypes)
        {
            if (!HasCompleted(context, building))
            {
                return _source.Name(building);
            }
        }

        // Without the sim's verdict (an old frame) the names above are all there is.
        return f.Makeable.Length > type ? "requirements" : null;
    }

    // The first unmet prerequisite of the sets for the civilization: a technology or a building, by name.
    private string? MissingIn(CardContext context, ImmutableArray<RequirementSet> sets, string civ)
    {
        FramePlayer me = Me(context);
        foreach (RequirementSet set in sets)
        {
            if (!set.AppliesTo(civ))
            {
                continue;
            }

            if (set.Unsatisfiable)
            {
                return "something this kingdom cannot have";
            }

            foreach (ushort tech in set.Technologies)
            {
                if (!me.HasTechnology(tech))
                {
                    return "the technology " + _rules.Technologies[tech].Name;
                }
            }

            foreach (ushort building in set.Types)
            {
                if (!HasCompleted(context, building))
                {
                    return _source.Name(building);
                }
            }
        }

        return null;
    }

    private bool HasCompleted(CardContext context, ushort type)
    {
        foreach (FrameEntity e in context.Frame.Entities)
        {
            if (e.Owner == _me && e.TypeId == type && e.ConstructionPermille < 0)
            {
                return true;
            }
        }

        return false;
    }
}
