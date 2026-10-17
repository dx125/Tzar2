using System;
using System.Collections.Generic;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Threading;

namespace Tzar2.Sim.Systems;

/// <summary>The fixed tick pipeline of TDD §8.5 (steps 1-17; step 18 is done by <see cref="Simulation"/>).</summary>
public enum SimPhase : byte
{
    /// <summary>1. Apply the validated commands for the tick.</summary>
    Commands = 1,

    /// <summary>2. Scripts/triggers.</summary>
    Scripts = 2,

    /// <summary>3. AI think.</summary>
    AI = 3,

    /// <summary>4. Production, research, construction and upgrade timers.</summary>
    Production = 4,

    /// <summary>5. Economy.</summary>
    Economy = 5,

    /// <summary>6. Activities/state machines.</summary>
    Activities = 6,

    /// <summary>7. Path request servicing (count-budgeted).</summary>
    PathRequests = 7,

    /// <summary>8. Movement, avoidance, occupancy update.</summary>
    Movement = 8,

    /// <summary>9. Combat.</summary>
    Combat = 9,

    /// <summary>10. Spells, status effects, auras.</summary>
    Spells = 10,

    /// <summary>11. Experience, level-ups, hero promotion.</summary>
    Experience = 11,

    /// <summary>12. Capture/resistance/influence.</summary>
    Capture = 12,

    /// <summary>13. Holders.</summary>
    Holders = 13,

    /// <summary>14. Visibility/exploration.</summary>
    Visibility = 14,

    /// <summary>15. Day/night clock, weather.</summary>
    Clock = 15,

    /// <summary>16. Victory/defeat evaluation.</summary>
    Victory = 16,

    /// <summary>17. Cleanup (despawn, corpse records, ID recycling).</summary>
    Cleanup = 17,
}

/// <summary>What a system sees while it runs.</summary>
public sealed class SimContext
{
    internal SimContext(World world, DeterministicWorkerPool workers)
    {
        World = world;
        Workers = workers;
    }

    /// <summary>The world.</summary>
    public World World { get; }

    /// <summary>The worker pool for fork/join jobs inside a system.</summary>
    public DeterministicWorkerPool Workers { get; }

    /// <summary>The commands of the current tick (stamped, in execution order).</summary>
    public CommandList Commands { get; internal set; } = new();
}

/// <summary>A simulation system: deterministic code that runs once per tick in its phase.</summary>
public interface ISimSystem
{
    /// <summary>The pipeline step the system belongs to.</summary>
    SimPhase Phase { get; }

    /// <summary>Name for diagnostics and profiling.</summary>
    string Name { get; }

    /// <summary>Runs the system for the current tick.</summary>
    void Execute(SimContext context);
}

/// <summary>
/// Runs systems in a fixed order: by <see cref="SimPhase"/>, then by registration order within a phase. The list is
/// sealed before the first tick so the order cannot change mid-match.
/// </summary>
public sealed class SystemScheduler
{
    private readonly List<ISimSystem> _systems = [];
    private ISimSystem[] _ordered = [];

    /// <summary>Whether systems can still be added.</summary>
    public bool IsSealed { get; private set; }

    /// <summary>The systems in execution order (valid after <see cref="Seal"/>).</summary>
    public IReadOnlyList<ISimSystem> Ordered => _ordered;

    /// <summary>Adds a system.</summary>
    /// <exception cref="InvalidOperationException">The scheduler is sealed.</exception>
    public void Add(ISimSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (IsSealed)
        {
            throw new InvalidOperationException("Systems must be added before the first tick.");
        }

        _systems.Add(system);
    }

    /// <summary>Fixes the order.</summary>
    public void Seal()
    {
        if (IsSealed)
        {
            return;
        }

        // Stable by registration index within a phase.
        var keyed = new (int Phase, int Index, ISimSystem System)[_systems.Count];
        for (int i = 0; i < keyed.Length; i++)
        {
            keyed[i] = ((int)_systems[i].Phase, i, _systems[i]);
        }

        Array.Sort(keyed, static (a, b) => a.Phase != b.Phase ? a.Phase.CompareTo(b.Phase) : a.Index.CompareTo(b.Index));
        _ordered = new ISimSystem[keyed.Length];
        for (int i = 0; i < keyed.Length; i++)
        {
            _ordered[i] = keyed[i].System;
        }

        IsSealed = true;
    }

    /// <summary>Runs every system once.</summary>
    public void RunTick(SimContext context)
    {
        Seal();
        foreach (ISimSystem system in _ordered)
        {
            system.Execute(context);
        }
    }
}
