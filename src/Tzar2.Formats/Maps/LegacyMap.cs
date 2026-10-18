using System.Collections.Generic;
using System.Collections.Immutable;

namespace Tzar2.Formats.Maps;

/// <summary>
/// A decoded legacy map (<c>.wmp</c>, docs/formats/wmp.md): every member of the HPFS container, decoded as far as the
/// format is known. Fields whose meaning is not proven are kept raw (<see cref="LegacyMapHeader.Raw"/>,
/// <see cref="LegacyKingdom.Raw"/>, <see cref="LegacyObject.Payload"/>) so nothing is lost.
/// </summary>
/// <param name="MapName">The map's internal name (the stem of its <c>MAPS/&lt;name&gt;.header</c> member).</param>
/// <param name="Header">The header member.</param>
/// <param name="Description">The description member.</param>
/// <param name="Kingdoms">The 8 kingdom records.</param>
/// <param name="Diplomacy">8 × 8 relation flags, row-major (row = kingdom, column = other kingdom).</param>
/// <param name="ForbiddenBuildings">Per kingdom: prohibited building names.</param>
/// <param name="ForbiddenUnits">Per kingdom: prohibited unit names.</param>
/// <param name="ForbiddenResearches">Per kingdom: prohibited technology names.</param>
/// <param name="ForbiddenUpgrades">Per kingdom: prohibited upgrade names.</param>
/// <param name="InitialResearches">Per kingdom: technologies researched at start.</param>
/// <param name="Tiles">Side × side tile words, row-major.</param>
/// <param name="Objects">Placed objects in file order.</param>
/// <param name="ObjectExtras">Title and portrait per object id.</param>
/// <param name="Events">Event records.</param>
/// <param name="Actions">Action records.</param>
/// <param name="Rules">Rule records.</param>
/// <param name="Conversations">Conversations.</param>
/// <param name="DefeatMessages">The <c>defeat.messages</c> member (16 bytes, meaning ⚠ UNVERIFIED, UNK-72), or empty.</param>
/// <param name="Music">Custom music playlist from <c>midis.ini</c> (paths as written, e.g. <c>USER\1.mid</c>).</param>
/// <param name="Files">Other members (user resources, AI scripts, INI files), path → bytes, in container order.</param>
/// <param name="Warnings">Irregularities that did not stop decoding.</param>
public sealed record LegacyMap(
    string MapName,
    LegacyMapHeader Header,
    string Description,
    ImmutableArray<LegacyKingdom> Kingdoms,
    ImmutableArray<uint> Diplomacy,
    ImmutableArray<ImmutableArray<string>> ForbiddenBuildings,
    ImmutableArray<ImmutableArray<string>> ForbiddenUnits,
    ImmutableArray<ImmutableArray<string>> ForbiddenResearches,
    ImmutableArray<ImmutableArray<string>> ForbiddenUpgrades,
    ImmutableArray<ImmutableArray<string>> InitialResearches,
    ImmutableArray<ushort> Tiles,
    ImmutableArray<LegacyObject> Objects,
    ImmutableArray<LegacyObjectExtra> ObjectExtras,
    ImmutableArray<LegacyTrigger> Events,
    ImmutableArray<LegacyTrigger> Actions,
    ImmutableArray<LegacyRule> Rules,
    ImmutableArray<LegacyConversation> Conversations,
    ImmutableArray<byte> DefeatMessages,
    ImmutableArray<string> Music,
    ImmutableArray<LegacyFile> Files,
    ImmutableArray<FormatWarning> Warnings)
{
    /// <summary>Map side in tiles.</summary>
    public int Side => Header.Side;
}

/// <summary>A member of a container kept as is.</summary>
/// <param name="Path">Path inside the container, <c>/</c>-separated, as stored (e.g. <c>USER/Woolin.BMP</c>).</param>
/// <param name="Data">Content.</param>
public sealed record LegacyFile(string Path, byte[] Data);

/// <summary>The 1,570-byte map header (docs/formats/wmp.md §2).</summary>
/// <param name="Side">Map side in tiles.</param>
/// <param name="ObjectCount">Records in <c>objects</c>.</param>
/// <param name="RuleCount">Records in <c>rules</c>.</param>
/// <param name="ActionCount">Records in <c>actions</c>.</param>
/// <param name="EventCount">Records in <c>events</c>.</param>
/// <param name="ObjectExtraSize">Byte size of <c>objext</c>.</param>
/// <param name="TilesSize">Byte size of <c>tiles</c>.</param>
/// <param name="Players">Player count shown by the editor (byte 0x4D).</param>
/// <param name="Difficulty">0 easy, 1 normal, 2 hard (byte 0x50).</param>
/// <param name="Title">Map title.</param>
/// <param name="Author">Author.</param>
/// <param name="TerrainSet">Terrain set name (<c>grass</c>, <c>summer</c>, <c>desert</c>, <c>snow</c>).</param>
/// <param name="DescriptionLength">Byte size of <c>description</c>.</param>
/// <param name="ConversationSize">Byte size of <c>conv</c>.</param>
/// <param name="PopulationLimit">Population limit.</param>
/// <param name="TimeLimit">Time limit default (minutes, ⚠ UNVERIFIED).</param>
/// <param name="ScoreLimit">Score limit default (⚠ UNVERIFIED).</param>
/// <param name="Version">Map file version (7 in the reference build).</param>
/// <param name="Raw">The whole header.</param>
public sealed record LegacyMapHeader(
    int Side,
    int ObjectCount,
    int RuleCount,
    int ActionCount,
    int EventCount,
    int ObjectExtraSize,
    int TilesSize,
    int Players,
    int Difficulty,
    string Title,
    string Author,
    string TerrainSet,
    int DescriptionLength,
    int ConversationSize,
    int PopulationLimit,
    int TimeLimit,
    int ScoreLimit,
    int Version,
    ImmutableArray<byte> Raw);

/// <summary>A 152-byte kingdom record (docs/formats/wmp.md §3).</summary>
/// <param name="Number">Kingdom number 1–8.</param>
/// <param name="Name">Kingdom name.</param>
/// <param name="TzarName">Tzar name.</param>
/// <param name="Active">Active flag.</param>
/// <param name="Color">Colour slot (1–8).</param>
/// <param name="Slot">Byte 0x43 (equals the number in every sample, ⚠ UNVERIFIED).</param>
/// <param name="Resources">The four starting resource words at 0x44 (order ⚠ UNVERIFIED: food, wood, gold, stone).</param>
/// <param name="StartX">Start location tile column, or −1.</param>
/// <param name="StartY">Start location tile row, or −1.</param>
/// <param name="Race">1 European, 2 Arabian, 3 Asian (0 random, ⚠ UNVERIFIED).</param>
/// <param name="AiScript">AI script file name, or empty for the default.</param>
/// <param name="AiFlags">Byte 0x79 (⚠ UNVERIFIED: bit 0 economy AI, bit 1 war AI).</param>
/// <param name="PlayerType">Byte 0x7A (⚠ UNVERIFIED: 0 human or AI, 1 AI only, 2 rescue).</param>
/// <param name="Raw">The whole record.</param>
public sealed record LegacyKingdom(
    int Number,
    string Name,
    string TzarName,
    bool Active,
    byte Color,
    byte Slot,
    ImmutableArray<int> Resources,
    int StartX,
    int StartY,
    byte Race,
    string AiScript,
    byte AiFlags,
    byte PlayerType,
    ImmutableArray<byte> Raw);

/// <summary>Object flags of the common object record (word at 0x04).</summary>
[System.Flags]
public enum LegacyObjectFlags : ushort
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Health is the default.</summary>
    DefaultHealth = 0x0040,

    /// <summary>Max health is the default.</summary>
    DefaultMaxHealth = 0x0080,

    /// <summary>Attack is the default.</summary>
    DefaultAttack = 0x0100,

    /// <summary>Defence is the default.</summary>
    DefaultDefence = 0x0200,

    /// <summary>Level (units) / resistance (buildings) is the default (⚠ UNVERIFIED).</summary>
    DefaultLevelOrResistance = 0x0400,

    /// <summary>Mana is the default.</summary>
    DefaultMana = 0x0800,

    /// <summary>Persistent (carried to later campaign maps).</summary>
    Persistent = 0x1000,

    /// <summary>No AI.</summary>
    NoAi = 0x2000,

    /// <summary>Not Appear.</summary>
    NotAppear = 0x4000,
}

/// <summary>
/// A placed object (docs/formats/wmp.md §4). The common 56-byte part is decoded for every record; the class-specific
/// extension is decoded into the optional fields when its layout is known. <see cref="Payload"/> keeps everything.
/// </summary>
/// <param name="Index">Position in the file.</param>
/// <param name="Kind">Record kind byte (always 0 in the reference build).</param>
/// <param name="ClassIndex">Class index (<see cref="LegacyClasses"/>), or −1.</param>
/// <param name="ClassName">Class name.</param>
/// <param name="Flags">Object flags.</param>
/// <param name="X">Tile column (structures: top-left of the footprint).</param>
/// <param name="Y">Tile row.</param>
/// <param name="Owner">0 none, 1–8 kingdoms, 9 Rescue, 10 Nature.</param>
/// <param name="Race">0 common, 1 European, 2 Arabian, 3 Asian.</param>
/// <param name="Id">Object id used by triggers, conversations and <c>objext</c> (0xFFFF = none).</param>
/// <param name="Direction">Facing 0–7.</param>
/// <param name="Health">Health.</param>
/// <param name="MaxHealth">Max health.</param>
/// <param name="Attack">Attack.</param>
/// <param name="Defence">Defence.</param>
/// <param name="Sight">Sight.</param>
/// <param name="Level">Word at 0x1C (level, ⚠ UNVERIFIED; 0 in every sample).</param>
/// <param name="Mana">Mana.</param>
/// <param name="Payload">The whole record.</param>
public sealed record LegacyObject(
    int Index,
    byte Kind,
    int ClassIndex,
    string ClassName,
    LegacyObjectFlags Flags,
    int X,
    int Y,
    byte Owner,
    byte Race,
    ushort Id,
    int Direction,
    int Health,
    int MaxHealth,
    int Attack,
    int Defence,
    int Sight,
    int Level,
    int Mana,
    ImmutableArray<byte> Payload)
{
    /// <summary>Owner value of objects that belong to nobody.</summary>
    public const byte OwnerNone = 0;

    /// <summary>Owner value of the Rescue pseudo-kingdom.</summary>
    public const byte OwnerRescue = 9;

    /// <summary>Owner value of Nature.</summary>
    public const byte OwnerNature = 10;

    /// <summary>Resource amount (gold/stone rocks, fish patches, resource items), or null.</summary>
    public int? Amount { get; init; }

    /// <summary>Image or draw-definition name (decorations, ruins), or null.</summary>
    public string? ImageName { get; init; }

    /// <summary>Pixel position of decorations (x, y), or null.</summary>
    public (int X, int Y)? PixelPosition { get; init; }

    /// <summary>Wall shape / gate or bridge orientation and state (the extension word or bytes), or null.</summary>
    public int? Shape { get; init; }

    /// <summary>Item properties, or null.</summary>
    public LegacyItem? Item { get; init; }
}

/// <summary>Item extension (docs/formats/wmp.md §4.4).</summary>
/// <param name="Name">Item definition name.</param>
/// <param name="Value">The word at 0x60 (bonus value of weapon/armor/potion items, resource type of resource items).</param>
/// <param name="Amount">Resource item amount.</param>
/// <param name="MagicType">Spell book magic type.</param>
/// <param name="NeedsMana">Spell book needs mana.</param>
/// <param name="CooldownSeconds">Spell book time restriction (⚠ UNVERIFIED unit).</param>
/// <param name="Charges">Spell book max charges.</param>
/// <param name="Text">Scroll text.</param>
/// <param name="Image">Scroll image.</param>
/// <param name="Sound">Scroll sound.</param>
/// <param name="Music">Scroll music.</param>
/// <param name="ScrollTimeMs">Scroll time.</param>
/// <param name="AutoClose">Scroll closes by itself.</param>
public sealed record LegacyItem(
    string Name,
    int Value,
    int? Amount = null,
    int? MagicType = null,
    bool? NeedsMana = null,
    int? CooldownSeconds = null,
    int? Charges = null,
    string? Text = null,
    string? Image = null,
    string? Sound = null,
    string? Music = null,
    int? ScrollTimeMs = null,
    bool? AutoClose = null);

/// <summary>An <c>objext</c> record: title and portrait of an object.</summary>
/// <param name="ObjectId">Object id.</param>
/// <param name="Title">Title (the object's name for triggers and persistence).</param>
/// <param name="Icon">Portrait path as written (e.g. <c>USER\Woolin.bmp</c>), or empty.</param>
public sealed record LegacyObjectExtra(ushort ObjectId, string Title, string Icon);

/// <summary>A rule record.</summary>
/// <param name="Name">Rule name.</param>
/// <param name="Condition">Condition expression text.</param>
/// <param name="ActionNames">Action names in order.</param>
public sealed record LegacyRule(string Name, string Condition, ImmutableArray<string> ActionNames);

/// <summary>A conversation.</summary>
/// <param name="Name">Name.</param>
/// <param name="Phrases">Phrases in file order.</param>
public sealed record LegacyConversation(string Name, ImmutableArray<LegacyPhrase> Phrases);

/// <summary>A phrase.</summary>
/// <param name="Id">Phrase id (referenced by follow-ups).</param>
/// <param name="SourceObjectId">Speaker object id.</param>
/// <param name="Initial">Offered when the conversation starts.</param>
/// <param name="Name">Phrase name.</param>
/// <param name="Condition">Condition expression text.</param>
/// <param name="Result">Result expression text.</param>
/// <param name="Icon">Custom portrait path, or empty.</param>
/// <param name="Sound">Sound path, or empty.</param>
/// <param name="Text">Text.</param>
/// <param name="FollowUps">Follow-up phrase ids.</param>
public sealed record LegacyPhrase(
    ushort Id,
    ushort SourceObjectId,
    bool Initial,
    string Name,
    string Condition,
    string Result,
    string Icon,
    string Sound,
    string Text,
    ImmutableArray<ushort> FollowUps);

/// <summary>A campaign (<c>.wcm</c>, docs/formats/wmp.md §8).</summary>
/// <param name="Title">First line of <c>campaign description.ini</c>.</param>
/// <param name="Description">The rest of it.</param>
/// <param name="Maps">Map list of <c>maps.ini</c> in order: name and entry flag.</param>
/// <param name="MapFiles">Embedded map images by member file name (e.g. <c>SAA3.wmp</c>).</param>
/// <param name="Files">Other members.</param>
/// <param name="Warnings">Irregularities.</param>
public sealed record LegacyCampaign(
    string Title,
    string Description,
    ImmutableArray<(string Name, bool Entry)> Maps,
    ImmutableArray<LegacyFile> MapFiles,
    ImmutableArray<LegacyFile> Files,
    ImmutableArray<FormatWarning> Warnings);
