namespace Tzar2.Content.Compiled.Scenario;

/// <summary>How a set of objects satisfies a condition (Appendix C.4).</summary>
public enum Quantifier : byte
{
    /// <summary>Every object must satisfy it.</summary>
    All,

    /// <summary>At least one object satisfies it.</summary>
    Any,
}

/// <summary>Count comparison of a typed object specification.</summary>
public enum CountComparison : byte
{
    /// <summary>At least <c>count</c> objects.</summary>
    AtLeast,

    /// <summary>At most <c>count</c> objects.</summary>
    AtMost,
}

/// <summary>
/// Which objects a trigger talks about (Appendix C.4): an explicit set of placed objects, or a type with players and a
/// count.
/// </summary>
public abstract record ObjectSpec;

/// <summary>An explicit set of placed objects.</summary>
/// <param name="ObjectIndices">Indices into <see cref="MapData.Objects"/> (also <see cref="ScenarioData.Objects"/>).</param>
/// <param name="Quantifier">All or Any.</param>
public sealed record NamedObjects(EquatableArray<int> ObjectIndices, Quantifier Quantifier) : ObjectSpec;

/// <summary>Objects of a type owned by some players, counted.</summary>
/// <param name="TypeKey">Entity type key in the ruleset (e.g. <c>euro.knight</c>), or the legacy name when unmapped.</param>
/// <param name="Players">Legacy players, bit 0 = player 1 … bit 7 = player 8 (higher bits: pseudo-players, see <see cref="PlayerMasks"/>).</param>
/// <param name="Comparison">At least / at most.</param>
/// <param name="Count">The count.</param>
/// <param name="Quantifier">All or Any.</param>
public sealed record TypedObjects(string TypeKey, ushort Players, CountComparison Comparison, int Count, Quantifier Quantifier) : ObjectSpec;

/// <summary>Object categories of the original editor's type filter (ADR 0120).</summary>
public enum ObjectCategory : byte
{
    /// <summary>Any object.</summary>
    AnyObject,

    /// <summary>"Major objects" (⚠ UNVERIFIED which classes count, UNK-75).</summary>
    MajorObjects,

    /// <summary>Units.</summary>
    Units,

    /// <summary>Military units.</summary>
    MilitaryUnits,

    /// <summary>Buildings.</summary>
    Buildings,
}

/// <summary>Objects of a category owned by some players, counted (like <see cref="TypedObjects"/> without a type).</summary>
/// <param name="Category">The category.</param>
/// <param name="Players">Legacy players, bit 0 = player 1.</param>
/// <param name="Comparison">At least / at most.</param>
/// <param name="Count">The count.</param>
/// <param name="Quantifier">All or Any.</param>
public sealed record CategoryObjects(ObjectCategory Category, ushort Players, CountComparison Comparison, int Count, Quantifier Quantifier) : ObjectSpec;

/// <summary>An area (Appendix C.4): a tile rectangle, or the current position of an object.</summary>
public abstract record AreaSpec;

/// <summary>A rectangle of tiles, corners inclusive.</summary>
/// <param name="Left">Left tile column.</param>
/// <param name="Top">Top tile row.</param>
/// <param name="Right">Right tile column (inclusive).</param>
/// <param name="Bottom">Bottom tile row (inclusive).</param>
public sealed record RectArea(int Left, int Top, int Right, int Bottom) : AreaSpec;

/// <summary>The current position of a placed object.</summary>
/// <param name="ObjectIndex">Index into <see cref="MapData.Objects"/>.</param>
/// <param name="RadiusTiles">An extent around the object if the source stores one, else null (⚠ UNVERIFIED, UNK-04).</param>
public sealed record ObjectArea(int ObjectIndex, int? RadiusTiles = null) : AreaSpec;

/// <summary>A user resource of the map package (image, sound or music), by its path under <c>assets/</c>.</summary>
/// <param name="Path">Logical path, e.g. <c>user/hero.png</c>.</param>
public sealed record ResourceRef(string Path);

/// <summary>Helpers for legacy player masks (bit 0 = player 1).</summary>
public static class PlayerMasks
{
    /// <summary>Players 1–8.</summary>
    public const ushort AllPlayers = 0x00FF;

    /// <summary>The mask of legacy player <paramref name="player"/> (1-based).</summary>
    public static ushort Of(int player) => player is >= 1 and <= 16 ? (ushort)(1 << (player - 1)) : (ushort)0;

    /// <summary>Whether <paramref name="mask"/> contains legacy player <paramref name="player"/> (1-based).</summary>
    public static bool Contains(ushort mask, int player) => (mask & Of(player)) != 0;
}
