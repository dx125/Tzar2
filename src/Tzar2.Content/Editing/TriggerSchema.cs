using System;
using System.Collections.Immutable;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;

namespace Tzar2.Content.Editing;

/// <summary>What kind of value a property-page field edits (Appendix C.4 shared property pages).</summary>
public enum FieldKind : byte
{
    /// <summary>One-line text.</summary>
    Text,

    /// <summary>Multi-line text (messages, quests, story screens).</summary>
    LongText,

    /// <summary>An integer.</summary>
    Integer,

    /// <summary>A duration in milliseconds.</summary>
    Milliseconds,

    /// <summary>A checkbox.</summary>
    Boolean,

    /// <summary>A legacy player mask (players 1–8).</summary>
    Players,

    /// <summary>One kingdom 1–8.</summary>
    Kingdom,

    /// <summary>An owner: 0 Nature or a kingdom 1–8.</summary>
    Owner,

    /// <summary>An object specification: named objects (All/Any), a type with players and a count, or a category.</summary>
    Objects,

    /// <summary>An optional object specification (typed holders).</summary>
    OptionalObjects,

    /// <summary>A single holder object (id, −1 = none).</summary>
    Holder,

    /// <summary>An area: a tile rectangle or an object.</summary>
    Area,

    /// <summary>An expression (Appendix C.6).</summary>
    Expression,

    /// <summary>A user image (optional).</summary>
    Image,

    /// <summary>A user sound (optional).</summary>
    Sound,

    /// <summary>User music (optional).</summary>
    Music,

    /// <summary>A next-map name (optional for Player Win / Player Lose).</summary>
    MapName,

    /// <summary>A conversation (index, −1 = none).</summary>
    Conversation,

    /// <summary>An Add Quest action (index, −1 = none).</summary>
    QuestAction,

    /// <summary>A Set Timer action (index, −1 = none).</summary>
    TimerAction,

    /// <summary>A diplomatic stance.</summary>
    Stance,

    /// <summary>An order key (<c>ORDERS.INI</c>).</summary>
    Order,

    /// <summary>An entity type key.</summary>
    TypeKey,

    /// <summary>The undecoded record of an unknown kind (read only).</summary>
    Raw,
}

/// <summary>One field of a property page.</summary>
/// <param name="Name">Label.</param>
/// <param name="Kind">Value kind.</param>
/// <param name="Get">Reads the value from a record.</param>
/// <param name="Set">Returns a copy of the record with the value.</param>
public sealed record TriggerField(string Name, FieldKind Kind, Func<object, object?> Get, Func<object, object?, object> Set);

/// <summary>An event or action kind with its property page.</summary>
/// <param name="Id">Stable id (the <c>kind</c> of <c>scenario.json</c>).</param>
/// <param name="Title">Display title (the original's list entry, in our words).</param>
/// <param name="RecordType">The IR record type.</param>
/// <param name="Fields">The property page.</param>
/// <param name="Create">A new record of this kind with the given name and default values.</param>
public sealed record TriggerKind(string Id, string Title, Type RecordType, ImmutableArray<TriggerField> Fields, Func<string, object> Create);

/// <summary>
/// The property pages of all 8 events and all 27 actions of Appendix C (plus Delay and the unknown kinds the converter
/// keeps), as data: the editor's trigger UI is generated from it, and a test checks that every IR record type has a page
/// and that every field reads and writes back.
/// </summary>
public static class TriggerSchema
{
    private static readonly ObjectSpec NoObjects = new NamedObjects(EquatableArray<int>.Empty, Quantifier.All);
    private static readonly AreaSpec NoArea = new RectArea(0, 0, 0, 0);

    /// <summary>The event kinds.</summary>
    public static ImmutableArray<TriggerKind> Events { get; } =
    [
        Kind<ObjectInAreaEvent>("objectInArea", "Object In Area", n => new ObjectInAreaEvent(n, NoObjects, NoArea), Objects<ObjectInAreaEvent>(e => e.Objects, (e, v) => e with { Objects = v }), Area<ObjectInAreaEvent>("Area", e => e.Area, (e, v) => e with { Area = v })),
        Kind<ObjectDestroyedEvent>("objectDestroyed", "Object Destroyed", n => new ObjectDestroyedEvent(n, NoObjects), Objects<ObjectDestroyedEvent>(e => e.Objects, (e, v) => e with { Objects = v })),
        Kind<ObjectsEnterHolderEvent>("enterHolder", "Objects Enter Holder", n => new ObjectsEnterHolderEvent(n, NoObjects, -1),
            Objects<ObjectsEnterHolderEvent>(e => e.Objects, (e, v) => e with { Objects = v }),
            F<ObjectsEnterHolderEvent>("Holder", FieldKind.Holder, e => e.HolderIndex, (e, v) => e with { HolderIndex = (int)v! }),
            F<ObjectsEnterHolderEvent>("Any holder of", FieldKind.OptionalObjects, e => e.Holders, (e, v) => e with { Holders = (ObjectSpec?)v })),
        Kind<ObjectLeaveHolderEvent>("leaveHolder", "Object Leave Holder", n => new ObjectLeaveHolderEvent(n, NoObjects, -1),
            Objects<ObjectLeaveHolderEvent>(e => e.Objects, (e, v) => e with { Objects = v }),
            F<ObjectLeaveHolderEvent>("Holder", FieldKind.Holder, e => e.HolderIndex, (e, v) => e with { HolderIndex = (int)v! }),
            F<ObjectLeaveHolderEvent>("Any holder of", FieldKind.OptionalObjects, e => e.Holders, (e, v) => e with { Holders = (ObjectSpec?)v })),
        Kind<ObjectExistEvent>("objectExist", "Object Exist", n => new ObjectExistEvent(n, NoObjects), Objects<ObjectExistEvent>(e => e.Objects, (e, v) => e with { Objects = v })),
        Kind<PlayerDefeatedEvent>("playerDefeated", "Player Defeated", n => new PlayerDefeatedEvent(n, PlayerMasks.Of(1)), Players<PlayerDefeatedEvent>(e => e.Players, (e, v) => e with { Players = v })),
        Kind<PlayerWonEvent>("playerWon", "Player Won", n => new PlayerWonEvent(n, PlayerMasks.Of(1)), Players<PlayerWonEvent>(e => e.Players, (e, v) => e with { Players = v })),
        Kind<ObjectExpressionEvent>("objectExpression", "Object Expression", n => new ObjectExpressionEvent(n, NoObjects, ExpressionDef.Empty),
            Objects<ObjectExpressionEvent>(e => e.Objects, (e, v) => e with { Objects = v }),
            Expr<ObjectExpressionEvent>("Expression", e => e.Expression, (e, v) => e with { Expression = v })),
        Kind<UnknownEvent>("unknown", "Unknown (legacy)", n => new UnknownEvent(n, -1, EquatableArray<byte>.Empty), F<UnknownEvent>("Legacy record", FieldKind.Raw, e => e.LegacyCode, (e, _) => e)),
    ];

    /// <summary>The action kinds.</summary>
    public static ImmutableArray<TriggerKind> Actions { get; } =
    [
        Kind<AddQuestAction>("addQuest", "Add Quest", n => new AddQuestAction(n, string.Empty, null),
            F<AddQuestAction>("Text", FieldKind.LongText, a => a.Text, (a, v) => a with { Text = (string)v! }),
            Res<AddQuestAction>("Image", FieldKind.Image, a => a.Image, (a, v) => a with { Image = v })),
        Kind<RemoveQuestAction>("removeQuest", "Remove Quest", n => new RemoveQuestAction(n, -1), F<RemoveQuestAction>("Quest", FieldKind.QuestAction, a => a.AddQuestActionIndex, (a, v) => a with { AddQuestActionIndex = (int)v! })),
        Kind<AppearObjectAction>("appearObject", "Appear Object", n => new AppearObjectAction(n, NoObjects), Objects<AppearObjectAction>(a => a.Objects, (a, v) => a with { Objects = v })),
        Kind<ChangeDiplomacyAction>("changeDiplomacy", "Change Diplomacy Settings", n => new ChangeDiplomacyAction(n, 1, 2, Stance.Enemy, false, false, false),
            F<ChangeDiplomacyAction>("Kingdom", FieldKind.Kingdom, a => a.Kingdom1, (a, v) => a with { Kingdom1 = (int)v! }),
            F<ChangeDiplomacyAction>("Other kingdom", FieldKind.Kingdom, a => a.Kingdom2, (a, v) => a with { Kingdom2 = (int)v! }),
            F<ChangeDiplomacyAction>("Stance", FieldKind.Stance, a => a.Stance, (a, v) => a with { Stance = (Stance)v! }),
            Bool<ChangeDiplomacyAction>("Shared vision", a => a.SharedVision, (a, v) => a with { SharedVision = v }),
            Bool<ChangeDiplomacyAction>("Shared resources", a => a.SharedResources, (a, v) => a with { SharedResources = v }),
            Bool<ChangeDiplomacyAction>("Command units", a => a.CommandUnits, (a, v) => a with { CommandUnits = v })),
        Kind<ChangeObjectOwnerAction>("changeOwner", "Change Object Owner", n => new ChangeObjectOwnerAction(n, NoObjects, 1),
            Objects<ChangeObjectOwnerAction>(a => a.Objects, (a, v) => a with { Objects = v }),
            F<ChangeObjectOwnerAction>("New owner", FieldKind.Kingdom, a => a.Kingdom, (a, v) => a with { Kingdom = (int)v! })),
        Kind<ChangePlayerStatusAction>("changePlayerStatus", "Change Player Status", n => new ChangePlayerStatusAction(n, 1, false, false),
            F<ChangePlayerStatusAction>("Kingdom", FieldKind.Kingdom, a => a.Kingdom, (a, v) => a with { Kingdom = (int)v! }),
            Bool<ChangePlayerStatusAction>("Cannot win", a => a.CannotWin, (a, v) => a with { CannotWin = v }),
            Bool<ChangePlayerStatusAction>("Cannot lose", a => a.CannotLose, (a, v) => a with { CannotLose = v })),
        Kind<ConversationAction>("conversation", "Conversation", n => new ConversationAction(n, -1), F<ConversationAction>("Conversation", FieldKind.Conversation, a => a.ConversationIndex, (a, v) => a with { ConversationIndex = (int)v! })),
        Kind<DestroyObjectAction>("destroyObject", "Destroy Object", n => new DestroyObjectAction(n, NoObjects), Objects<DestroyObjectAction>(a => a.Objects, (a, v) => a with { Objects = v })),
        Kind<EraseObjectAction>("eraseObject", "Erase Object", n => new EraseObjectAction(n, NoObjects), Objects<EraseObjectAction>(a => a.Objects, (a, v) => a with { Objects = v })),
        Kind<ExploreAreaAction>("exploreArea", "Explore Area", n => new ExploreAreaAction(n, 10_000, false, NoArea),
            Ms<ExploreAreaAction>("Time", a => a.TimeMs, (a, v) => a with { TimeMs = v }),
            Bool<ExploreAreaAction>("Reveal fog", a => a.RevealFog, (a, v) => a with { RevealFog = v }),
            Area<ExploreAreaAction>("Area", a => a.Area, (a, v) => a with { Area = v })),
        Kind<ExpressionAction>("expression", "Expression", n => new ExpressionAction(n, ExpressionDef.Empty), Expr<ExpressionAction>("Expression", a => a.Expression, (a, v) => a with { Expression = v })),
        Kind<KeepRuleAction>("keepRule", "Keep Rule", n => new KeepRuleAction(n)),
        Kind<LoadMapAction>("loadMap", "Load Map", n => new LoadMapAction(n, string.Empty), F<LoadMapAction>("Map", FieldKind.MapName, a => a.Map, (a, v) => a with { Map = (string?)v ?? string.Empty })),
        Kind<LockViewAction>("lockView", "Lock View", n => new LockViewAction(n, 5000, NoArea),
            Ms<LockViewAction>("Time", a => a.TimeMs, (a, v) => a with { TimeMs = v }),
            Area<LockViewAction>("Area", a => a.Area, (a, v) => a with { Area = v })),
        Kind<LockUnitsAction>("lockUnits", "Lock/Unlock Units", n => new LockUnitsAction(n, NoObjects, true),
            Objects<LockUnitsAction>(a => a.Objects, (a, v) => a with { Objects = v }),
            Bool<LockUnitsAction>("Lock (off = unlock)", a => a.Locked, (a, v) => a with { Locked = v })),
        Kind<ObjectOrderAction>("objectOrder", "Object Order", n => new ObjectOrderAction(n, NoObjects, NoArea, "move"),
            Objects<ObjectOrderAction>(a => a.Objects, (a, v) => a with { Objects = v }),
            Area<ObjectOrderAction>("Area", a => a.Area, (a, v) => a with { Area = v }),
            F<ObjectOrderAction>("Order", FieldKind.Order, a => a.OrderKey, (a, v) => a with { OrderKey = (string)v! })),
        Kind<PlayerLoseAction>("playerLose", "Player Lose", n => new PlayerLoseAction(n, PlayerMasks.Of(1), null),
            Players<PlayerLoseAction>(a => a.Players, (a, v) => a with { Players = v }),
            F<PlayerLoseAction>("Next map", FieldKind.MapName, a => a.Map, (a, v) => a with { Map = Empty((string?)v) })),
        Kind<PlayerWinAction>("playerWin", "Player Win", n => new PlayerWinAction(n, PlayerMasks.Of(1), null),
            Players<PlayerWinAction>(a => a.Players, (a, v) => a with { Players = v }),
            F<PlayerWinAction>("Next map", FieldKind.MapName, a => a.Map, (a, v) => a with { Map = Empty((string?)v) })),
        Kind<ScrollViewAction>("scrollView", "Scroll View", n => new ScrollViewAction(n, NoArea, NoArea),
            Area<ScrollViewAction>("From", a => a.From, (a, v) => a with { From = v }),
            Area<ScrollViewAction>("To", a => a.To, (a, v) => a with { To = v })),
        Kind<SendChatAction>("sendChat", "Send Chat Message", n => new SendChatAction(n, PlayerMasks.AllPlayers, string.Empty),
            Players<SendChatAction>(a => a.Players, (a, v) => a with { Players = v }),
            F<SendChatAction>("Message", FieldKind.Text, a => a.Text, (a, v) => a with { Text = (string)v! })),
        Kind<SetObjectPropertiesAction>("setObjectProperties", "Set Object Properties", n => new SetObjectPropertiesAction(n, NoObjects, ExpressionDef.Empty),
            Objects<SetObjectPropertiesAction>(a => a.Objects, (a, v) => a with { Objects = v }),
            Expr<SetObjectPropertiesAction>("Expression", a => a.Expression, (a, v) => a with { Expression = v })),
        Kind<SetTimerAction>("setTimer", "Set Timer", n => new SetTimerAction(n, ExpressionDef.Empty, false),
            Expr<SetTimerAction>("Initial value (ms)", a => a.Initial, (a, v) => a with { Initial = v }),
            Bool<SetTimerAction>("Count down", a => a.CountDown, (a, v) => a with { CountDown = v })),
        Kind<ShowMessageAction>("showMessage", "Show Message", n => new ShowMessageAction(n, string.Empty, string.Empty, null),
            F<ShowMessageAction>("Title", FieldKind.Text, a => a.Title, (a, v) => a with { Title = (string)v! }),
            F<ShowMessageAction>("Text", FieldKind.LongText, a => a.Text, (a, v) => a with { Text = (string)v! }),
            Res<ShowMessageAction>("Image", FieldKind.Image, a => a.Image, (a, v) => a with { Image = v })),
        Kind<ShowStoryScreenAction>("showStoryScreen", "Show Story Screen", n => new ShowStoryScreenAction(n, 20_000, true, string.Empty, null, null, null),
            Ms<ShowStoryScreenAction>("Scroll time", a => a.ScrollTimeMs, (a, v) => a with { ScrollTimeMs = v }),
            Bool<ShowStoryScreenAction>("Auto close", a => a.AutoClose, (a, v) => a with { AutoClose = v }),
            F<ShowStoryScreenAction>("Text", FieldKind.LongText, a => a.Text, (a, v) => a with { Text = (string)v! }),
            Res<ShowStoryScreenAction>("Image", FieldKind.Image, a => a.Image, (a, v) => a with { Image = v }),
            Res<ShowStoryScreenAction>("Sound", FieldKind.Sound, a => a.Sound, (a, v) => a with { Sound = v }),
            Res<ShowStoryScreenAction>("Music", FieldKind.Music, a => a.Music, (a, v) => a with { Music = v })),
        Kind<SpawnObjectAction>("spawnObject", "Spawn Object", n => new SpawnObjectAction(n, string.Empty, 1, 1, NoArea),
            F<SpawnObjectAction>("Type", FieldKind.TypeKey, a => a.TypeKey, (a, v) => a with { TypeKey = (string)v! }),
            F<SpawnObjectAction>("Owner", FieldKind.Owner, a => a.Owner, (a, v) => a with { Owner = (int)v! }),
            F<SpawnObjectAction>("Count", FieldKind.Integer, a => a.Count, (a, v) => a with { Count = (int)v! }),
            Area<SpawnObjectAction>("Area", a => a.Area, (a, v) => a with { Area = v })),
        Kind<StopTimerAction>("stopTimer", "Stop Timer", n => new StopTimerAction(n, -1), F<StopTimerAction>("Timer", FieldKind.TimerAction, a => a.SetTimerActionIndex, (a, v) => a with { SetTimerActionIndex = (int)v! })),
        Kind<UnlockViewAction>("unlockView", "Unlock View", n => new UnlockViewAction(n)),
        Kind<DelayAction>("delay", "Delay", n => new DelayAction(n, 1000), Ms<DelayAction>("Time", a => a.TimeMs, (a, v) => a with { TimeMs = v })),
        Kind<UnknownAction>("unknown", "Unknown (legacy)", n => new UnknownAction(n, -1, EquatableArray<byte>.Empty), F<UnknownAction>("Legacy record", FieldKind.Raw, a => a.LegacyCode, (a, _) => a)),
    ];

    /// <summary>The page of an event.</summary>
    public static TriggerKind Of(EventDef e) => Find(Events, e);

    /// <summary>The page of an action.</summary>
    public static TriggerKind Of(ActionDef a) => Find(Actions, a);

    /// <summary>Converts a record to another kind, keeping its name (the kind list of a property page).</summary>
    public static T Retype<T>(TriggerKind kind, string name)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(kind);
        return (T)kind.Create(name);
    }

    private static TriggerKind Find(ImmutableArray<TriggerKind> kinds, object record)
    {
        ArgumentNullException.ThrowIfNull(record);
        foreach (TriggerKind k in kinds)
        {
            if (k.RecordType == record.GetType())
            {
                return k;
            }
        }

        throw new ArgumentException($"No property page for {record.GetType().Name}.", nameof(record));
    }

    private static string? Empty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static TriggerKind Kind<T>(string id, string title, Func<string, T> create, params TriggerField[] fields)
        where T : class
        => new(id, title, typeof(T), [.. fields], name => create(name));

    private static TriggerField F<T>(string name, FieldKind kind, Func<T, object?> get, Func<T, object?, T> set)
        => new(name, kind, o => get((T)o), (o, v) => set((T)o, v)!);

    private static TriggerField Bool<T>(string name, Func<T, bool> get, Func<T, bool, T> set)
        => F<T>(name, FieldKind.Boolean, o => get(o), (o, v) => set(o, (bool)v!));

    private static TriggerField Ms<T>(string name, Func<T, int> get, Func<T, int, T> set)
        => F<T>(name, FieldKind.Milliseconds, o => get(o), (o, v) => set(o, (int)v!));

    private static TriggerField Players<T>(Func<T, ushort> get, Func<T, ushort, T> set)
        => F<T>("Players", FieldKind.Players, o => get(o), (o, v) => set(o, (ushort)v!));

    private static TriggerField Objects<T>(Func<T, ObjectSpec> get, Func<T, ObjectSpec, T> set)
        => F<T>("Objects", FieldKind.Objects, o => get(o), (o, v) => set(o, (ObjectSpec)v!));

    private static TriggerField Area<T>(string name, Func<T, AreaSpec> get, Func<T, AreaSpec, T> set)
        => F<T>(name, FieldKind.Area, o => get(o), (o, v) => set(o, (AreaSpec)v!));

    private static TriggerField Expr<T>(string name, Func<T, ExpressionDef> get, Func<T, ExpressionDef, T> set)
        => F<T>(name, FieldKind.Expression, o => get(o), (o, v) => set(o, v as ExpressionDef ?? ScenarioModel.Source((string?)v ?? string.Empty)));

    private static TriggerField Res<T>(string name, FieldKind kind, Func<T, ResourceRef?> get, Func<T, ResourceRef?, T> set)
        => F<T>(name, kind, o => get(o), (o, v) => set(o, v as ResourceRef ?? (v is string { Length: > 0 } path ? new ResourceRef(path) : null)));
}
