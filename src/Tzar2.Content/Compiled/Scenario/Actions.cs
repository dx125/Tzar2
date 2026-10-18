using Tzar2.Content.Compiled.Scenario.Expressions;

namespace Tzar2.Content.Compiled.Scenario;

/// <summary>
/// A named action (Appendix C.7). Actions are defined once and referenced by rules (<see cref="RuleDef.ActionIndices"/>);
/// Add Quest and Set Timer actions also name the quest and the timer. Kingdoms are legacy 1–8, player masks have
/// bit 0 = player 1, times are milliseconds, areas tiles.
/// </summary>
/// <param name="Name">The action's name.</param>
public abstract record ActionDef(string Name);

/// <summary>Diplomatic stance between two kingdoms.</summary>
public enum Stance : byte
{
    /// <summary>Allied.</summary>
    Ally,

    /// <summary>Enemies.</summary>
    Enemy,

    /// <summary>Neutral.</summary>
    Neutral,
}

/// <summary>Adds a quest named after the action to the quest list (F8).</summary>
/// <param name="Name">Action (and quest) name.</param>
/// <param name="Text">Quest text (UTF-8).</param>
/// <param name="Image">Optional image.</param>
public sealed record AddQuestAction(string Name, string Text, ResourceRef? Image) : ActionDef(Name);

/// <summary>Removes a quest.</summary>
/// <param name="Name">Action name.</param>
/// <param name="AddQuestActionIndex">Index of the <see cref="AddQuestAction"/> that created it, or −1.</param>
public sealed record RemoveQuestAction(string Name, int AddQuestActionIndex) : ActionDef(Name);

/// <summary>Makes objects flagged Not Appear appear.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Objects">Which objects.</param>
public sealed record AppearObjectAction(string Name, ObjectSpec Objects) : ActionDef(Name);

/// <summary>Changes the diplomacy between two kingdoms.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Kingdom1">First kingdom (1–8).</param>
/// <param name="Kingdom2">Second kingdom (1–8).</param>
/// <param name="Stance">New stance.</param>
/// <param name="SharedVision">Allies share vision.</param>
/// <param name="SharedResources">Allies share resources.</param>
/// <param name="CommandUnits">Allies may command each other's units.</param>
public sealed record ChangeDiplomacyAction(string Name, int Kingdom1, int Kingdom2, Stance Stance, bool SharedVision, bool SharedResources, bool CommandUnits) : ActionDef(Name);

/// <summary>Transfers objects to a kingdom.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Objects">Which objects.</param>
/// <param name="Kingdom">New owner (1–8).</param>
public sealed record ChangeObjectOwnerAction(string Name, ObjectSpec Objects, int Kingdom) : ActionDef(Name);

/// <summary>Locks a kingdom's win or lose state.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Kingdom">Kingdom (1–8).</param>
/// <param name="CannotWin">The kingdom cannot win.</param>
/// <param name="CannotLose">The kingdom cannot lose.</param>
public sealed record ChangePlayerStatusAction(string Name, int Kingdom, bool CannotWin, bool CannotLose) : ActionDef(Name);

/// <summary>Starts a conversation (single player pauses).</summary>
/// <param name="Name">Action name.</param>
/// <param name="ConversationIndex">Index into <see cref="ScenarioData.Conversations"/>, or −1.</param>
public sealed record ConversationAction(string Name, int ConversationIndex) : ActionDef(Name);

/// <summary>Kills or destroys objects with their death animation.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Objects">Which objects.</param>
public sealed record DestroyObjectAction(string Name, ObjectSpec Objects) : ActionDef(Name);

/// <summary>Removes objects at once, without animation.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Objects">Which objects.</param>
public sealed record EraseObjectAction(string Name, ObjectSpec Objects) : ActionDef(Name);

/// <summary>Explores an area for a while.</summary>
/// <param name="Name">Action name.</param>
/// <param name="TimeMs">Duration.</param>
/// <param name="RevealFog">Also removes the fog.</param>
/// <param name="Area">Where.</param>
public sealed record ExploreAreaAction(string Name, int TimeMs, bool RevealFog, AreaSpec Area) : ActionDef(Name);

/// <summary>Evaluates an expression (assignments).</summary>
/// <param name="Name">Action name.</param>
/// <param name="Expression">The expression.</param>
public sealed record ExpressionAction(string Name, ExpressionDef Expression) : ActionDef(Name);

/// <summary>Keeps the rule that runs it active for the whole game.</summary>
/// <param name="Name">Action name.</param>
public sealed record KeepRuleAction(string Name) : ActionDef(Name);

/// <summary>Loads the next campaign map.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Map">The map name as written (matched case-insensitively against <see cref="CampaignMap.MapName"/>).</param>
public sealed record LoadMapAction(string Name, string Map) : ActionDef(Name);

/// <summary>Freezes the camera on an area for a while.</summary>
/// <param name="Name">Action name.</param>
/// <param name="TimeMs">Duration.</param>
/// <param name="Area">Where.</param>
public sealed record LockViewAction(string Name, int TimeMs, AreaSpec Area) : ActionDef(Name);

/// <summary>Locks or unlocks units against player commands.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Objects">Which objects.</param>
/// <param name="Locked">True to lock, false to unlock.</param>
public sealed record LockUnitsAction(string Name, ObjectSpec Objects, bool Locked) : ActionDef(Name);

/// <summary>Issues an order to objects, executed in an area.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Objects">Which objects.</param>
/// <param name="Area">Where.</param>
/// <param name="OrderKey">The <c>ORDERS.INI</c> key (e.g. <c>move</c>), or <c>#&lt;code&gt;</c> when the code is unknown.</param>
public sealed record ObjectOrderAction(string Name, ObjectSpec Objects, AreaSpec Area, string OrderKey) : ActionDef(Name);

/// <summary>Forces players to lose, then loads the next map.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Players">Legacy player mask.</param>
/// <param name="Map">Next map name, or null.</param>
public sealed record PlayerLoseAction(string Name, ushort Players, string? Map) : ActionDef(Name);

/// <summary>Forces players to win, then loads the next map.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Players">Legacy player mask.</param>
/// <param name="Map">Next map name, or null.</param>
public sealed record PlayerWinAction(string Name, ushort Players, string? Map) : ActionDef(Name);

/// <summary>Moves the camera from one area's centre to another's.</summary>
/// <param name="Name">Action name.</param>
/// <param name="From">Start area.</param>
/// <param name="To">End area.</param>
public sealed record ScrollViewAction(string Name, AreaSpec From, AreaSpec To) : ActionDef(Name);

/// <summary>Sends a chat message to players.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Players">Legacy player mask.</param>
/// <param name="Text">Message (UTF-8).</param>
public sealed record SendChatAction(string Name, ushort Players, string Text) : ActionDef(Name);

/// <summary>Sets object properties through an expression over the reserved variables.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Objects">Which objects.</param>
/// <param name="Expression">The assignments.</param>
public sealed record SetObjectPropertiesAction(string Name, ObjectSpec Objects, ExpressionDef Expression) : ActionDef(Name);

/// <summary>Starts a millisecond timer variable named after the action.</summary>
/// <param name="Name">Action (and timer) name.</param>
/// <param name="Initial">Initial value.</param>
/// <param name="CountDown">Counts down instead of up.</param>
public sealed record SetTimerAction(string Name, ExpressionDef Initial, bool CountDown) : ActionDef(Name);

/// <summary>Shows a pop-up message.</summary>
/// <param name="Name">Action name.</param>
/// <param name="Title">Title (UTF-8).</param>
/// <param name="Text">Text (UTF-8).</param>
/// <param name="Image">Optional image.</param>
public sealed record ShowMessageAction(string Name, string Title, string Text, ResourceRef? Image) : ActionDef(Name);

/// <summary>Shows a story screen with scrolling text.</summary>
/// <param name="Name">Action name.</param>
/// <param name="ScrollTimeMs">Scroll time.</param>
/// <param name="AutoClose">Closes by itself at the end.</param>
/// <param name="Text">Text (UTF-8).</param>
/// <param name="Image">Optional image.</param>
/// <param name="Sound">Optional sound.</param>
/// <param name="Music">Optional music.</param>
public sealed record ShowStoryScreenAction(string Name, int ScrollTimeMs, bool AutoClose, string Text, ResourceRef? Image, ResourceRef? Sound, ResourceRef? Music) : ActionDef(Name);

/// <summary>Creates objects.</summary>
/// <param name="Name">Action name.</param>
/// <param name="TypeKey">Entity type key (or the legacy name when unmapped).</param>
/// <param name="Owner">Owner kingdom 1–8, or 0 for Nature (⚠ UNVERIFIED encoding, UNK-04).</param>
/// <param name="Count">How many.</param>
/// <param name="Area">Where.</param>
public sealed record SpawnObjectAction(string Name, string TypeKey, int Owner, int Count, AreaSpec Area) : ActionDef(Name);

/// <summary>Stops a timer.</summary>
/// <param name="Name">Action name.</param>
/// <param name="SetTimerActionIndex">Index of the <see cref="SetTimerAction"/>, or −1.</param>
public sealed record StopTimerAction(string Name, int SetTimerActionIndex) : ActionDef(Name);

/// <summary>Releases Lock View.</summary>
/// <param name="Name">Action name.</param>
public sealed record UnlockViewAction(string Name) : ActionDef(Name);

/// <summary>
/// The legacy "Delay" action (code 27; not in the manual). ⚠ UNVERIFIED (UNK-76): presumably delays the remaining
/// actions of the rule by <see cref="TimeMs"/>.
/// </summary>
/// <param name="Name">Action name.</param>
/// <param name="TimeMs">Delay.</param>
public sealed record DelayAction(string Name, int TimeMs) : ActionDef(Name);

/// <summary>An action kind the converter does not understand; kept so nothing is lost silently. Does nothing.</summary>
/// <param name="Name">Action name.</param>
/// <param name="LegacyCode">The legacy type code.</param>
/// <param name="Raw">The undecoded record.</param>
public sealed record UnknownAction(string Name, int LegacyCode, EquatableArray<byte> Raw) : ActionDef(Name);
