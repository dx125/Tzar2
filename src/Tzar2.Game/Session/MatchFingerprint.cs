using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Tzar2.Content.Compiled;
using Tzar2.Core.Hashing;
using Tzar2.Replay;

namespace Tzar2.Game.Session;

/// <summary>A peer's match does not match the local one (refused at match start, REQ-NET-05).</summary>
public sealed class MatchMismatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MatchMismatchException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public MatchMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public MatchMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// What every peer of a match must agree on before the first tick (ADR 0073; REQ-NET-05 lobby checks): the ruleset
/// and map content hashes, the lobby settings, and the AI program of every computer kingdom. Two peers whose resolved
/// AI scripts differ (another package, a fallback to the engine default) are refused here instead of desyncing at the
/// first full-state hash.
/// </summary>
/// <param name="RulesetHash">Ruleset content hash.</param>
/// <param name="MapHash">Map content hash.</param>
/// <param name="SettingsHash">Hash of the lobby settings and kingdoms.</param>
/// <param name="AiPrograms">The AI program of every computer kingdom, ascending by slot.</param>
public sealed record MatchFingerprint(ulong RulesetHash, ulong MapHash, ulong SettingsHash, ImmutableArray<ReplayAiProgram> AiPrograms)
{
    /// <summary>All parts in one value (lobby display, quick comparison).</summary>
    public ulong Value
    {
        get
        {
            var h = new StateHasher();
            h.Add(RulesetHash).Add(MapHash).Add(SettingsHash).Add(AiPrograms.Length);
            foreach (ReplayAiProgram p in AiPrograms)
            {
                h.Add(p.Slot).Add(p.ProgramHash);
            }

            return h.GetCurrentHash();
        }
    }

    /// <summary>The fingerprint of a match.</summary>
    public static MatchFingerprint Of(MatchSetup setup, Ruleset ruleset, MapData map)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(map);
        var h = new StateHasher();
        h.Add(Encoding.UTF8.GetBytes(setup.Ruleset)).Add(setup.Seed).Add(setup.StartingResources).Add(setup.PopulationLimit)
            .Add((int)setup.Victory).Add((int)setup.GameType).Add(setup.ScoreLimit).Add(setup.TimeLimitMinutes)
            .Add(setup.NoFog ? 1 : 0).Add(setup.Explored ? 1 : 0).Add(setup.DayNight ? 1 : 0).Add(setup.Weather ? 1 : 0)
            .Add(setup.Teams).Add(setup.CheatsAllowed ? 1 : 0).Add(setup.Players.Count);
        foreach (PlayerSetup p in setup.Players)
        {
            h.Add(p.Slot).Add(Encoding.UTF8.GetBytes(p.Civilization)).Add((int)p.Controller).Add((int)p.Difficulty);
        }

        return new MatchFingerprint(ruleset.ContentHash, map.ContentHash, h.GetCurrentHash(), setup.AiPrograms);
    }

    /// <summary>Refuses a peer whose match differs, naming the first difference.</summary>
    /// <exception cref="MatchMismatchException">The peer's match differs.</exception>
    public void EnsureCompatible(MatchFingerprint peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (peer.RulesetHash != RulesetHash)
        {
            throw new MatchMismatchException($"The peer's ruleset differs ({Hex(peer.RulesetHash)} vs {Hex(RulesetHash)}).");
        }

        if (peer.MapHash != MapHash)
        {
            throw new MatchMismatchException($"The peer's map differs ({Hex(peer.MapHash)} vs {Hex(MapHash)}).");
        }

        if (peer.SettingsHash != SettingsHash)
        {
            throw new MatchMismatchException("The peer's match settings differ.");
        }

        if (peer.AiPrograms.Length != AiPrograms.Length)
        {
            throw new MatchMismatchException($"The peer has {peer.AiPrograms.Length} computer kingdoms, this match {AiPrograms.Length}.");
        }

        for (int i = 0; i < AiPrograms.Length; i++)
        {
            if (peer.AiPrograms[i] != AiPrograms[i])
            {
                throw new MatchMismatchException(
                    $"The AI script of kingdom {AiPrograms[i].Slot + 1} differs ({Hex(peer.AiPrograms[i].ProgramHash)} vs {Hex(AiPrograms[i].ProgramHash)}): "
                    + "every peer needs the same AI scripts (the same imported package, or the engine default everywhere).");
            }
        }
    }

    private static string Hex(ulong value) => value.ToString("X16", CultureInfo.InvariantCulture);
}
