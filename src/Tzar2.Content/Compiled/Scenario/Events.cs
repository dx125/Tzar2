using Tzar2.Content.Compiled.Scenario.Expressions;

namespace Tzar2.Content.Compiled.Scenario;

/// <summary>
/// A named event (Appendix C.5): a boolean variable the game keeps true while its condition holds. Conditions refer to
/// it by index (<see cref="EventRef"/>).
/// </summary>
/// <param name="Name">The event's name (letters, digits, underscore in the legacy editor).</param>
public abstract record EventDef(string Name);

/// <summary>The objects are in the area.</summary>
/// <param name="Name">Event name.</param>
/// <param name="Objects">Which objects.</param>
/// <param name="Area">Where.</param>
public sealed record ObjectInAreaEvent(string Name, ObjectSpec Objects, AreaSpec Area) : EventDef(Name);

/// <summary>The objects were destroyed or killed.</summary>
/// <param name="Name">Event name.</param>
/// <param name="Objects">Which objects.</param>
public sealed record ObjectDestroyedEvent(string Name, ObjectSpec Objects) : EventDef(Name);

/// <summary>The objects entered the holder.</summary>
/// <param name="Name">Event name.</param>
/// <param name="Objects">Which objects.</param>
/// <param name="HolderIndex">Index of the holder object in <see cref="MapData.Objects"/>, or −1 when unset.</param>
public sealed record ObjectsEnterHolderEvent(string Name, ObjectSpec Objects, int HolderIndex) : EventDef(Name);

/// <summary>The objects left the holder.</summary>
/// <param name="Name">Event name.</param>
/// <param name="Objects">Which objects.</param>
/// <param name="HolderIndex">Index of the holder object in <see cref="MapData.Objects"/>, or −1 when unset.</param>
public sealed record ObjectLeaveHolderEvent(string Name, ObjectSpec Objects, int HolderIndex) : EventDef(Name);

/// <summary>The objects exist (are alive).</summary>
/// <param name="Name">Event name.</param>
/// <param name="Objects">Which objects.</param>
public sealed record ObjectExistEvent(string Name, ObjectSpec Objects) : EventDef(Name);

/// <summary>One of the players was defeated.</summary>
/// <param name="Name">Event name.</param>
/// <param name="Players">Legacy player mask (bit 0 = player 1).</param>
public sealed record PlayerDefeatedEvent(string Name, ushort Players) : EventDef(Name);

/// <summary>One of the players won.</summary>
/// <param name="Name">Event name.</param>
/// <param name="Players">Legacy player mask (bit 0 = player 1).</param>
public sealed record PlayerWonEvent(string Name, ushort Players) : EventDef(Name);

/// <summary>The expression, evaluated for the objects (reserved variables = the object's properties), is true (UNK-38).</summary>
/// <param name="Name">Event name.</param>
/// <param name="Objects">Which objects.</param>
/// <param name="Expression">The condition.</param>
public sealed record ObjectExpressionEvent(string Name, ObjectSpec Objects, ExpressionDef Expression) : EventDef(Name);

/// <summary>An event kind the converter does not understand; kept so nothing is lost silently. Always false.</summary>
/// <param name="Name">Event name.</param>
/// <param name="LegacyCode">The legacy type code.</param>
/// <param name="Raw">The undecoded record.</param>
public sealed record UnknownEvent(string Name, int LegacyCode, EquatableArray<byte> Raw) : EventDef(Name);
