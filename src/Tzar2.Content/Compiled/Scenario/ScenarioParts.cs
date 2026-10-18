using Tzar2.Content.Compiled.Scenario.Expressions;

namespace Tzar2.Content.Compiled.Scenario;

/// <summary>
/// A rule (Appendix C.3): when <see cref="Condition"/> is non-zero the actions run top to bottom, then the rule is
/// disabled unless one of them is a <see cref="KeepRuleAction"/>.
/// </summary>
/// <param name="Name">Rule name.</param>
/// <param name="Condition">The condition (an empty condition is ⚠ UNVERIFIED; treated as false by convention).</param>
/// <param name="ActionIndices">Indices into <see cref="ScenarioData.Actions"/>, in execution order.</param>
public sealed record RuleDef(string Name, ExpressionDef Condition, EquatableArray<int> ActionIndices);

/// <summary>A conversation: a graph of phrases (Appendix C.8).</summary>
/// <param name="Name">Conversation name.</param>
/// <param name="Phrases">The phrases; follow-ups refer to indices of this list.</param>
public sealed record ConversationDef(string Name, EquatableArray<PhraseDef> Phrases);

/// <summary>One phrase of a conversation.</summary>
/// <param name="Name">Phrase name.</param>
/// <param name="Initial">Offered when the conversation starts (≤ 6 per conversation in the original).</param>
/// <param name="SourceObject">The speaker (index into <see cref="MapData.Objects"/>), or null.</param>
/// <param name="Condition">Offered only if true; empty = always.</param>
/// <param name="Result">Executed when the phrase is said; empty = nothing.</param>
/// <param name="Text">The text (UTF-8, ≤ 1,000 characters in the original).</param>
/// <param name="Icon">Optional custom portrait.</param>
/// <param name="Sound">Optional sound.</param>
/// <param name="FollowUps">Indices of the follow-up phrases (≤ 6 in the original).</param>
public sealed record PhraseDef(string Name, bool Initial, int? SourceObject, ExpressionDef Condition, ExpressionDef Result, string Text, ResourceRef? Icon, ResourceRef? Sound, EquatableArray<int> FollowUps);

/// <summary>Repeat mode of a custom sound (Appendix C.1).</summary>
public enum SoundRepeat : byte
{
    /// <summary>Every <see cref="CustomSoundDef.IntervalMs"/>.</summary>
    Every,

    /// <summary>Looped.</summary>
    Looped,

    /// <summary>At random intervals between <see cref="CustomSoundDef.IntervalMs"/> and <see cref="CustomSoundDef.IntervalMaxMs"/>.</summary>
    Random,

    /// <summary>Once, after <see cref="CustomSoundDef.IntervalMs"/>.</summary>
    Delayed,
}

/// <summary>A custom ambient sound.</summary>
/// <param name="Name">Name.</param>
/// <param name="Sound">The sound.</param>
/// <param name="Repeat">Repeat mode.</param>
/// <param name="IntervalMs">Interval, minimum interval or start delay.</param>
/// <param name="IntervalMaxMs">Maximum interval (Random only).</param>
/// <param name="SourceObject">The object it moves with, or null for a point source.</param>
/// <param name="X">Point source tile column (when <paramref name="SourceObject"/> is null).</param>
/// <param name="Y">Point source tile row.</param>
public sealed record CustomSoundDef(string Name, ResourceRef Sound, SoundRepeat Repeat, int IntervalMs, int IntervalMaxMs, int? SourceObject, int X, int Y);

/// <summary>
/// Scenario properties of a placed object (Appendix C.2), parallel to <see cref="MapData.Objects"/>. A null skill is
/// "use default".
/// </summary>
/// <param name="Name">The object's name (triggers, persistence), or empty.</param>
/// <param name="Title">Display title, or empty.</param>
/// <param name="Flags">Not Appear / Persistent / No AI.</param>
/// <param name="Health">Health override.</param>
/// <param name="MaxHealth">Max health override.</param>
/// <param name="Attack">Attack override.</param>
/// <param name="Defence">Defence override.</param>
/// <param name="Mana">Mana override.</param>
/// <param name="Level">Level override.</param>
/// <param name="Resistance">Building resistance override.</param>
/// <param name="Icon">Custom 44×44 icon.</param>
/// <param name="BigIcon">Custom 96×96 icon.</param>
/// <param name="Item">Item properties of an item object, or null.</param>
public sealed record ObjectInfo(
    string Name,
    string Title,
    ObjectFlags Flags,
    int? Health = null,
    int? MaxHealth = null,
    int? Attack = null,
    int? Defence = null,
    int? Mana = null,
    int? Level = null,
    int? Resistance = null,
    ResourceRef? Icon = null,
    ResourceRef? BigIcon = null,
    ItemInfo? Item = null)
{
    /// <summary>An object without scenario properties.</summary>
    public static ObjectInfo Plain { get; } = new(string.Empty, string.Empty, ObjectFlags.None);
}

/// <summary>Item properties (Appendix C.2 "Items"); a null field is "not set / default".</summary>
/// <param name="Definition">Item definition name (the legacy item name).</param>
/// <param name="Value">Bonus value (weapon attack, armor defence, potion amount; meaning by item class).</param>
/// <param name="Amount">Resource amount of a resource item.</param>
/// <param name="MagicType">Spell book magic type.</param>
/// <param name="NeedsMana">Spell book needs mana.</param>
/// <param name="CooldownSeconds">Spell book time restriction.</param>
/// <param name="Charges">Spell book max charges.</param>
/// <param name="Text">Scroll text (UTF-8).</param>
/// <param name="Image">Scroll image.</param>
/// <param name="Sound">Scroll sound.</param>
/// <param name="Music">Scroll music.</param>
/// <param name="ScrollTimeMs">Scroll time.</param>
/// <param name="AutoClose">Scroll closes by itself.</param>
public sealed record ItemInfo(
    string Definition,
    int? Value = null,
    int? Amount = null,
    int? MagicType = null,
    bool? NeedsMana = null,
    int? CooldownSeconds = null,
    int? Charges = null,
    string? Text = null,
    ResourceRef? Image = null,
    ResourceRef? Sound = null,
    ResourceRef? Music = null,
    int? ScrollTimeMs = null,
    bool? AutoClose = null);

/// <summary>Object flags (Appendix C.2).</summary>
[System.Flags]
public enum ObjectFlags : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Hidden until an Appear Object action.</summary>
    NotAppear = 1,

    /// <summary>Carried to later campaign maps onto a unit with the same name.</summary>
    Persistent = 2,

    /// <summary>The "No AI" checkbox (⚠ meaning contradictory in the manual, UNK-37).</summary>
    NoAi = 4,

    /// <summary>Owned by the Rescue pseudo-kingdom (the map object's owner is then Nature).</summary>
    Rescue = 8,
}

/// <summary>Who controls a kingdom.</summary>
public enum PlayerType : byte
{
    /// <summary>A human, or the AI if no human takes the slot.</summary>
    HumanOrAi,

    /// <summary>Always the AI.</summary>
    AiOnly,

    /// <summary>A rescue kingdom (units join whoever reaches them).</summary>
    Rescue,
}

/// <summary>Civilization of a kingdom.</summary>
public enum Civilization : byte
{
    /// <summary>European.</summary>
    European,

    /// <summary>Arabian.</summary>
    Arabian,

    /// <summary>Asian.</summary>
    Asian,

    /// <summary>Random / not set.</summary>
    Random,
}

/// <summary>Kingdom set-up (Appendix C.1).</summary>
/// <param name="Number">Legacy kingdom number, 1–8.</param>
/// <param name="Name">Kingdom name.</param>
/// <param name="TzarName">Tzar (ruler) name.</param>
/// <param name="Civilization">Civilization.</param>
/// <param name="PlayerType">Controller.</param>
/// <param name="Active">Whether the kingdom takes part.</param>
/// <param name="Food">Starting food.</param>
/// <param name="Wood">Starting wood.</param>
/// <param name="Gold">Starting gold.</param>
/// <param name="Stone">Starting stone.</param>
/// <param name="AiScript">AI script (from <c>ai.ini</c>), or empty.</param>
/// <param name="EconomyAi">Economy AI enabled.</param>
/// <param name="WarAi">War AI enabled.</param>
/// <param name="SharedVictory">Allies win together.</param>
/// <param name="StartTileX">Starting location tile column, or −1.</param>
/// <param name="StartTileY">Starting location tile row, or −1.</param>
/// <param name="ForbiddenBuildings">Prohibited building type keys.</param>
/// <param name="ForbiddenUnits">Prohibited unit type keys.</param>
/// <param name="ForbiddenResearches">Prohibited technology keys.</param>
/// <param name="ForbiddenUpgrades">Prohibited upgrade keys.</param>
/// <param name="InitialResearches">Technologies researched at start.</param>
public sealed record KingdomDef(
    int Number,
    string Name,
    string TzarName,
    Civilization Civilization,
    PlayerType PlayerType,
    bool Active,
    int Food,
    int Wood,
    int Gold,
    int Stone,
    string AiScript,
    bool EconomyAi,
    bool WarAi,
    bool SharedVictory,
    int StartTileX,
    int StartTileY,
    EquatableArray<string> ForbiddenBuildings,
    EquatableArray<string> ForbiddenUnits,
    EquatableArray<string> ForbiddenResearches,
    EquatableArray<string> ForbiddenUpgrades,
    EquatableArray<string> InitialResearches);

/// <summary>Diplomacy of one ordered kingdom pair (row → column).</summary>
/// <param name="From">Kingdom 1–8.</param>
/// <param name="To">Kingdom 1–8.</param>
/// <param name="Stance">Stance.</param>
/// <param name="SharedVision">Shared vision.</param>
/// <param name="SharedResources">Shared resources.</param>
/// <param name="CommandUnits">Command units.</param>
public sealed record DiplomacyDef(int From, int To, Stance Stance, bool SharedVision, bool SharedResources, bool CommandUnits);

/// <summary>Game type (map properties).</summary>
public enum GameType : byte
{
    /// <summary>Standard.</summary>
    Standard,

    /// <summary>Not specified (chosen in the lobby).</summary>
    NotSpecified,
}

/// <summary>Victory condition (map properties).</summary>
public enum VictoryCondition : byte
{
    /// <summary>Standard.</summary>
    Standard,

    /// <summary>Destroy the castle.</summary>
    DestroyCastle,

    /// <summary>Score limit.</summary>
    ScoreLimit,

    /// <summary>Time limit.</summary>
    TimeLimit,

    /// <summary>Not specified.</summary>
    NotSpecified,
}

/// <summary>Map properties (Appendix C.1).</summary>
/// <param name="Title">Map title.</param>
/// <param name="Author">Author.</param>
/// <param name="Description">Description.</param>
/// <param name="GameType">Game type.</param>
/// <param name="Victory">Victory condition.</param>
/// <param name="PopulationLimit">Population limit (0 = default).</param>
/// <param name="Difficulty">0 easy, 1 normal, 2 hard.</param>
/// <param name="NoFog">No fog of war.</param>
/// <param name="Explore">Map starts explored.</param>
public sealed record MapProperties(string Title, string Author, string Description, GameType GameType, VictoryCondition Victory, int PopulationLimit, int Difficulty, bool NoFog, bool Explore);
