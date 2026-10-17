using System;
using Tzar2.Sim.Commands;

namespace Tzar2.Sim.Scenarios;

/// <summary>A deterministic scenario that drives a <see cref="Simulation"/> tick by tick (benchmarks, determinism CI).</summary>
public interface IBenchScenario : IDisposable
{
    /// <summary>The simulation.</summary>
    Simulation Simulation { get; }

    /// <summary>The command set of the last generated tick.</summary>
    CommandList LastCommands { get; }

    /// <summary>Generates the next tick's command set into <see cref="LastCommands"/> without running it.</summary>
    void GenerateCommands();

    /// <summary>Updates the scenario's metrics from the state after a tick (call after every tick).</summary>
    void AfterTick();

    /// <summary>Generates the next tick's commands, runs the tick and calls <see cref="AfterTick"/>.</summary>
    TickResult RunTick();

    /// <summary>One line of scenario-specific results for reports (e.g. planning latency, crossings).</summary>
    string Describe();
}
