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
    public static IReadOnlyList<string> All { get; } = ["healing", "bless", "curse", "hypnotise", "convert_exp", "turn_to_stone", "spawn", "skeleton_rise", "magic_star", Whirlpool, Meteor];

    /// <summary>A whirlpool on the water (IMAGES.INI <c>whirlpool</c>: twenty rows at 24 a second; PRES-020).</summary>
    public const string Whirlpool = "whirlpool";

    /// <summary>A falling meteor of the fire rain (IMAGES.INI <c>fire_rain</c>; ReTzar 0x450620).</summary>
    public const string Meteor = "fire_rain";

    /// <summary>The stars a magic vision scatters round its point (ReTzar 0x4735ac: sixteen over a square of 128 pixels).</summary>
    public const int VisionStars = 16;

    /// <summary>The stars round each raised skeleton (ReTzar 0x47367c: three).</summary>
    public const int RaisedStars = 3;

    /// <summary>A meteor as it is made sounds in three cases of ten (ReTzar 0x4507b0, <c>effects\meteor.wav</c>).</summary>
    public const string MeteorSound = @"effects\meteor.wav";

    /// <summary>How often a meteor sounds as it is made, in percent.</summary>
    public const int MeteorSoundPercent = 30;

    /// <summary>The picture a spell's area shows on the ground: a wandering one (the whirlpool) its whirl, the others none.</summary>
    public static string? Area(SpellDefinition spell) => spell is { MoveMs: > 0 } ? Whirlpool : null;

    /// <summary>
    /// The sound a spell makes as it is cast (ReTzar's reading of the original, 0x473c7a to 0x473fd9: <c>SOUND\SPELLS</c>);
    /// summons call the <c>summon_spell</c> set, the fire rain sounds with each meteor instead.
    /// </summary>
    public static string? Sound(SpellDefinition spell) => spell.Effect is SpellEffect.Summon ? "summon_spell" : spell.Key switch
    {
        "spell_heal" => @"spells\heal.wav",
        "spell_bless" => @"spells\bless.wav",
        "spell_berserk" => @"spells\confusion.wav",
        "spell_freeze" => @"spells\freeze.wav",
        "spell_turntostone" => @"spells\turn to stone.wav",
        "spell_cloak" or "cloak" => @"spells\invisible.wav",
        "spell_lightning" => @"spells\lightning.wav",
        "spell_flamebolt" => @"spells\flame ball.wav",
        "spell_whirlpool" => @"spells\whirlpool.wav",
        "spell_magicvision" => @"spells\reveal.wav",
        "spell_raiseexp" => @"spells\teach.wav",
        "raisedead" => @"spells\ressurect.wav",
        "spell_curse" => @"spells\curse.wav",
        _ => null,
    };

    /// <summary>
    /// The <c>[priority]</c> key of a spell's sound: the bolts (lightning, stone, flame ball, freeze) take category 19,
    /// <c>explosion</c>, the others category 13, <c>attack_far</c> (ReTzar's reading, 0x4cdd50).
    /// </summary>
    public static string SoundPriority(SpellDefinition spell)
        => spell.Key is "spell_lightning" or "spell_turntostone" or "spell_flamebolt" or "spell_freeze" ? "explosion" : "attack_far";

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
