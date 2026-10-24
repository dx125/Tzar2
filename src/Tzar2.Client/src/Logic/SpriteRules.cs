using System;
using Tzar2.Content.Compiled;

namespace Tzar2.Client.Logic;

/// <summary>Engine-free sprite choices of the world renderer (unit-tested).</summary>
public static class SpriteRules
{
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

    /// <summary>
    /// The depletion frame of a rock (frame 0 = full) holding <paramref name="left"/> of <paramref name="full"/>
    /// (⚠ the original's thresholds are not measured).
    /// </summary>
    public static int DepletionFrame(int left, int full, int frames)
    {
        if (full <= 0 || frames <= 1)
        {
            return 0;
        }

        int remaining = (int)Math.Clamp(left * (long)frames / full, 0, frames - 1);
        return frames - 1 - remaining;
    }
}
