using System.Collections.Generic;
using Tzar2.Content.Compiled;

namespace Tzar2.Client.Logic;

/// <summary>
/// The pictures of spells (PRES-019; ReTzar's reading of the original's spell code, 0x472e83 to 0x473bf0): what plays over a
/// unit a spell takes, where a stone spell strikes, and how a summoned creature comes out.
/// </summary>
public static class SpellPictures
{
    /// <summary>The lightning's order key: drawn as three bolts from the caster to the target (0x472e83).</summary>
    public const string LightningKey = "spell_lightning";

    /// <summary>How long a lightning's bolts last (0x46d510: 700 ms and a tick).</summary>
    public const int LightningMs = 750;

    /// <summary>Every spell picture a match may need.</summary>
    public static IReadOnlyList<string> All { get; } = ["healing", "bless", "curse", "hypnotise", "convert_exp", "turn_to_stone", "spawn", "skeleton_rise", "magic_star"];

    /// <summary>
    /// The picture that plays over a unit a spell takes (overlay 0x4e1d0a's kind): the heal's, the blessing's, the curse's
    /// (also confusion's), hypnosis's and teaching's; none for the others.
    /// </summary>
    public static string? Overlay(SpellEffect effect) => effect switch
    {
        SpellEffect.Heal => "healing",
        SpellEffect.Bless => "bless",
        SpellEffect.Curse or SpellEffect.Confuse => "curse",
        SpellEffect.Hypnotize => "hypnotise",
        SpellEffect.Experience => "convert_exp",
        SpellEffect.Stone => "turn_to_stone",
        SpellEffect.Cloak or SpellEffect.SoulBind => "magic_star",
        _ => null,
    };

    /// <summary>
    /// How a summoned creature comes out (0x473ab3, 0x4737dd): the golem, the carpet, the jinn and the dragon in the spawn
    /// picture, a raised skeleton rising; a bat as it is.
    /// </summary>
    public static string? Appearance(string legacyClass) => legacyClass switch
    {
        "troll" or "carpet" or "jinn" or "dragon" => "spawn",
        "skeleton" => "skeleton_rise",
        _ => null,
    };
}
