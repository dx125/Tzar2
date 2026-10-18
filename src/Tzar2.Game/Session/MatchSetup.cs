using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;

namespace Tzar2.Game.Session;

/// <summary>Who issues a slot's commands.</summary>
public enum ControllerKind
{
    /// <summary>The local player (input → commands).</summary>
    Human = 0,

    /// <summary>A computer player (an <see cref="IPlayerController"/>).</summary>
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
