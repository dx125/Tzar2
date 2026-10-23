using System;
using System.Collections.Generic;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;
using Tzar2.Sim.Scenarios;

namespace Tzar2.Game.Campaigns;

/// <summary>
/// A built-in three-map campaign made for the rebuild (invented names, texts and layout; REQ-LEGAL-03). It needs no
/// original files, so the campaign screen, the headless campaign runner and the tests always have a campaign. It uses
/// every campaign feature: an entry map, Load Map and Player Win transitions, a persistent hero, a carried
/// <c>#</c>variable, quests, a story screen, a conversation with a choice, a countdown timer and Not Appear objects.
/// </summary>
public static class DemoCampaign
{
    /// <summary>The campaign id.</summary>
    public const string Id = "tzar2-demo";

    private const int Tile = MapData.UnitsPerTile;

    /// <summary>The campaign as a source.</summary>
    public static InMemoryCampaignSource Create()
    {
        var maps = new Dictionary<string, MissionContent>(StringComparer.Ordinal)
        {
            ["ford"] = Ford(),
            ["mill"] = Mill(),
            ["watch"] = Watch(),
        };
        var campaign = new CampaignData(
            Id,
            "The Long Winter (demo)",
            "A short campaign made for Tzar2: lead Captain Oren from the ford to the hill watch.",
            new CampaignMap[]
            {
                new("ford", "Ford", "maps/ford.t2map", "Cross the river ford.", IsEntry: true),
                new("mill", "Mill", "maps/mill.t2map", "Free the mill.", IsEntry: false),
                new("watch", "Watch", "maps/watch.t2map", "Hold the hill watch.", IsEntry: false),
            },
            new CampaignEdge[]
            {
                new("ford", "Mill", "mill", CampaignEdgeKind.LoadMap, 3),
                new("mill", "Watch", "watch", CampaignEdgeKind.Win, 4),
            });
        return new InMemoryCampaignSource(campaign, maps);
    }

    // Map 1: walk the captain across the ford; the next map loads when he reaches the far bank.
    private static MissionContent Ford()
    {
        var b = new Authoring();
        int captain = b.Unit("euro.footman", 0, 4, 10, new ObjectInfo("Oren", "Captain Oren", ObjectFlags.Persistent));
        b.Unit("euro.footman", 0, 4, 12);
        b.Unit("euro.peon", 0, 3, 8);
        b.Unit("euro.peon", 1, 40, 4);
        int bank = b.Event(new ObjectInAreaEvent("FarBank", new NamedObjects(new[] { captain }, Quantifier.All), new RectArea(30, 6, 36, 14)));
        int story = b.Action(new ShowStoryScreenAction("Winter", 8000, true, "The river froze early that year, and the captain was sent east.", null, null, null));
        int quest = b.Action(new AddQuestAction("Cross the ford", "Bring Captain Oren to the far bank.", null));
        b.Action(new SetObjectPropertiesAction("Trained", new NamedObjects(new[] { captain }, Quantifier.All), b.Expr("$level = 3")));
        b.Action(new LoadMapAction("Onward", "Mill"));
        b.Rule("1", story, quest, 2);
        b.Rule("FarBank", b.Action(new ExpressionAction("Remember", b.Expr("#Crossed = 1"))), 3);
        _ = bank;
        return b.Build("ford", 44, 20);
    }

    // Map 2: the miller asks for help; destroy the bandit house; the win names the next map.
    private static MissionContent Mill()
    {
        var b = new Authoring();
        b.Unit("euro.footman", 0, 4, 10, new ObjectInfo("Oren", "Captain Oren", ObjectFlags.Persistent));
        for (int i = 0; i < 8; i++)
        {
            b.Unit("euro.footman", 0, 5 + (i % 2), 6 + i);
        }

        int miller = b.Unit("euro.peon", 2, 10, 4, new ObjectInfo("Miller", "The Miller", ObjectFlags.None));
        int house = b.Structure("euro.house", 1, 30, 8, new ObjectInfo("BanditHouse", "", ObjectFlags.None, MaxHealth: 150));
        b.Unit("euro.peon", 1, 34, 14);
        int talk = b.Conversation(
            new PhraseDef("ask", true, miller, ExpressionDef.Empty, ExpressionDef.Empty, "Bandits took my mill. Will you drive them off?", null, null, new[] { 1, 2 }),
            new PhraseDef("yes", false, null, ExpressionDef.Empty, b.Expr("$Agreed = 1"), "We will.", null, null, Array.Empty<int>()),
            new PhraseDef("later", false, null, ExpressionDef.Empty, b.Expr("$Agreed = 2"), "Perhaps later.", null, null, Array.Empty<int>()));
        b.Event(new ObjectDestroyedEvent("HouseDown", new NamedObjects(new[] { house }, Quantifier.All)));
        int ask = b.Action(new ConversationAction("Ask", talk));
        int quest = b.Action(new AddQuestAction("Free the mill", "Burn the bandits' house east of the mill.", null));
        int greet = b.Action(new ShowMessageAction("Welcome", "The Miller", "#Crossed tells me you came by the ford.", null));
        b.Action(new RemoveQuestAction("Done", quest));
        b.Action(new PlayerWinAction("Freed", PlayerMasks.Of(1), "Watch"));
        b.Rule("1", ask, quest);
        b.Rule("#Crossed == 1", greet);
        b.Rule("HouseDown", 3, 4);
        return b.Build("mill", 44, 20);
    }

    // Map 3: hold the watch until the timer runs out; the captain must live. The campaign ends with the win.
    private static MissionContent Watch()
    {
        var b = new Authoring();
        int captain = b.Unit("euro.footman", 0, 8, 10, new ObjectInfo("Oren", "Captain Oren", ObjectFlags.Persistent));
        b.Unit("euro.footman", 0, 9, 10);
        b.Unit("euro.peon", 1, 40, 4);
        int relief = b.Unit("euro.footman", 0, 6, 6, new ObjectInfo("Relief", "", ObjectFlags.NotAppear));
        b.Event(new ObjectDestroyedEvent("CaptainDown", new NamedObjects(new[] { captain }, Quantifier.All)));
        int timer = b.Action(new SetTimerAction("Dawn", b.Expr("20000"), CountDown: true));
        int quest = b.Action(new AddQuestAction("Hold the watch", "Keep Captain Oren alive until dawn.", null));
        b.Action(new AppearObjectAction("Relief comes", new NamedObjects(new[] { relief }, Quantifier.All)));
        b.Action(new PlayerWinAction("Dawn breaks", PlayerMasks.Of(1), null));
        b.Action(new PlayerLoseAction("Fallen", PlayerMasks.Of(1), null));
        b.Rule("1", timer, quest);
        b.Rule("Dawn == 0 && $level_ok == 0", 2, b.Action(new ExpressionAction("Seen", b.Expr("$level_ok = 1"))));
        b.Rule("Dawn == 0", 3);
        b.Rule("CaptainDown", 4);
        return b.Build("watch", 44, 20);
    }

    // A minimal scenario author for code-built maps (names resolve while the expressions are parsed).
    private sealed class Authoring : IExpressionScope
    {
        private readonly List<string> _variables = [];
        private readonly List<EventDef> _events = [];
        private readonly List<ActionDef> _actions = [];
        private readonly List<RuleDef> _rules = [];
        private readonly List<ConversationDef> _conversations = [];
        private readonly List<MapObject> _objects = [];
        private readonly List<ObjectInfo> _infos = [];

        public int Unit(string type, int owner, int tx, int ty, ObjectInfo? info = null)
        {
            _objects.Add(new MapObject(type, (byte)owner, (tx * Tile) + (Tile / 2), (ty * Tile) + (Tile / 2)));
            _infos.Add(info ?? ObjectInfo.Plain);
            return _objects.Count - 1;
        }

        public int Structure(string type, int owner, int tx, int ty, ObjectInfo? info = null)
        {
            _objects.Add(MapObject.AtTile(type, (byte)owner, tx, ty));
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

        public void Rule(string condition, params int[] actions) => _rules.Add(new RuleDef("rule" + _rules.Count, Expr(condition), actions));

        public int Conversation(params PhraseDef[] phrases)
        {
            _conversations.Add(new ConversationDef("talk" + _conversations.Count, phrases));
            return _conversations.Count - 1;
        }

        public ExpressionDef Expr(string text) => ExpressionParser.Parse(text, this);

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

        public MissionContent Build(string name, int width, int height)
        {
            MapData map = ScenarioMaps.WithObjects(ScenarioMaps.Build(name, width, height, new byte[width * height], []), _objects);
            var kingdoms = new KingdomDef[]
            {
                Kingdom(1, PlayerType.HumanOrAi),
                Kingdom(2, PlayerType.AiOnly),
                Kingdom(3, PlayerType.AiOnly),
            };
            var scenario = new ScenarioData(
                new MapProperties(name, "Tzar2", "Demo campaign map (invented).", GameType.Standard, VictoryCondition.NotSpecified, 0, 1, false, false),
                _objects.Count,
                _infos.ToArray(),
                kingdoms,
                new[] { new DiplomacyDef(1, 3, Stance.Ally, true, false, false), new DiplomacyDef(3, 1, Stance.Ally, true, false, false) },
                _variables.ToArray(),
                _events.ToArray(),
                _actions.ToArray(),
                _rules.ToArray(),
                _conversations.ToArray(),
                EquatableArray<CustomSoundDef>.Empty,
                EquatableArray<ResourceRef>.Empty);
            return new MissionContent(map, scenario);
        }

        private static KingdomDef Kingdom(int number, PlayerType type)
            => new(number, "Kingdom " + number, string.Empty, "euro", type, true, 300, 300, 300, 300, string.Empty, true, true, false, -1, -1,
                EquatableArray<string>.Empty, EquatableArray<string>.Empty, EquatableArray<string>.Empty, EquatableArray<string>.Empty, EquatableArray<string>.Empty);
    }
}
