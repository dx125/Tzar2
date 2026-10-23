using System;
using System.Collections.Immutable;
using Tzar2.Content.Compiled;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Systems;

namespace Tzar2.Game.Input;

/// <summary>
/// The original's <c>hm*</c> chat codes (GAME-19, TDD §10; community list, UNK-30) as debug console commands. Each code
/// becomes a deterministic sim command, so cheats replay and stay in lockstep; the sim refuses them unless the match
/// allows cheats (a sandbox, or a lobby where every player agreed; never in ranked matches).
/// </summary>
public static class CheatCodes
{
    /// <summary>The codes and what they do (for the help text and tests).</summary>
    public static readonly ImmutableArray<(string Code, string Effect)> All =
    [
        ("hmreveal", "explore the whole map"),
        ("hmnofog", "toggle No Fog"),
        ("hmnotech", "toggle: every requirement is met"),
        ("hmgod", "toggle: own units and buildings take no damage"),
        ("hmbuildozer", "toggle: build, train and research at once"),
        ("hmdvaleva", "resources (small)"),
        ("hmpetleva", "resources (large)"),
        ("hmnext", "win the match"),
        ("hmspawn <unit> [count]", "spawn units at the given point"),
    ];

    /// <summary>
    /// Parses a chat line; on a cheat code appends its command to <paramref name="into"/>.
    /// <c>hmspawn</c> spawns at (<paramref name="x"/>, <paramref name="y"/>) in world units.
    /// </summary>
    /// <returns>Whether the line was a known cheat code.</returns>
    public static bool TryParse(string text, Ruleset ruleset, CommandList into, int x = 0, int y = 0)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(into);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] words = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string code = words[0].ToUpperInvariant();
        CheatCode? cheat = code switch
        {
            "HMREVEAL" => CheatCode.Reveal,
            "HMNOFOG" => CheatCode.NoFog,
            "HMNOTECH" => CheatCode.NoTech,
            "HMGOD" => CheatCode.God,
            "HMBUILDOZER" => CheatCode.Buildozer,
            "HMDVALEVA" => CheatCode.SmallResources,
            "HMPETLEVA" => CheatCode.LargeResources,
            "HMNEXT" => CheatCode.Next,
            _ => null,
        };
        if (cheat is { } c)
        {
            into.Add(CommandKind.Cheat, arg0: (int)c);
            return true;
        }

        if (code != "HMSPAWN" || words.Length < 2)
        {
            return false;
        }

        int type = FindType(ruleset, words[1]);
        int count = 1;
        if (type < 0 || (words.Length > 2 && !int.TryParse(words[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out count)))
        {
            return false; // 1.10 fix: an unknown unit ("hmspawn magic") is ignored, never a crash
        }

        into.Add(CommandKind.DebugSpawn, x: x, y: y, arg0: type, arg1: Math.Clamp(count, 1, CommandSystem.MaxDebugSpawn));
        return true;
    }

    // An exact key (euro.knight) or a class name (knight): the first type whose key ends with ".name".
    private static int FindType(Ruleset ruleset, string name)
    {
        int exact = ruleset.EntityTypeId(name);
        if (exact >= 0)
        {
            return ruleset.EntityTypes[exact].IsMobile ? exact : -1;
        }

        foreach (EntityTypeDefinition t in ruleset.EntityTypes)
        {
            if (t.IsMobile && t.Key.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))
            {
                return t.Id;
            }
        }

        return -1;
    }
}
