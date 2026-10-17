using System;
using Tzar2.Core.Ids;
using Tzar2.Sim;

namespace Tzar2.Replay;

/// <summary>Outcome of <see cref="ReplayVerifier.Verify"/>.</summary>
/// <param name="TicksRun">Ticks simulated.</param>
/// <param name="HashesChecked">Stored hashes compared.</param>
/// <param name="FirstMismatch">The first tick whose hash differed, or <c>null</c> if every hash matched.</param>
/// <param name="FinalStateHash">The per-tick hash after the last simulated tick.</param>
public readonly record struct ReplayVerification(int TicksRun, int HashesChecked, Tick? FirstMismatch, ulong FinalStateHash)
{
    /// <summary>Whether every stored hash matched.</summary>
    public bool Succeeded => FirstMismatch is null;
}

/// <summary>Headless replay verification and seeking (<c>t2 replay verify</c>, §19.4).</summary>
public static class ReplayVerifier
{
    /// <summary>
    /// Re-simulates <paramref name="replay"/> on <paramref name="simulation"/> (which must be at the replay's first
    /// tick, created from the same ruleset, map and config) and compares every stored hash.
    /// </summary>
    /// <param name="replay">The replay.</param>
    /// <param name="simulation">A simulation at tick 0, or restored from one of the replay's keyframes.</param>
    /// <param name="untilTick">Stop before this tick (exclusive); <c>null</c> = to the end.</param>
    /// <exception cref="ArgumentException">The simulation does not match the replay header.</exception>
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
        int end = Math.Min(replay.Ticks.Count, untilTick?.Value ?? int.MaxValue);
        int checkedCount = 0;
        Tick? mismatch = null;
        ulong last = simulation.ComputeStateHash();
        for (int t = start; t < end; t++)
        {
            ReplayTick record = replay.Ticks[t];
            TickResult result = simulation.Tick(record.Commands);
            last = result.StateHash;
            if (record.StateHash is { } expected)
            {
                checkedCount++;
                if (expected != result.StateHash && mismatch is null)
                {
                    mismatch = record.Tick;
                }
            }

            if (record.FullHash is { } expectedFull)
            {
                checkedCount++;
                if (expectedFull != result.FullHash && mismatch is null)
                {
                    mismatch = record.Tick;
                }
            }

            if (mismatch is not null)
            {
                return new ReplayVerification(t - start + 1, checkedCount, mismatch, last);
            }
        }

        return new ReplayVerification(end - start, checkedCount, null, last);
    }

    /// <summary>
    /// Seeks to the state before <paramref name="tick"/>: restores the nearest keyframe at or before it with
    /// <paramref name="restore"/> (or starts from <paramref name="start"/> if there is none) and fast-forwards.
    /// </summary>
    /// <param name="replay">The replay.</param>
    /// <param name="tick">Target tick.</param>
    /// <param name="start">Creates a simulation at tick 0.</param>
    /// <param name="restore">Restores a simulation from snapshot bytes (the caller supplies ruleset, map and workers).</param>
    public static Simulation SeekTo(ReplayFile replay, Tick tick, Func<Simulation> start, Func<byte[], Simulation> restore)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(restore);
        (Tick Tick, byte[] Snapshot)? keyframe = replay.KeyframeAtOrBefore(tick);
        Simulation simulation = keyframe is { } k ? restore(k.Snapshot) : start();
        ReplayVerification result = Verify(replay, simulation, tick);
        if (!result.Succeeded)
        {
            simulation.Dispose();
            throw new InvalidOperationException($"Replay desynced at tick {result.FirstMismatch!.Value.Value} while seeking.");
        }

        return simulation;
    }
}
