using System;
using System.Runtime.InteropServices;
using Tzar2.Core.Ids;

namespace Tzar2.Sim.Commands;

/// <summary>
/// Command kinds (REQ-SIM-11). Values are wire format: never renumber, only append. Ranges: 1-0x0FFF orders mirroring
/// <c>ORDERS.INI</c> (filled in with the gameplay systems), 0xF000-0xFFFF debug/cheat commands (GAME-19).
/// </summary>
public enum CommandKind : ushort
{
    /// <summary>Invalid.</summary>
    None = 0,

    /// <summary>Stop the listed units.</summary>
    Stop = 1,

    /// <summary>
    /// Move the listed units to (X, Y) (TDD §9.3 right-click). Units of one command that share a locomotor form a
    /// group: arrival slots around the goal (REQ-PATH-05) and, from 6 units, a shared flow field (REQ-PATH-04).
    /// </summary>
    Move = 2,

    /// <summary>Delete the listed units (the original's Del key).</summary>
    Delete = 3,

    /// <summary>Patrol between each unit's current position and (X, Y) until given another order.</summary>
    Patrol = 4,

    /// <summary>Follow <see cref="Command.Target"/>: keep re-pathing to it and stay close (guard/escort base order).</summary>
    Follow = 5,

    /// <summary>Gather from resource node <see cref="Command.Target"/> and carry to the nearest drop-off (GAME-02).</summary>
    Harvest = 6,

    /// <summary>
    /// Construct (GAME-03): a new building of type Arg0 with its footprint's top-left at the tile containing (X, Y), paid
    /// at placement; Arg1 = 1 turns the footprint by 90° (gates and bridges, GAME-03). With <see cref="Command.Target"/>
    /// set, help build that existing construction site.
    /// </summary>
    Build = 7,

    /// <summary>
    /// Queue Arg1 (1-24) units of type Arg0 in each listed producer, each paid when queued (GAME-05); any number of any
    /// items queue (ADR 0180).
    /// </summary>
    Train = 8,

    /// <summary>
    /// Cancel: a listed construction site is removed with a refund; a listed producer drops one item of queue entry Arg0
    /// (-1 = the last entry), or with Arg1 &gt; 0 one item of queue item Arg1 − 1 (<c>QueueItems</c>), with a refund
    /// (ADR 0180).
    /// </summary>
    Cancel = 9,

    /// <summary>
    /// Set the rally point of the listed producers to (X, Y); Arg0 = 1 clears it. Arg1 = <c>RallyMode</c> (0 move, 1 default
    /// action on <see cref="Command.Target"/>, 2 move-advance; TDD §9.3).
    /// </summary>
    SetRally = 10,

    /// <summary>Attack <see cref="Command.Target"/> until it dies (GAME-07). Own cows may be attacked: butchering for meat.</summary>
    Attack = 11,

    /// <summary>
    /// Research technology Arg0 (GAME-06): queued in the first listed own building that researches it, paid when queued;
    /// rejected if researched or queued anywhere already.
    /// </summary>
    Research = 12,

    /// <summary>Buy upgrade Arg0 for each listed own entity that offers it (towers, walls, gates, galleons), paid when queued.</summary>
    Upgrade = 13,

    /// <summary>Repair own damaged building, ship or siege weapon <see cref="Command.Target"/> (GAME-03).</summary>
    Repair = 14,

    /// <summary>Capture enemy building <see cref="Command.Target"/> (GAME-04), or steal an enemy cow under Cow Poaching.</summary>
    Capture = 15,

    /// <summary>
    /// Drag a wall (GAME-03): segments of wall type Arg0 on the straight tile line from (X, Y) to the tile packed in
    /// Arg1 (<c>tileY &lt;&lt; 16 | tileX</c>), each a site paid when placed; tiles that do not fit are skipped.
    /// </summary>
    BuildWall = 16,

    /// <summary>Set the listed gates: Arg0 = 0 close, 1 open (both manual), 2 automatic (GAME-03).</summary>
    SetGate = 17,

    /// <summary>
    /// Diplomacy (GAME-13): towards kingdom X take stance Arg0 (<c>Stance</c>) and offer Arg1 (<c>Sharing</c> flags). At most
    /// one change per pair per <c>world.diplomacyChangeIntervalMs</c>; sharing takes effect only when both kingdoms offer it.
    /// </summary>
    SetDiplomacy = 40,

    /// <summary>Tribute (GAME-13): send Arg1 of resource Arg0 to kingdom X (rate-limited, bounded, never overflowing).</summary>
    Tribute = 41,

    /// <summary>Resign (GAME-13/17): the kingdom is defeated at once.</summary>
    Resign = 42,

    /// <summary>
    /// Trade (GAME-14): the listed traders cycle between dock <see cref="Command.Target"/> (A) and the dock at (X, Y) (B),
    /// loading resource (Arg0 &amp; 0xFF) at A and (Arg0 &gt;&gt; 8 &amp; 0xFF) at B, Arg1 per trip (0 = full load).
    /// </summary>
    Trade = 43,

    /// <summary>Tavern conversion (GAME-14): in a listed tavern give Arg1 of resource (Arg0 &amp; 0xFF) for a tenth as much of (Arg0 &gt;&gt; 8).</summary>
    Convert = 44,

    /// <summary>Gambling (GAME-14, Gambling): stake Arg1 of resource (Arg0 &amp; 0xFF) at odds index (Arg0 &gt;&gt; 8) in a listed tavern.</summary>
    Gamble = 45,

    /// <summary>Loans (GAME-14, Usury): Arg1 = 0 take a loan of resource Arg0 in a listed tavern, 1 repay the outstanding loan.</summary>
    Loan = 46,

    /// <summary>
    /// Tavern hiring (GAME-14, ADR 0108): in a listed own tavern hire offer Arg0 of this period (mercenary groups first, then
    /// heroes; see <c>TavernOffers</c>), paid at once; Mercenaries / Heroes' Quest needed.
    /// </summary>
    Hire = 47,

    /// <summary>
    /// Multiplayer drop policy (REQ-NET-07, ADR 0163): Arg0 = 1 a computer takes the kingdom over
    /// (<see cref="PlayerFlags.AiControlled"/>), 0 gives it back to its human. Issued only by the game layer from the
    /// relay's control events, in the same tick on every peer; peers' own batches never carry it (the game drops it).
    /// </summary>
    SetController = 48,
    // GAME-07…11 (ADR 0096): values 20-39.

    /// <summary>The listed units walk to own holder <see cref="Command.Target"/> (castle, outpost, ship, carpet) and enter it (GAME-09).</summary>
    Board = 20,

    /// <summary>The listed holders let their units out next to (X, Y) (orders emerge/unload); Arg0 &gt; 0 = only that many.</summary>
    Unload = 21,

    /// <summary>The listed units walk to item <see cref="Command.Target"/> and pick it up (the first to arrive gets it; GAME-10).</summary>
    PickItem = 22,

    /// <summary>The listed unit drops the item in inventory slot Arg0 where it stands.</summary>
    DropItem = 23,

    /// <summary>The listed unit uses the item in slot Arg0 (-1 = the default item) on <see cref="Command.Target"/> or (X, Y).</summary>
    UseItem = 24,

    /// <summary>The listed unit walks to own unit <see cref="Command.Target"/> and gives it the item in slot Arg0.</summary>
    GiveItem = 25,

    /// <summary>The listed unit makes slot Arg0 its default item (-1 = none).</summary>
    SetDefaultItem = 26,

    /// <summary>The listed casters cast spell Arg0 on <see cref="Command.Target"/>, at (X, Y), or on themselves (GAME-11).</summary>
    CastSpell = 27,

    /// <summary>The listed armed entities take stance Arg0 (0 aggressive, 1 stand ground, 2 hold fire; GAME-07).</summary>
    SetStance = 28,

    /// <summary>The listed ranged units attack the ground at (X, Y) (siege; GAME-07).</summary>
    AttackGround = 29,

    /// <summary>The listed units turn into type Arg0 (Jihad warriors from peasants, priests from monks), paying its cost.</summary>
    ConvertUnit = 30,

    /// <summary>The listed peasants walk to enemy unit <see cref="Command.Target"/> and bribe it with gold (Bribery).</summary>
    Bribe = 31,

    /// <summary>The listed cathedral preaches a crusade: an anchor at (X, Y), at most 4 tiles away (GAME-11).</summary>
    PreachCrusade = 32,

    /// <summary>The listed Magic Workshop crafts item type Arg0 for Arg1 gold (more gold, better item; GAME-10).</summary>
    CraftItem = 33,

    /// <summary>
    /// Scenario reply (ADR 0131): in conversation session Arg0 (its serial) say candidate phrase Arg1 (0-based, in the
    /// offered order). Refused unless a conversation waits for this player's choice.
    /// </summary>
    ScenarioReply = 60,

    /// <summary>Explore: the units head for unexplored ground they can reach, ships on water (ADR 0143).</summary>
    Explore = 34,

    /// <summary>Debug: spawn Arg1 (1-64) units of type Arg0 at (X, Y); needs <see cref="SimulationConfig.CheatsAllowed"/>.</summary>
    DebugSpawn = 0xF000,

    /// <summary>Debug: kill the listed entities of any owner (leaving corpses); needs <see cref="SimulationConfig.CheatsAllowed"/>.</summary>
    DebugKill = 0xF001,

    /// <summary>Cheat code Arg0 (<c>CheatCode</c>, the <c>hm*</c> chat codes, GAME-19); needs <see cref="SimulationConfig.CheatsAllowed"/>.</summary>
    Cheat = 0xF010,
}

/// <summary>Input modifiers carried by a command (TDD §9.3: Shift/Ctrl/Alt variants of orders).</summary>
[Flags]
public enum CommandFlags : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Shift: queue (waypoints) instead of replacing the current order.</summary>
    Queue = 1,

    /// <summary>Ctrl: e.g. move-advance (attack-move: a Move with Ctrl attacks enemies met on the way).</summary>
    Ctrl = 2,

    /// <summary>Alt: e.g. move-sneak.</summary>
    Alt = 4,
}

/// <summary>
/// The fixed part of one command (32 bytes, unmanaged). The entity set lives in the owning <see cref="CommandList"/>
/// at <see cref="EntityOffset"/>..+<see cref="EntityCount"/>, sorted ascending and free of duplicates.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Command
{
    internal Command(CommandKind kind, byte player, CommandFlags flags, EntityId target, int x, int y, int arg0, int arg1, int entityOffset, int entityCount)
    {
        Kind = kind;
        Player = player;
        Flags = flags;
        Target = target;
        X = x;
        Y = y;
        Arg0 = arg0;
        Arg1 = arg1;
        EntityOffset = entityOffset;
        EntityCount = entityCount;
    }

    /// <summary>What to do.</summary>
    public CommandKind Kind { get; }

    /// <summary>Issuing player slot, stamped by the relay/session from the sender (ADR 0009), never trusted from the client.</summary>
    public byte Player { get; }

    /// <summary>Modifiers.</summary>
    public CommandFlags Flags { get; }

    /// <summary>Target entity, or <see cref="EntityId.None"/>.</summary>
    public EntityId Target { get; }

    /// <summary>Target X in world units.</summary>
    public int X { get; }

    /// <summary>Target Y in world units.</summary>
    public int Y { get; }

    /// <summary>Kind-specific argument (type id, count, mode...).</summary>
    public int Arg0 { get; }

    /// <summary>Kind-specific argument.</summary>
    public int Arg1 { get; }

    /// <summary>Start of the entity set in the owning list.</summary>
    public int EntityOffset { get; }

    /// <summary>Size of the entity set.</summary>
    public int EntityCount { get; }

    internal Command WithPlayer(byte player, int entityOffset)
        => new(Kind, player, Flags, Target, X, Y, Arg0, Arg1, entityOffset, EntityCount);
}
