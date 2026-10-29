using System;
using Tzar2.Content.Compiled;

namespace Tzar2.Client.Logic;

/// <summary>Engine-free sprite choices of the world renderer (unit-tested).</summary>
public static class SpriteRules
{
    /// <summary>
    /// The painter's order of a picture whose place is <paramref name="y"/> (world pixels; higher is drawn later, in
    /// front), with <paramref name="sub"/> to order the pictures of one place.
    /// </summary>
    public static uint PainterKey(int y, uint sub = 0) => ((uint)(y + 4096) << 6) | sub;

    /// <summary>
    /// PRES-014 (ReTzar's reading of the original's painter, 0x500950 and 0x4f1000): a building takes its place in the
    /// painter's order from the top of its footprint, not from its foot, so the units on the tiles beside it and on the
    /// free tiles of its footprint (the front of the stables) are drawn in front of it, and those behind its top row
    /// behind it. One pixel lower keeps a tree on the row above it behind it.
    /// </summary>
    public static int StructureOrderY(int footprintTop) => footprintTop + 1;

    /// <summary>
    /// The sheet column of a sim facing. Sim: 0 = east, clockwise (ADR 0005). 8-direction sheets: S, SE, E, NE, N, NW,
    /// W, SW (counter-clockwise from south, as the viewer labels them).
    /// </summary>
    public static int Direction(int facing, int directions)
        => directions < 8 ? 0 : (((2 - facing) % 8) + 8) % 8;

    /// <summary>
    /// The column a unit facing <paramref name="column"/> shows in a picture with IMAGES.INI <c>startcol</c>
    /// (PRES-008): the original turns a unit to its target and adds the column (ReTzar's reading, 0x4fd450 and +0xaa; the
    /// knight's, the longbowman's and the ships' attack sheets have 2, so a knight striking east shows column 4).
    /// </summary>
    public static int TurnedColumn(int column, int startCol, int directions)
        => directions < 8 || startCol == 0 ? column : (column + startCol) & 7;

    /// <summary>
    /// The picture of blow number <paramref name="blow"/> (PRES-005): half of a unit's blows use its second swing when it
    /// has one (<c>image_attack2</c>; the original draws at random, 0x4e5720, ReTzar's reading; Tzar2 alternates by a
    /// hash of the blow, which the simulation never sees).
    /// </summary>
    public static UnitAnim Swing(bool hasSecond, uint blow)
        => hasSecond && ((blow * 2246822519u) >> 31) == 1 ? UnitAnim.Attack2 : UnitAnim.Attack;

    /// <summary>
    /// The picture of blow number <paramref name="blow"/> for a unit that may also strike its third swing
    /// (<c>image_attack3</c>; PRES-005): with <paramref name="third"/> each of the three swings takes a third of the blows, as
    /// the Asian peasant under Kung Fu draws one of three at random (ReTzar 0x4ae090); otherwise as <see cref="Swing(bool, uint)"/>.
    /// </summary>
    public static UnitAnim Swing(bool hasSecond, bool third, uint blow)
    {
        if (!third)
        {
            return Swing(hasSecond, blow);
        }

        return ((ulong)(blow * 2246822519u) * 3 >> 32) switch
        {
            0 => UnitAnim.Attack,
            1 => hasSecond ? UnitAnim.Attack2 : UnitAnim.Attack,
            _ => UnitAnim.Attack3,
        };
    }

    /// <summary>
    /// The frame of a swing <paramref name="elapsed"/> frames after it began, in a picture starting at IMAGES.INI
    /// <c>startrow</c> (PRES-008; ReTzar's reading, 0x5017f0: a picture set on row r shows r first, then runs on and wraps).
    /// </summary>
    public static int SwingFrame(int startRow, int elapsed, int frames)
        => frames <= 0 ? 0 : (((startRow + elapsed) % frames) + frames) % frames;

    /// <summary>
    /// The picture of a structure turned by 90° (BLD-019): a bridge's <c>bridge_horz</c> becomes <c>bridge_vert</c>; others
    /// keep theirs.
    /// </summary>
    public static string? Turned(string? image, bool rotated)
        => rotated && image is not null && image.Contains("_horz", StringComparison.Ordinal) ? image.Replace("_horz", "_vert", StringComparison.Ordinal) : image;

    /// <summary>
    /// A gate's drawbridge picture for its facing (0 and 1 across, 2 and 3 down; CTRL-024): IMAGES.INI <c>gw0_anim</c> …
    /// <c>gw3_anim</c> for wooden gates, <c>gs…</c> for stone ones, <c>…_build</c> for sites; null for what is no gate.
    /// </summary>
    public static string? GateImage(EntityTypeDefinition type, int facing, bool site)
    {
        ArgumentNullException.ThrowIfNull(type);
        string? material = type.Traits.LegacyClass switch
        {
            "gate_wood" => "gw",
            "gate_stone" => "gs",
            _ => null,
        };
        return material is null ? null : $"{material}{facing & 3}_{(site ? "build" : "anim")}";
    }

    /// <summary>The drawbridge's frame: raised (closed) on row 0, lowered (open) on row 6 (ReTzar's reading, 0x456080).</summary>
    public static int GateFrame(bool open) => open ? 6 : 0;

    /// <summary>The construction-site sheet for a footprint (<c>build1x1</c> … <c>build8x8</c> of IMAGES.INI).</summary>
    public static string BuildSiteName(EntityTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        int n = Math.Clamp(Math.Max(type.FootprintWidth, type.FootprintHeight), 1, 8);
        return $"build{n}x{n}";
    }

    /// <summary>The construction stage frame (0 … frames − 1) of a site at <paramref name="permille"/> progress.</summary>
    public static int SiteFrame(int permille, int frames)
        => Math.Clamp(permille * frames / 1000, 0, Math.Max(0, frames - 1));

    /// <summary>The material of a wall or gate (1 wood, 2 stone; 0 for anything else): walls join pieces of their own.</summary>
    public static byte WallMaterial(EntityTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Traits.LegacyClass switch
        {
            "wall_wood" or "gate_wood" => 1,
            "wall_stone" or "gate_stone" => 2,
            _ => 0,
        };
    }

    /// <summary>
    /// The neighbours of the wall tile (<paramref name="x"/>, <paramref name="y"/>) in <paramref name="pieces"/> (a
    /// material per tile, <see cref="WallMaterial"/>, row by row <paramref name="width"/> wide) that are of
    /// <paramref name="material"/>: 1 north, 2 east, 4 south, 8 west (PRES-010).
    /// </summary>
    public static int WallMask(ReadOnlySpan<byte> pieces, int width, int x, int y, byte material)
    {
        return (Joins(pieces, width, x, y - 1, material) ? 1 : 0) | (Joins(pieces, width, x + 1, y, material) ? 2 : 0)
            | (Joins(pieces, width, x, y + 1, material) ? 4 : 0) | (Joins(pieces, width, x - 1, y, material) ? 8 : 0);
    }

    private static bool Joins(ReadOnlySpan<byte> pieces, int width, int x, int y, byte material)
        => material != 0 && width > 0 && x >= 0 && y >= 0 && x < width && (y * width) + x < pieces.Length && pieces[(y * width) + x] == material;

    /// <summary>
    /// A wall's picture (PRES-010; ReTzar's reading of the original, 0x4fb690, 0x4fbde0, 0x4fbce0): a site
    /// <c>walls_{wood|stone}_build</c>; a finished wall <c>_vert</c> when joined north and south only, <c>_horz</c> east
    /// and west only, otherwise a <c>_tower</c>, each <c>_dest</c> when damaged. Null for what is no wall.
    /// </summary>
    public static string? WallImage(EntityTypeDefinition type, int mask, bool site, bool damaged)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.Traits.IsWall || WallMaterial(type) is not (> 0 and var m))
        {
            return null;
        }

        string material = m == 1 ? "wood" : "stone";
        if (site)
        {
            return $"walls_{material}_build";
        }

        string shape = mask switch
        {
            5 => "vert",
            10 => "horz",
            _ => "tower",
        };
        return $"walls_{material}_{shape}{(damaged ? "_dest" : "")}";
    }

    /// <summary>The frame of a damaged wall's picture (ReTzar's reading, 0x4fbde0): rows − ⌊health × rows / most⌋ − 1.</summary>
    public static int WallDamageFrame(int health, int maxHealth, int frames)
        => maxHealth <= 0 || frames <= 0 ? 0 : Math.Clamp(frames - (int)((long)Math.Max(0, health) * frames / maxHealth) - 1, 0, frames - 1);

    /// <summary>Whether <paramref name="type"/> is a gold or stone mine, whose picture is a look, not an animation (PRES-011).</summary>
    public static bool IsMine(EntityTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Traits.LegacyClass is "res_gold" or "res_stone";
    }

    /// <summary>
    /// The frame of a gold or stone mine (PRES-011; ReTzar's reading of the original, 0x4afeae): one of the first three
    /// pictures, the mine's own look for its whole life, picked by <paramref name="seed"/>; the last one, the pit, once
    /// <paramref name="left"/> is 0. A stock not known (below 0: a mine remembered under the fog) keeps the look.
    /// </summary>
    public static int MineFrame(uint seed, int left, int frames)
        => frames <= 1 ? 0 : left == 0 ? frames - 1 : (int)(seed % (uint)Math.Min(frames - 1, 3));
}
