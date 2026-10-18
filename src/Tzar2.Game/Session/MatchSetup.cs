using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Sim;

namespace Tzar2.Game.Session;

/// <summary>Who issues a slot's commands.</summary>
public enum ControllerKind
{
    /// <summary>The local player (input → commands).</summary>
    Human = 0,

    /// <summary>A computer kingdom run by the legacy AI (ADR 0051, 0073).</summary>
    Computer = 1,
}

/// <summary>Computer difficulty (lobby DIFFICULTY LEVEL: Easy / Normal / Hard).</summary>
public enum Difficulty
{
    /// <summary>Easy.</summary>
    Easy = 0,

    /// <summary>Normal.</summary>
    Normal = 1,

    /// <summary>Hard.</summary>
    Hard = 2,
}

/// <summary>One kingdom of a match.</summary>
/// <param name="Slot">Player slot 0-7 (also the colour ramp, COLOR BARS order).</param>
/// <param name="Civilization">Civilization key of the ruleset (<c>euro</c>, <c>asia</c>, <c>arab</c>).</param>
/// <param name="Controller">Human or computer.</param>
/// <param name="Difficulty">Computer difficulty.</param>
public sealed record PlayerSetup(byte Slot, string Civilization, ControllerKind Controller, Difficulty Difficulty = Difficulty.Normal);

/// <summary>
/// A single-player skirmish (TDD §7.2 <c>Tzar2.Game</c>: match setup): the ruleset, the map, the kingdoms and the lobby
/// settings that feed <see cref="Sim.SimulationConfig"/>.
/// </summary>
public sealed record MatchSetup
{
    /// <summary>The ruleset name (<c>classic</c> by default, DEC-06).</summary>
    public string Ruleset { get; init; } = "classic";

    /// <summary>Where the map comes from.</summary>
    public required IMapSource Map { get; init; }

    /// <summary>The kingdoms, by ascending slot; slots must be 0..n-1.</summary>
    public required IReadOnlyList<PlayerSetup> Players { get; init; }

    /// <summary>The match seed (map layout of generated maps, AI and all sim RNG streams).</summary>
    public ulong Seed { get; init; } = 0x7A42_0003UL;

    /// <summary>Starting-resource level index (lobby STARTING RESOURCES), or -1 for the ruleset default.</summary>
    public int StartingResources { get; init; } = -1;

    /// <summary>Population limit (lobby POPULATION LIMIT), or -1 for the ruleset default.</summary>
    public int PopulationLimit { get; init; } = -1;

    /// <summary>Victory condition (GAME-17; the skirmish default is Standard).</summary>
    public VictoryCondition Victory { get; init; } = VictoryCondition.Standard;

    /// <summary>Game type (GAME-17).</summary>
    public GameType GameType { get; init; } = GameType.Standard;

    /// <summary>Score limit of <see cref="VictoryCondition.ScoreLimit"/>.</summary>
    public int ScoreLimit { get; init; } = 10_000;

    /// <summary>Time limit (minutes) of <see cref="VictoryCondition.TimeLimit"/>.</summary>
    public int TimeLimitMinutes { get; init; } = 60;

    /// <summary>No Fog (GAME-12).</summary>
    public bool NoFog { get; init; }

    /// <summary>Explore: the map starts explored (GAME-12).</summary>
    public bool Explored { get; init; }

    /// <summary>Day/night cycle (GAME-16).</summary>
    public bool DayNight { get; init; } = true;

    /// <summary>Weather (GAME-16).</summary>
    public bool Weather { get; init; } = true;

    /// <summary>Teams, 4 bits per slot (GAME-13; see <see cref="SimulationConfig.Teams"/>).</summary>
    public ulong Teams { get; init; }

    /// <summary>Cheats (the <c>hm*</c> codes) allowed: a sandbox or a lobby where every player agreed (GAME-19).</summary>
    public bool CheatsAllowed { get; init; }

    /// <summary>The script of the computer kingdoms (<see cref="SkirmishAi.Resolve"/>; part of the match fingerprint).</summary>
    public SkirmishAi Ai { get; init; } = SkirmishAi.EngineDefault;

    /// <summary>Whether computer kingdoms run the AI (false: they stay idle — tests that need a passive opponent).</summary>
    public bool ComputerAi { get; init; } = true;

    /// <summary>
    /// The scenario of a converted or authored map (legacy triggers, conversations, quests; ADR 0120, ADR 0130), or null
    /// for a plain skirmish. Its <see cref="Content.Compiled.Scenario.ScenarioData.ObjectCount"/> must match the map.
    /// </summary>
    public Content.Compiled.Scenario.ScenarioData? Scenario { get; init; }

    /// <summary>
    /// Match-start changes beyond the lobby settings (scenario kingdoms' stockpiles and diplomacy, campaign carry-over;
    /// ADR 0133), run once for a new match after the AI and the triggers attached and before the first tick; never on
    /// restore (the snapshot has their result). Null for none.
    /// </summary>
    public IMatchPreparation? Preparation { get; init; }

    /// <summary>Trigger engine settings (null: the defaults with the human kingdoms as responders).</summary>
    public Scripting.Legacy.TriggerOptions? TriggerOptions { get; init; }

    /// <summary>The trigger settings the match runs with.</summary>
    public Scripting.Legacy.TriggerOptions EffectiveTriggerOptions => TriggerOptions ?? Scripting.Legacy.TriggerOptions.Default with { ResponderMask = HumanMask != 0 ? HumanMask : 0xFF };

    /// <summary>The kingdoms (bit = slot) that answer conversations: the human ones.</summary>
    public uint HumanMask
    {
        get
        {
            uint mask = 0;
            foreach (PlayerSetup p in Players)
            {
                mask |= p.Controller == ControllerKind.Human ? 1u << p.Slot : 0;
            }

            return mask;
        }
    }

    /// <summary>The AI program of every computer kingdom, ascending by slot (match fingerprint, replay header).</summary>
    public System.Collections.Immutable.ImmutableArray<Replay.ReplayAiProgram> AiPrograms
    {
        get
        {
            var list = System.Collections.Immutable.ImmutableArray.CreateBuilder<Replay.ReplayAiProgram>();
            if (ComputerAi)
            {
                foreach (PlayerSetup p in Players)
                {
                    if (p.Controller == ControllerKind.Computer)
                    {
                        list.Add(new Replay.ReplayAiProgram(p.Slot, Ai.ContentHash));
                    }
                }
            }

            list.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            return list.ToImmutable();
        }
    }

    /// <summary>The slot the local player controls.</summary>
    public byte LocalPlayer
    {
        get
        {
            foreach (PlayerSetup p in Players)
            {
                if (p.Controller == ControllerKind.Human)
                {
                    return p.Slot;
                }
            }

            return 0;
        }
    }

    /// <summary>Checks slots and civilizations against <paramref name="ruleset"/>.</summary>
    /// <exception cref="ArgumentException">The players are invalid.</exception>
    public void Validate(Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        if (Players.Count < 1 || Players.Count > ruleset.MaxPlayers)
        {
            throw new ArgumentException($"A match needs 1 to {ruleset.MaxPlayers} players.", nameof(ruleset));
        }

        int humans = 0;
        for (int i = 0; i < Players.Count; i++)
        {
            if (Players[i].Slot != i)
            {
                throw new ArgumentException($"Player {i} has slot {Players[i].Slot}; slots must be 0..n-1 in order.", nameof(ruleset));
            }

            if (ruleset.EntityTypeId(Players[i].Civilization + ".castle") < 0)
            {
                throw new ArgumentException($"Civilization '{Players[i].Civilization}' has no castle in ruleset '{ruleset.Name}'.", nameof(ruleset));
            }

            humans += Players[i].Controller == ControllerKind.Human ? 1 : 0;
        }

        if (humans > 1)
        {
            throw new ArgumentException("A local skirmish has at most one human player.", nameof(ruleset));
        }
    }
}

/// <summary>Changes a new match before its first tick (ADR 0133).</summary>
public interface IMatchPreparation
{
    /// <summary>Applies the changes; the world is still unlocked (match setup).</summary>
    /// <param name="simulation">The new match.</param>
    /// <param name="triggers">The scenario's trigger engine, or null.</param>
    void Prepare(Simulation simulation, Scripting.Legacy.LegacyTriggerSystem? triggers);
}

/// <summary>A map built elsewhere (a converted or authored scenario map, a campaign mission; ADR 0132).</summary>
/// <param name="Name">Display name.</param>
/// <param name="Map">The map.</param>
public sealed record FixedMapSource(string Name, MapData Map) : IMapSource
{
    /// <inheritdoc />
    public MapData Create(Ruleset ruleset, MatchSetup setup) => Map;
}

/// <summary>
/// The source of a match's map (thin integration point, ADR 0070): today the generated skirmish test map; the random
/// map generator and converted legacy maps plug in here later.
/// </summary>
public interface IMapSource
{
    /// <summary>Display name.</summary>
    string Name { get; }

    /// <summary>Builds the map for <paramref name="setup"/> (start objects of every kingdom included).</summary>
    MapData Create(Ruleset ruleset, MatchSetup setup);
}
