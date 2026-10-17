using System;
using System.IO;
using Tzar2.Core.Ids;
using Tzar2.Core.Numerics;
using Tzar2.Sim;
using Tzar2.Sim.Commands;

namespace Tzar2.Replay;

/// <summary>Outcome of <see cref="ReplayVerifier.Verify"/>.</summary>
/// <param name="TicksRun">Ticks simulated.</param>
/// <param name="HashesChecked">Stored hashes compared.</param>
/// <param name="FirstMismatch">The first tick whose hash differed, or <c>null</c> if every hash matched.</param>
/// <param name="FinalStateHash">The per-tick hash after the last simulated tick.</param>
/// <param name="OverflowModeMatches">
/// Whether this build's <see cref="OverflowPolicy.Mode"/> equals the recording build's (ADR 0014). A mismatch only
/// matters if an overflow happens; then a Debug build throws where the Release recording saturated.
/// </param>
public readonly record struct ReplayVerification(int TicksRun, int HashesChecked, Tick? FirstMismatch, ulong FinalStateHash, bool OverflowModeMatches)
{
    /// <summary>Whether every stored hash matched.</summary>
    public bool Succeeded => FirstMismatch is null;
}

/// <summary>Headless replay verification and seeking (<c>t2 replay verify</c>, §19.4).</summary>
public static class ReplayVerifier
{
    /// <summary>
    /// Re-simulates <paramref name="replay"/> on <paramref name="simulation"/> (which must be at a tick of the replay,
    /// created from the same ruleset, map and config) and compares every stored hash.
    /// </summary>
    /// <param name="replay">The replay.</param>
    /// <param name="simulation">A simulation at tick 0, or restored from one of the replay's keyframes.</param>
    /// <param name="untilTick">Stop before this tick (exclusive), at least the simulation's tick; <c>null</c> = to the end.</param>
    /// <exception cref="ArgumentException">The simulation does not match the replay header.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The simulation's tick or <paramref name="untilTick"/> is outside the replay.</exception>
    public static ReplayVerification Verify(ReplayFile replay, Simulation simulation, Tick? untilTick = null)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(simulation);
        if (simulation.RulesetHash != replay.Header.RulesetHash || simulation.MapHash != replay.Header.MapHash
            || simulation.World.Config != replay.Header.Config)
        {
            throw new ArgumentException("The simulation was not created with the replay's ruleset, map and configuration.", nameof(simulation));
        }

        int start = simulation.World.Tick.Value;
        if (start > replay.Ticks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(simulation), $"The simulation is at tick {start}, past the replay's {replay.Ticks.Count} ticks.");
        }

        int end = untilTick?.Value ?? replay.Ticks.Count;
        if (end < start || end > replay.Ticks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(untilTick), $"Tick {end} is not in [{start}, {replay.Ticks.Count}].");
        }

        bool overflowMatches = replay.Header.OverflowMode == OverflowPolicy.Mode;
        var commands = new CommandList();
        int checkedCount = 0;
        ulong last = simulation.ComputeStateHash();
        for (int t = start; t < end; t++)
        {
            ReplayTick record = replay.Ticks[t];
            replay.ReadCommands(t, commands);
            TickResult result = simulation.Tick(commands);
            last = result.StateHash;
            bool mismatch = false;
            if (record.StateHash is { } expected)
            {
                checkedCount++;
                mismatch |= expected != result.StateHash;
            }

            if (record.FullHash is { } expectedFull)
            {
                checkedCount++;
                mismatch |= expectedFull != result.FullHash;
            }

            if (mismatch)
            {
                return new ReplayVerification(t - start + 1, checkedCount, record.Tick, last, overflowMatches);
            }
        }

        return new ReplayVerification(end - start, checkedCount, null, last, overflowMatches);
    }

    /// <summary>
    /// Seeks to the state before <paramref name="tick"/>: restores the nearest keyframe at or before it with
    /// <paramref name="restore"/> (or starts from <paramref name="start"/> if there is none) and fast-forwards,
    /// verifying hashes on the way.
    /// </summary>
    /// <param name="replay">The replay.</param>
    /// <param name="tick">Target tick, 0 to the replay's tick count.</param>
    /// <param name="start">Creates a simulation at tick 0.</param>
    /// <param name="restore">Restores a simulation from snapshot bytes (the caller supplies ruleset, map and workers).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tick"/> is past the end of the replay.</exception>
    /// <exception cref="InvalidDataException">A keyframe's snapshot is not at the keyframe's tick.</exception>
    /// <exception cref="InvalidOperationException">The replay desyncs before <paramref name="tick"/>.</exception>
    public static Simulation SeekTo(ReplayFile replay, Tick tick, Func<Simulation> start, Func<ReadOnlyMemory<byte>, Simulation> restore)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(restore);
        if (tick.Value > replay.Ticks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), $"Tick {tick.Value} is past the replay's {replay.Ticks.Count} ticks.");
        }

        ReplayKeyframe? keyframe = replay.KeyframeAtOrBefore(tick);
        Simulation simulation = keyframe is { } k ? restore(k.Snapshot) : start();
        try
        {
            Tick expectedStart = keyframe?.Tick ?? Tick.Zero;
            if (simulation.World.Tick != expectedStart)
            {
                throw new InvalidDataException($"The keyframe for tick {expectedStart.Value} holds the state of tick {simulation.World.Tick.Value}.");
            }

            ReplayVerification result = Verify(replay, simulation, tick);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Replay desynced at tick {result.FirstMismatch!.Value.Value} while seeking.");
            }

            return simulation;
        }
        catch
        {
            simulation.Dispose();
            throw;
        }
    }
}
