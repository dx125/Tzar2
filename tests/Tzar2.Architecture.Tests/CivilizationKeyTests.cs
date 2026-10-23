using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Tzar2.Architecture.Tests;

/// <summary>
/// Code review 2026-10 (item 16, for a fork): which civilizations exist, which type is a kingdom's seat and what a type
/// is called are the ruleset's (ADR 0211; <c>Ruleset.CanFoundKingdom</c>, the <c>Seat</c> trait). Code that spells a
/// civilization key (<c>"euro"</c>, <c>"arab.castle"</c>), an original race name (<c>"European"</c>) or a type by its
/// key suffix (<c>".castle"</c>, <c>".peon"</c>) is what a fork with a fourth civilization trips over.
/// </summary>
public class CivilizationKeyTests
{
    // The files that still spell them, with how many lines each: a ratchet. A new file, or more lines in a listed one,
    // fails; fewer lines fail too, until the count here is lowered, so the list only shrinks.
    private static readonly Dictionary<string, int> Known = new(StringComparer.Ordinal)
    {
        // Translators of the original's race names (its AI scripts and scenario files say "european", "Arabian"...).
        ["src/Tzar2.AI/Legacy/Syntax/AiParser.cs"] = 1,
        ["src/Tzar2.AI/Runtime/AiRulesetModel.cs"] = 3,
        ["src/Tzar2.Game/Ui/OnlineHelp.cs"] = 3, // the help's pages per race (E, R, A)

        // Test, bench and demo scenarios that build their maps by key.
        ["src/Tzar2.AI/Scenarios/AiSkirmishScenario.cs"] = 3,
        ["src/Tzar2.Sim/Scenarios/GameplayScenario.cs"] = 20,
        ["src/Tzar2.Sim/Scenarios/GameplayScenario.CombatMagic.cs"] = 4,
        ["src/Tzar2.Game/Maps/SkirmishTestMap.cs"] = 3,
        ["src/Tzar2.Game/Campaigns/DemoCampaign.cs"] = 14,
        ["src/Tzar2.Game/Campaigns/CampaignAutoplay.cs"] = 1,
        ["src/Tzar2.Client/src/Editor/EditorSmoke.cs"] = 2,
        ["src/Tzar2.Client/src/Game/AutoplayDriver.cs"] = 1,
        ["src/Tzar2.Client/src/Game/GameApp.MultiplayerSmoke.cs"] = 1,
        ["src/Tzar2.Client/src/Game/GameApp.UiSmoke.cs"] = 1,

        // Defaults and fallbacks with no ruleset at hand, and the client's per-civilization art (review doc: remaining).
        ["src/Tzar2.Game/Session/MatchDescriptor.cs"] = 1,
        ["src/Tzar2.Game/Ui/RandomMapScreenModel.cs"] = 1,
        ["src/Tzar2.Game/Ui/CustomMapScreenModel.cs"] = 1,
        ["src/Tzar2.Client/src/Game/Screens.cs"] = 1,
        ["src/Tzar2.Client/src/Game/GameApp.cs"] = 1,
        ["src/Tzar2.Client/src/Game/DiplomacyScreens.cs"] = 1,
        ["src/Tzar2.Client/src/Logic/PresentationCatalog.cs"] = 1,
    };

    private static readonly Regex Spelling = new(
        "\"(?:(?:euro|arab|asia)(?:\\.[a-z_0-9]*)?|\\.(?:castle|peon)|european|arabian|asian)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Sim_game_ai_and_client_do_not_spell_civilization_keys_beyond_the_known_list()
    {
        var found = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string project in new[] { "Tzar2.Sim", "Tzar2.Game", "Tzar2.AI", Path.Combine("Tzar2.Client", "src") })
        {
            string dir = Path.Combine(RepositoryPaths.Root, "src", project);
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string sep = Path.DirectorySeparatorChar.ToString();
                if (file.Contains(sep + "obj" + sep, StringComparison.Ordinal) || file.Contains(sep + "bin" + sep, StringComparison.Ordinal))
                {
                    continue;
                }

                int lines = File.ReadLines(file).Count(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && Spelling.IsMatch(l));
                if (lines > 0)
                {
                    found[Path.GetRelativePath(RepositoryPaths.Root, file).Replace('\\', '/')] = lines;
                }
            }
        }

        var problems = new List<string>();
        foreach ((string file, int lines) in found)
        {
            if (!Known.TryGetValue(file, out int known))
            {
                problems.Add($"{file}: {lines} line(s) spell a civilization or a type key; ask the ruleset (Civilizations, CanFoundKingdom, SeatTypeId, traits)");
            }
            else if (lines != known)
            {
                problems.Add($"{file}: {lines} line(s), the list says {known}; " + (lines > known ? "ask the ruleset instead" : "lower the count in CivilizationKeyTests"));
            }
        }

        problems.AddRange(Known.Keys.Where(k => !found.ContainsKey(k)).Select(k => $"{k}: no longer spells any; remove it from CivilizationKeyTests"));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_pattern_catches_keys_names_and_suffixes_but_not_other_words()
    {
        Assert.Matches(Spelling, "SlotChoice(\"euro\", 0)");
        Assert.Matches(Spelling, "EntityTypeId(\"arab.castle\")");
        Assert.Matches(Spelling, "civ + \".castle\"");
        Assert.Matches(Spelling, "\"European\" => \"euro\"");
        Assert.DoesNotMatch(Spelling, "\"europe\"");
        Assert.DoesNotMatch(Spelling, "\"comm.res_gold\"");
        Assert.DoesNotMatch(Spelling, "\"castle\"");
    }
}
