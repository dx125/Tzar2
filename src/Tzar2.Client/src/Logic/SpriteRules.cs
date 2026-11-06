using System;
using System.Collections.Generic;
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

    /// <summary>The picture of a gate's two flags in its kingdom's colours (IMAGES.INI <c>[gates c]</c>, 8 columns of 16 rows).</summary>
    public const string GateFlagsImage = "gates c";

    // Where a gate's two flags go from the top-left of its picture, by column of `gates c` and row (Tzar.exe's table at
    // 0x589770, as ReTzar reads it): rows 0-6 the left flag over the drawbridge's frames, 8-14 the right one; 7 and 15 the
    // ruin's. A column holds one material and facing.
    private static readonly (int X, int Y)[][] GateFlagOffsets =
    [
        Column([(58, 67)], (62, 51), (212, 67), (212, 67)),
        Column([(91, 41)], (0, 0), (175, 41), (136, 126)),
        Column([(61, 34)], (65, 27), (211, 35), (210, 38)),
        Column([(90, 8)], (130, 107), (178, 8), (0, 0)),
        Column([(68, 24), (68, 23), (68, 23), (68, 23), (68, 23), (68, 23), (68, 23)], (76, 19), (66, 150), (64, 143)),
        Column([(70, 61)], (68, 121), (70, 150), (66, 151)),
        Column([(170, 22)], (180, 29), (170, 147), (160, 135)),
        Column([(168, 60)], (172, 118), (168, 151), (417, 150)),
    ];

    private static (int X, int Y)[] Column((int X, int Y)[] left, (int X, int Y) ruin, (int X, int Y) right, (int X, int Y) last)
    {
        var column = new (int X, int Y)[16];
        for (int i = 0; i < 7; i++)
        {
            column[i] = left[Math.Min(i, left.Length - 1)];
            column[8 + i] = right;
        }

        column[7] = ruin;
        column[15] = last;
        return column;
    }

    /// <summary>
    /// The two flags a finished gate flies (PRES-018; ReTzar's reading of the original): the column of <c>gates c</c> for its
    /// facing (1, 0, 3, 2 → 0, 2, 4, 6, one more for stone), the left flag on the drawbridge's row, the right one eight rows
    /// down, each at its offset from the top-left of the gate's picture.
    /// </summary>
    public static IEnumerable<(int Column, int Row, int Dx, int Dy)> GateFlags(int facing, bool stone, int bridgeFrame)
    {
        int column = new[] { 2, 0, 6, 4 }[facing & 3] + (stone ? 1 : 0);
        foreach (int row in new[] { bridgeFrame, bridgeFrame + 8 })
        {
            if ((uint)row < 16u)
            {
                (int dx, int dy) = GateFlagOffsets[column][row];
                yield return (column, row, dx, dy);
            }
        }
    }

    /// <summary>The frame of a gate's picture that holds its two towers (row 8 of <c>g{w|s}{0-3}_anim</c>).</summary>
    public const int GateTowersFrame = 8;

    /// <summary>
    /// What a finished gate draws, in order (PRES-017; ReTzar's reading of the original): for each column of its picture
    /// from the last to the first (a gate drawn down is two columns, back to front), the towers (row 8), then the drawbridge
    /// (<paramref name="bridgeFrame"/>). A picture without the towers' row draws the drawbridge alone.
    /// </summary>
    public static IEnumerable<(int Column, int Frame)> GateLayers(int columns, int frames, int bridgeFrame)
    {
        for (int column = Math.Max(1, columns) - 1; column >= 0; column--)
        {
            if (frames > GateTowersFrame)
            {
                yield return (column, GateTowersFrame);
            }

            yield return (column, bridgeFrame);
        }
    }

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

    /// <summary>
    /// Every picture a wall or gate of <paramref name="type"/> may show (PRES-010): the wall's site and its upright, across
    /// and tower pieces, each also damaged; none for anything else. The match loads them all, as it loads the gates'.
    /// </summary>
    public static IEnumerable<string> WallImages(EntityTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.Traits.IsWall || WallMaterial(type) is not (> 0 and var m))
        {
            yield break;
        }

        string material = m == 1 ? "wood" : "stone";
        yield return $"walls_{material}_build";
        foreach (string shape in new[] { "vert", "horz", "tower" })
        {
            yield return $"walls_{material}_{shape}";
            yield return $"walls_{material}_{shape}_dest";
        }
    }

    /// <summary>
    /// What a destroyed building of <paramref name="type"/> leaves (BLD-028; ReTzar's reading of the original, 0x41db50):
    /// a gate <c>g{w|s}{facing}_demol</c>, its drawbridge fallen; a wall <c>walls_{wood|stone}_debris</c>; a bridge
    /// <c>bridge_{horz|vert}_demol</c>; a harbor <c>debris harbor</c>; any other building <c>debris{W}x{H}</c> of its size up
    /// to 6 (<see cref="RuinFallback"/> when there is none). <paramref name="look"/>: bit 0 mirrored, bit 1 turned.
    /// </summary>
    public static string RuinImage(EntityTypeDefinition type, int look)
    {
        ArgumentNullException.ThrowIfNull(type);
        int facing = look & 3;
        if (GateImage(type, facing, site: false) is { } gate)
        {
            return gate[..^"anim".Length] + "demol";
        }

        if (type.Traits.IsWall && WallMaterial(type) is > 0 and var m)
        {
            return m == 1 ? "walls_wood_debris" : "walls_stone_debris";
        }

        if (type.Traits.IsBridge)
        {
            return (facing & 2) != 0 ? "bridge_vert_demol" : "bridge_horz_demol";
        }

        if (type.Traits.LegacyClass == "harbor")
        {
            return "debris harbor";
        }

        (int w, int h) = type.FootprintSize((facing & 2) != 0 && type.CanTurn);
        return $"debris{Math.Clamp(w, 1, 6)}x{Math.Clamp(h, 1, 6)}";
    }

    /// <summary>The ruin picture of a building whose size has no <c>debris{W}x{H}</c> of its own (0x41dba0).</summary>
    public const string RuinFallback = "debris3x3";

    /// <summary>
    /// Every ruin picture a destroyed <paramref name="type"/> may leave (BLD-028), for the match to load: its gate's four
    /// facings or a bridge's two ways; none for what leaves no ruin.
    /// </summary>
    public static IEnumerable<string> RuinImages(EntityTypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.HasFootprint || !type.Traits.LeavesRuin)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int look = 0; look < 4; look++)
        {
            if (seen.Add(RuinImage(type, look)))
            {
                yield return RuinImage(type, look);
            }
        }

        if (seen.Add(RuinFallback))
        {
            yield return RuinFallback;
        }
    }

    /// <summary>
    /// The frame a ruin shows (ReTzar's reading, 0x444bd0): the next one every two seconds; rubble that stays rests on its
    /// last, a ruin is gone past its last (null).
    /// </summary>
    public static int? RuinFrame(int ageMs, int frames, bool lasting)
    {
        int last = Math.Max(1, frames) - 1;
        if (lasting)
        {
            return last;
        }

        int f = Math.Max(0, ageMs) / RuinFrameMs;
        return f <= last ? f : null;
    }

    /// <summary>How long a ruin shows each of its frames (0x444ba8: 2000 ms).</summary>
    public const int RuinFrameMs = 2000;

    /// <summary>The frame of a damaged wall's picture (ReTzar's reading, 0x4fbde0): rows − ⌊health × rows / most⌋ − 1.</summary>
    public static int WallDamageFrame(int health, int maxHealth, int frames)
        => maxHealth <= 0 || frames <= 0 ? 0 : Math.Clamp(frames - (int)((long)Math.Max(0, health) * frames / maxHealth) - 1, 0, frames - 1);

    /// <summary>The picture of a field short of its full health, growing or withering (ECO-012; ReTzar's reading, 0x44c880).</summary>
    public const string FieldGrowImage = "field_grow";

    /// <summary>The growth stage a field short of full health shows (0x44c89b): ⌊health × rows / most⌋, at most the last row.</summary>
    public static int FieldGrowFrame(int health, int maxHealth, int frames)
        => maxHealth <= 0 || frames <= 0 ? 0 : Math.Clamp((int)((long)Math.Max(0, health) * frames / maxHealth), 0, frames - 1);

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
