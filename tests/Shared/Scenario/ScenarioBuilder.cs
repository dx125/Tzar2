using System;
using System.Collections.Generic;
using System.Linq;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Content.Rules;
using Tzar2.Sim.Scenarios;

namespace Tzar2.Tests.Scenario;

/// <summary>
/// Builds synthetic scenarios (invented names, texts and values; REQ-LEGAL-03) on the synthetic classic ruleset and
/// drives them tick by tick.
/// </summary>
public sealed class ScenarioBuilder : IExpressionScope
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

}
