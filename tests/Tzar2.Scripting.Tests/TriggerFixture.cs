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

namespace Tzar2.Scripting.Tests;

/// <summary>
/// Builds synthetic scenarios (invented names, texts and values; REQ-LEGAL-03) on the synthetic classic ruleset and
/// drives them tick by tick.
/// </summary>
internal sealed class ScenarioBuilder : IExpressionScope
{
    public const int Tile = 2048;

    private readonly List<string> _variables = [];
    private readonly List<EventDef> _events = [];
    private readonly List<ActionDef> _actions = [];
    private readonly List<RuleDef> _rules = [];
    private readonly List<ConversationDef> _conversations = [];
    private readonly List<MapObject> _objects = [];
    private readonly List<ObjectInfo> _infos = [];

    public static Ruleset Classic { get; } = SyntheticLegacyContent.CompileRuleset();

    public int Width { get; set; } = 48;

    public int Height { get; set; } = 32;

    /// <summary>Places a unit at the centre of a tile; returns its object index.</summary>
    public int Unit(string type, int owner, int tileX, int tileY, ObjectInfo? info = null)
    {
        _objects.Add(new MapObject(type, (byte)owner, (tileX * Tile) + (Tile / 2), (tileY * Tile) + (Tile / 2)));
        _infos.Add(info ?? ObjectInfo.Plain);
        return _objects.Count - 1;
    }

    /// <summary>Places a structure with its footprint's top-left at a tile.</summary>
    public int Structure(string type, int owner, int tileX, int tileY, ObjectInfo? info = null)
    {
        _objects.Add(MapObject.AtTile(type, (byte)owner, tileX, tileY));
        _infos.Add(info ?? ObjectInfo.Plain);
        return _objects.Count - 1;
    }

    public int Event(EventDef e)
    {
        _events.Add(e);
        return _events.Count - 1;
    }

    public int Action(ActionDef a)
    {
        _actions.Add(a);
        return _actions.Count - 1;
    }

    public int Rule(string condition, params int[] actions)
    {
        _rules.Add(new RuleDef("rule" + _rules.Count, Expr(condition), actions));
        return _rules.Count - 1;
    }

    public int Conversation(params PhraseDef[] phrases)
    {
        _conversations.Add(new ConversationDef("talk" + _conversations.Count, phrases));
        return _conversations.Count - 1;
    }

    public static PhraseDef Phrase(ScenarioBuilder b, string name, bool initial, string text, string condition = "", string result = "", int? source = null, params int[] followUps)
        => new(name, initial, source, b.Expr(condition), b.Expr(result), text, null, null, followUps);

    public ExpressionDef Expr(string text) => ExpressionParser.Parse(text, this);

    public static NamedObjects All(params int[] objects) => new(objects, Quantifier.All);

    public static NamedObjects Any(params int[] objects) => new(objects, Quantifier.Any);

    public static TypedObjects Typed(string type, int legacyPlayer, CountComparison comparison, int count, Quantifier quantifier = Quantifier.All)
        => new(type, PlayerMasks.Of(legacyPlayer), comparison, count, quantifier);

    public static RectArea Rect(int left, int top, int right, int bottom) => new(left, top, right, bottom);

    public int UserVariable(string name)
    {
        int i = _variables.FindIndex(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            return i;
        }

        _variables.Add(name);
        return _variables.Count - 1;
    }

    public int Event(string name) => _events.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public int Timer(string name) => _actions.FindIndex(a => a is SetTimerAction && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public MapData Map()
        => ScenarioMaps.WithObjects(ScenarioMaps.Build("scenario-test", Width, Height, new byte[Width * Height], []), [.. _objects]);

    public ScenarioData Scenario()
        => new(
            new MapProperties("Synthetic", "tests", "invented", Content.Compiled.Scenario.GameType.Standard, Content.Compiled.Scenario.VictoryCondition.Standard, 0, 1, false, false),
            _objects.Count,
            _infos.Any(i => i != ObjectInfo.Plain) ? (EquatableArray<ObjectInfo>)_infos.ToArray() : EquatableArray<ObjectInfo>.Empty,
            EquatableArray<KingdomDef>.Empty,
            EquatableArray<DiplomacyDef>.Empty,
            _variables.ToArray(),
            _events.ToArray(),
            _actions.ToArray(),
            _rules.ToArray(),
            _conversations.ToArray(),
            EquatableArray<CustomSoundDef>.Empty,
            EquatableArray<ResourceRef>.Empty);

    public TriggerDriver Start(int players = 2, TriggerOptions? options = null, Func<SimulationConfig, SimulationConfig>? configure = null)
        => new(Map(), Scenario(), players, options, configure);
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
