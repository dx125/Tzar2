using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Content.Rules;
using Tzar2.Core.Ids;
using Tzar2.Scripting.Legacy;
using Tzar2.Sim;
using Tzar2.Sim.Commands;
using Tzar2.Sim.Events;
using Tzar2.Sim.Scenarios;

using Tzar2.Tests.Scenario;

namespace Tzar2.Scripting.Tests;

/// <summary>Starts a synthetic scenario with the trigger engine.</summary>
internal static class ScenarioStart
{
    public static TriggerDriver Start(this ScenarioBuilder b, int players = 2, TriggerOptions? options = null, Func<SimulationConfig, SimulationConfig>? configure = null)
        => new(b.Map(), b.Scenario(), players, options, configure);
}


/// <summary>A simulation with the trigger engine attached, stepped with per-player commands; records events.</summary>
internal sealed class TriggerDriver : IDisposable
{
    private readonly CommandList[] _pending;
    private readonly CommandList _tickSet = new();

    public TriggerDriver(MapData map, ScenarioData scenario, int players = 2, TriggerOptions? options = null, Func<SimulationConfig, SimulationConfig>? configure = null, int workers = 1)
    {
        var config = new SimulationConfig { MatchSeed = 11, PlayerCount = players, CheatsAllowed = true, Victory = Tzar2.Sim.VictoryCondition.NotSpecified };
        config = configure?.Invoke(config) ?? config;
        Map = map;
        ScenarioData = scenario;
        Options = options ?? TriggerOptions.Default;
        Sim = new Simulation(ScenarioBuilder.Classic, map, config, workers);
        Triggers = LegacyTriggerSystem.Attach(Sim, scenario, Options);
        _pending = new CommandList[players];
        for (int i = 0; i < players; i++)
        {
            _pending[i] = new CommandList();
        }
    }

    private TriggerDriver(Simulation sim, LegacyTriggerSystem triggers, MapData map, ScenarioData scenario, TriggerOptions options)
    {
        Sim = sim;
        Triggers = triggers;
        Map = map;
        ScenarioData = scenario;
        Options = options;
        _pending = new CommandList[sim.World.Players.Length];
        for (int i = 0; i < _pending.Length; i++)
        {
            _pending[i] = new CommandList();
        }
    }

    public MapData Map { get; }

    public ScenarioData ScenarioData { get; }

    public TriggerOptions Options { get; }

    public Simulation Sim { get; }

    public LegacyTriggerSystem Triggers { get; }

    public World World => Sim.World;

    public TriggerState State => Triggers.State;

    public List<(int Tick, SimEvent Event)> Events { get; } = [];

    public List<ulong> Hashes { get; } = [];

    /// <summary>The live entity of map object <paramref name="index"/>.</summary>
    public EntityId Object(int index) => EntityId.FromValue(State.MapEntities[index]);

    public int Variable(string name) => State.Variables[ScenarioData.Variables.Items.IndexOf(name)];

    public void Order(int player, CommandKind kind, ReadOnlySpan<EntityId> units, int x = 0, int y = 0, EntityId target = default, int arg0 = 0, int arg1 = 0)
        => _pending[player].Add(kind, units, x, y, target, arg0, arg1);

    public void Reply(int player, int choice) => Order(player, CommandKind.ScenarioReply, [], arg0: State.ConversationSerial, arg1: choice);

    public TickResult Step()
    {
        _tickSet.Clear();
        for (int p = 0; p < _pending.Length; p++)
        {
            _tickSet.AppendStamped((byte)p, _pending[p]);
            _pending[p].Clear();
        }

        int tick = World.Tick.Value;
        TickResult result = Sim.Tick(_tickSet);
        foreach (SimEvent e in World.Events.Events)
        {
            Events.Add((tick, e));
        }

        Hashes.Add(result.StateHash);
        return result;
    }

    public void Run(int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            Step();
        }
    }

    /// <summary>Runs whole rule passes (20 ticks each at the defaults).</summary>
    public void Seconds(int seconds) => Run(seconds * 1000 / World.Config.TickLengthMilliseconds);

    public int Count(SimEventKind kind) => Events.Count(e => e.Event.Kind == kind);

    public IEnumerable<SimEvent> Of(SimEventKind kind) => Events.Where(e => e.Event.Kind == kind).Select(e => e.Event);

    /// <summary>Snapshot, restore into a fresh simulation with the engine re-attached.</summary>
    public TriggerDriver Restore(int workers = 1)
    {
        byte[] snapshot = Sim.CaptureSnapshot();
        Simulation sim = Simulation.Restore(snapshot, ScenarioBuilder.Classic, Map, workers);
        LegacyTriggerSystem triggers = LegacyTriggerSystem.Attach(sim, ScenarioData, Options);
        return new TriggerDriver(sim, triggers, Map, ScenarioData, Options);
    }

    public void Dispose() => Sim.Dispose();
}
