using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tzar2.Content.Compiled.Scenario.Expressions;

namespace Tzar2.Content.Compiled.Scenario;

/// <summary>
/// Canonical JSON encoding of a <see cref="ScenarioData"/> (the <c>scenario.json</c> part of a <c>.t2map</c>, ADR 0120).
/// Fields come in a fixed order, unions carry a <c>kind</c>, text is UTF-8 unescaped where JSON allows, so the bytes
/// depend only on the scenario. Expressions are stored as their source text and re-parsed on reading against the
/// scenario's own name tables, which yields the same resolved tree. The same bytes feed
/// <see cref="ScenarioData.ContentHash"/>.
/// </summary>
public static class ScenarioJson
{
    /// <summary>Format version written into the document.</summary>
    public const int FormatVersion = 1;

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };
    private static readonly JsonWriterOptions Pretty = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = true };

    /// <summary>Encodes <paramref name="scenario"/>.</summary>
    public static byte[] Write(ScenarioData scenario, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, indented ? Pretty : Compact))
        {
            new Writer(w).Scenario(scenario);
        }

        if (indented)
        {
            // Utf8JsonWriter's newline is Environment.NewLine; normalise so the bytes are platform-independent.
            return NormaliseNewlines(buffer.WrittenSpan);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Decodes a document written by <see cref="Write"/>.</summary>
    /// <exception cref="FormatException">The document is malformed.</exception>
    public static ScenarioData Read(ReadOnlySpan<byte> json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json.ToArray());
            return new Reader().Scenario(doc.RootElement);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new FormatException("Invalid scenario document: " + e.Message, e);
        }
    }

    private static byte[] NormaliseNewlines(ReadOnlySpan<byte> bytes)
    {
        var output = new List<byte>(bytes.Length);
        foreach (byte b in bytes)
        {
            if (b != (byte)'\r')
            {
                output.Add(b);
            }
        }

        output.Add((byte)'\n');
        return output.ToArray();
    }

    private sealed class Writer(Utf8JsonWriter w)
    {
        public void Scenario(ScenarioData s)
        {
            w.WriteStartObject();
            w.WriteNumber("format", FormatVersion);
            MapProperties p = s.Properties;
            w.WriteStartObject("properties");
            w.WriteString("title", p.Title);
            w.WriteString("author", p.Author);
            w.WriteString("description", p.Description);
            w.WriteString("gameType", p.GameType.ToString());
            w.WriteString("victory", p.Victory.ToString());
            w.WriteNumber("populationLimit", p.PopulationLimit);
            w.WriteNumber("difficulty", p.Difficulty);
            w.WriteBoolean("noFog", p.NoFog);
            w.WriteBoolean("explore", p.Explore);
            w.WriteEndObject();
            w.WriteNumber("objectCount", s.ObjectCount);
            Array("objects", s.Objects, ObjectInfo);
            Array("kingdoms", s.Kingdoms, Kingdom);
            Array("diplomacy", s.Diplomacy, Diplomacy);
            Array("variables", s.Variables, v => w.WriteStringValue(v));
            Array("events", s.Events, Event);
            Array("actions", s.Actions, Action);
            Array("rules", s.Rules, Rule);
            Array("conversations", s.Conversations, Conversation);
            Array("sounds", s.Sounds, Sound);
            Array("music", s.Music, r => w.WriteStringValue(r.Path));
            w.WriteEndObject();
        }

        private void Array<T>(string name, EquatableArray<T> items, Action<T> item)
        {
            w.WriteStartArray(name);
            foreach (T t in items)
            {
                item(t);
            }

            w.WriteEndArray();
        }

        private void Ints(string name, EquatableArray<int> items) => Array(name, items, i => w.WriteNumberValue(i));

        private void Strings(string name, EquatableArray<string> items) => Array(name, items, i => w.WriteStringValue(i));

        private void OptionalInt(string name, int? value)
        {
            if (value is int v)
            {
                w.WriteNumber(name, v);
            }
            else
            {
                w.WriteNull(name);
            }
        }

        private void Resource(string name, ResourceRef? r)
        {
            if (r is null)
            {
                w.WriteNull(name);
            }
            else
            {
                w.WriteString(name, r.Path);
            }
        }

        private void Expression(string name, ExpressionDef e) => w.WriteString(name, e.Source);

        private void ObjectInfo(ObjectInfo o)
        {
            w.WriteStartObject();
            w.WriteString("name", o.Name);
            w.WriteString("title", o.Title);
            w.WriteNumber("flags", (int)o.Flags);
            OptionalInt("health", o.Health);
            OptionalInt("maxHealth", o.MaxHealth);
            OptionalInt("attack", o.Attack);
            OptionalInt("defence", o.Defence);
            OptionalInt("mana", o.Mana);
            OptionalInt("level", o.Level);
            OptionalInt("resistance", o.Resistance);
            Resource("icon", o.Icon);
            Resource("bigIcon", o.BigIcon);
            w.WriteEndObject();
        }

        private void Kingdom(KingdomDef k)
        {
            w.WriteStartObject();
            w.WriteNumber("number", k.Number);
            w.WriteString("name", k.Name);
            w.WriteString("tzar", k.TzarName);
            w.WriteString("civilization", k.Civilization.ToString());
            w.WriteString("playerType", k.PlayerType.ToString());
            w.WriteBoolean("active", k.Active);
            w.WriteNumber("food", k.Food);
            w.WriteNumber("wood", k.Wood);
            w.WriteNumber("gold", k.Gold);
            w.WriteNumber("stone", k.Stone);
            w.WriteString("aiScript", k.AiScript);
            w.WriteBoolean("economyAi", k.EconomyAi);
            w.WriteBoolean("warAi", k.WarAi);
            w.WriteBoolean("sharedVictory", k.SharedVictory);
            w.WriteNumber("startX", k.StartTileX);
            w.WriteNumber("startY", k.StartTileY);
            Strings("forbiddenBuildings", k.ForbiddenBuildings);
            Strings("forbiddenUnits", k.ForbiddenUnits);
            Strings("forbiddenResearches", k.ForbiddenResearches);
            Strings("forbiddenUpgrades", k.ForbiddenUpgrades);
            Strings("initialResearches", k.InitialResearches);
            w.WriteEndObject();
        }

        private void Diplomacy(DiplomacyDef d)
        {
            w.WriteStartObject();
            w.WriteNumber("from", d.From);
            w.WriteNumber("to", d.To);
            w.WriteString("stance", d.Stance.ToString());
            w.WriteBoolean("sharedVision", d.SharedVision);
            w.WriteBoolean("sharedResources", d.SharedResources);
            w.WriteBoolean("commandUnits", d.CommandUnits);
            w.WriteEndObject();
        }

        private void Objects(string name, ObjectSpec spec)
        {
            w.WriteStartObject(name);
            switch (spec)
            {
                case NamedObjects n:
                    w.WriteString("kind", "named");
                    Ints("objects", n.ObjectIndices);
                    w.WriteString("quantifier", n.Quantifier.ToString());
                    break;
                case TypedObjects t:
                    w.WriteString("kind", "typed");
                    w.WriteString("type", t.TypeKey);
                    w.WriteNumber("players", t.Players);
                    w.WriteString("comparison", t.Comparison.ToString());
                    w.WriteNumber("count", t.Count);
                    w.WriteString("quantifier", t.Quantifier.ToString());
                    break;
                default:
                    throw new ArgumentException($"Unknown object spec {spec.GetType().Name}.");
            }

            w.WriteEndObject();
        }

        private void Area(string name, AreaSpec area)
        {
            w.WriteStartObject(name);
            switch (area)
            {
                case RectArea r:
                    w.WriteString("kind", "rect");
                    w.WriteNumber("left", r.Left);
                    w.WriteNumber("top", r.Top);
                    w.WriteNumber("right", r.Right);
                    w.WriteNumber("bottom", r.Bottom);
                    break;
                case ObjectArea o:
                    w.WriteString("kind", "object");
                    w.WriteNumber("object", o.ObjectIndex);
                    OptionalInt("radius", o.RadiusTiles);
                    break;
                default:
                    throw new ArgumentException($"Unknown area {area.GetType().Name}.");
            }

            w.WriteEndObject();
        }

        private void Event(EventDef e)
        {
            w.WriteStartObject();
            w.WriteString("name", e.Name);
            switch (e)
            {
                case ObjectInAreaEvent x:
                    w.WriteString("kind", "objectInArea");
                    Objects("objects", x.Objects);
                    Area("area", x.Area);
                    break;
                case ObjectDestroyedEvent x:
                    w.WriteString("kind", "objectDestroyed");
                    Objects("objects", x.Objects);
                    break;
                case ObjectsEnterHolderEvent x:
                    w.WriteString("kind", "enterHolder");
                    Objects("objects", x.Objects);
                    w.WriteNumber("holder", x.HolderIndex);
                    break;
                case ObjectLeaveHolderEvent x:
                    w.WriteString("kind", "leaveHolder");
                    Objects("objects", x.Objects);
                    w.WriteNumber("holder", x.HolderIndex);
                    break;
                case ObjectExistEvent x:
                    w.WriteString("kind", "objectExist");
                    Objects("objects", x.Objects);
                    break;
                case PlayerDefeatedEvent x:
                    w.WriteString("kind", "playerDefeated");
                    w.WriteNumber("players", x.Players);
                    break;
                case PlayerWonEvent x:
                    w.WriteString("kind", "playerWon");
                    w.WriteNumber("players", x.Players);
                    break;
                case ObjectExpressionEvent x:
                    w.WriteString("kind", "objectExpression");
                    Objects("objects", x.Objects);
                    Expression("expression", x.Expression);
                    break;
                case UnknownEvent x:
                    w.WriteString("kind", "unknown");
                    w.WriteNumber("code", x.LegacyCode);
                    w.WriteBase64String("raw", x.Raw.Items.AsSpan());
                    break;
                default:
                    throw new ArgumentException($"Unknown event {e.GetType().Name}.");
            }

            w.WriteEndObject();
        }

        private void Action(ActionDef a)
        {
            w.WriteStartObject();
            w.WriteString("name", a.Name);
            switch (a)
            {
                case AddQuestAction x:
                    w.WriteString("kind", "addQuest");
                    w.WriteString("text", x.Text);
                    Resource("image", x.Image);
                    break;
                case RemoveQuestAction x:
                    w.WriteString("kind", "removeQuest");
                    w.WriteNumber("quest", x.AddQuestActionIndex);
                    break;
                case AppearObjectAction x:
                    w.WriteString("kind", "appearObject");
                    Objects("objects", x.Objects);
                    break;
                case ChangeDiplomacyAction x:
                    w.WriteString("kind", "changeDiplomacy");
                    w.WriteNumber("kingdom1", x.Kingdom1);
                    w.WriteNumber("kingdom2", x.Kingdom2);
                    w.WriteString("stance", x.Stance.ToString());
                    w.WriteBoolean("sharedVision", x.SharedVision);
                    w.WriteBoolean("sharedResources", x.SharedResources);
                    w.WriteBoolean("commandUnits", x.CommandUnits);
                    break;
                case ChangeObjectOwnerAction x:
                    w.WriteString("kind", "changeObjectOwner");
                    Objects("objects", x.Objects);
                    w.WriteNumber("kingdom", x.Kingdom);
                    break;
                case ChangePlayerStatusAction x:
                    w.WriteString("kind", "changePlayerStatus");
                    w.WriteNumber("kingdom", x.Kingdom);
                    w.WriteBoolean("cannotWin", x.CannotWin);
                    w.WriteBoolean("cannotLose", x.CannotLose);
                    break;
                case ConversationAction x:
                    w.WriteString("kind", "conversation");
                    w.WriteNumber("conversation", x.ConversationIndex);
                    break;
                case DestroyObjectAction x:
                    w.WriteString("kind", "destroyObject");
                    Objects("objects", x.Objects);
                    break;
                case EraseObjectAction x:
                    w.WriteString("kind", "eraseObject");
                    Objects("objects", x.Objects);
                    break;
                case ExploreAreaAction x:
                    w.WriteString("kind", "exploreArea");
                    w.WriteNumber("time", x.TimeMs);
                    w.WriteBoolean("revealFog", x.RevealFog);
                    Area("area", x.Area);
                    break;
                case ExpressionAction x:
                    w.WriteString("kind", "expression");
                    Expression("expression", x.Expression);
                    break;
                case KeepRuleAction:
                    w.WriteString("kind", "keepRule");
                    break;
                case LoadMapAction x:
                    w.WriteString("kind", "loadMap");
                    w.WriteString("map", x.Map);
                    break;
                case LockViewAction x:
                    w.WriteString("kind", "lockView");
                    w.WriteNumber("time", x.TimeMs);
                    Area("area", x.Area);
                    break;
                case LockUnitsAction x:
                    w.WriteString("kind", "lockUnits");
                    Objects("objects", x.Objects);
                    w.WriteBoolean("locked", x.Locked);
                    break;
                case ObjectOrderAction x:
                    w.WriteString("kind", "objectOrder");
                    Objects("objects", x.Objects);
                    Area("area", x.Area);
                    w.WriteString("order", x.OrderKey);
                    break;
                case PlayerLoseAction x:
                    w.WriteString("kind", "playerLose");
                    w.WriteNumber("players", x.Players);
                    OptionalString("map", x.Map);
                    break;
                case PlayerWinAction x:
                    w.WriteString("kind", "playerWin");
                    w.WriteNumber("players", x.Players);
                    OptionalString("map", x.Map);
                    break;
                case ScrollViewAction x:
                    w.WriteString("kind", "scrollView");
                    Area("from", x.From);
                    Area("to", x.To);
                    break;
                case SendChatAction x:
                    w.WriteString("kind", "sendChat");
                    w.WriteNumber("players", x.Players);
                    w.WriteString("text", x.Text);
                    break;
                case SetObjectPropertiesAction x:
                    w.WriteString("kind", "setObjectProperties");
                    Objects("objects", x.Objects);
                    Expression("expression", x.Expression);
                    break;
                case SetTimerAction x:
                    w.WriteString("kind", "setTimer");
                    Expression("initial", x.Initial);
                    w.WriteBoolean("countDown", x.CountDown);
                    break;
                case ShowMessageAction x:
                    w.WriteString("kind", "showMessage");
                    w.WriteString("title", x.Title);
                    w.WriteString("text", x.Text);
                    Resource("image", x.Image);
                    break;
                case ShowStoryScreenAction x:
                    w.WriteString("kind", "showStoryScreen");
                    w.WriteNumber("scrollTime", x.ScrollTimeMs);
                    w.WriteBoolean("autoClose", x.AutoClose);
                    w.WriteString("text", x.Text);
                    Resource("image", x.Image);
                    Resource("sound", x.Sound);
                    Resource("music", x.Music);
                    break;
                case SpawnObjectAction x:
                    w.WriteString("kind", "spawnObject");
                    w.WriteString("type", x.TypeKey);
                    w.WriteNumber("owner", x.Owner);
                    w.WriteNumber("count", x.Count);
                    Area("area", x.Area);
                    break;
                case StopTimerAction x:
                    w.WriteString("kind", "stopTimer");
                    w.WriteNumber("timer", x.SetTimerActionIndex);
                    break;
                case UnlockViewAction:
                    w.WriteString("kind", "unlockView");
                    break;
                case UnknownAction x:
                    w.WriteString("kind", "unknown");
                    w.WriteNumber("code", x.LegacyCode);
                    w.WriteBase64String("raw", x.Raw.Items.AsSpan());
                    break;
                default:
                    throw new ArgumentException($"Unknown action {a.GetType().Name}.");
            }

            w.WriteEndObject();
        }

        private void OptionalString(string name, string? value)
        {
            if (value is null)
            {
                w.WriteNull(name);
            }
            else
            {
                w.WriteString(name, value);
            }
        }

        private void Rule(RuleDef r)
        {
            w.WriteStartObject();
            w.WriteString("name", r.Name);
            Expression("condition", r.Condition);
            Ints("actions", r.ActionIndices);
            w.WriteEndObject();
        }

        private void Conversation(ConversationDef c)
        {
            w.WriteStartObject();
            w.WriteString("name", c.Name);
            Array("phrases", c.Phrases, p =>
            {
                w.WriteStartObject();
                w.WriteString("name", p.Name);
                w.WriteBoolean("initial", p.Initial);
                OptionalInt("source", p.SourceObject);
                Expression("condition", p.Condition);
                Expression("result", p.Result);
                w.WriteString("text", p.Text);
                Resource("icon", p.Icon);
                Resource("sound", p.Sound);
                Ints("followUps", p.FollowUps);
                w.WriteEndObject();
            });
            w.WriteEndObject();
        }

        private void Sound(CustomSoundDef s)
        {
            w.WriteStartObject();
            w.WriteString("name", s.Name);
            w.WriteString("sound", s.Sound.Path);
            w.WriteString("repeat", s.Repeat.ToString());
            w.WriteNumber("interval", s.IntervalMs);
            w.WriteNumber("intervalMax", s.IntervalMaxMs);
            OptionalInt("source", s.SourceObject);
            w.WriteNumber("x", s.X);
            w.WriteNumber("y", s.Y);
            w.WriteEndObject();
        }
    }

    private sealed class Reader
    {
        private TableScope _scope = new(ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

        public ScenarioData Scenario(JsonElement root)
        {
            int format = root.GetProperty("format").GetInt32();
            if (format != FormatVersion)
            {
                throw new JsonException($"unsupported scenario format {format}");
            }

            JsonElement p = root.GetProperty("properties");
            var properties = new MapProperties(
                Str(p, "title"),
                Str(p, "author"),
                Str(p, "description"),
                Enum<GameType>(p, "gameType"),
                Enum<VictoryCondition>(p, "victory"),
                Int(p, "populationLimit"),
                Int(p, "difficulty"),
                Bool(p, "noFog"),
                Bool(p, "explore"));
            ImmutableArray<string> variables = List(root, "variables", e => e.GetString() ?? string.Empty);

            // Names first: expressions resolve against the scenario's own tables.
            ImmutableArray<string> eventNames = List(root, "events", e => Str(e, "name"));
            ImmutableArray<string> timerNames = List(root, "actions", e => Str(e, "kind") == "setTimer" ? Str(e, "name") : string.Empty);
            _scope = new TableScope(variables, eventNames, timerNames);
            return new ScenarioData(
                properties,
                Int(root, "objectCount"),
                List(root, "objects", ObjectInfo),
                List(root, "kingdoms", Kingdom),
                List(root, "diplomacy", Diplomacy),
                variables,
                List(root, "events", Event),
                List(root, "actions", Action),
                List(root, "rules", e => new RuleDef(Str(e, "name"), Expr(e, "condition"), Ints(e, "actions"))),
                List(root, "conversations", Conversation),
                List(root, "sounds", Sound),
                List(root, "music", e => new ResourceRef(e.GetString() ?? string.Empty)));
        }

        private static ImmutableArray<T> List<T>(JsonElement e, string name, Func<JsonElement, T> item)
        {
            var builder = ImmutableArray.CreateBuilder<T>();
            foreach (JsonElement x in e.GetProperty(name).EnumerateArray())
            {
                builder.Add(item(x));
            }

            return builder.ToImmutable();
        }

        private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString() ?? string.Empty;

        private static string? OptionalStr(JsonElement e, string name)
        {
            JsonElement v = e.GetProperty(name);
            return v.ValueKind == JsonValueKind.Null ? null : v.GetString();
        }

        private static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

        private static int? OptionalInt(JsonElement e, string name)
        {
            JsonElement v = e.GetProperty(name);
            return v.ValueKind == JsonValueKind.Null ? null : v.GetInt32();
        }

        private static bool Bool(JsonElement e, string name) => e.GetProperty(name).GetBoolean();

        private static ushort Mask(JsonElement e, string name) => e.GetProperty(name).GetUInt16();

        private static T Enum<T>(JsonElement e, string name)
            where T : struct, Enum
            => System.Enum.TryParse(Str(e, name), out T value) && System.Enum.IsDefined(value) ? value : throw new JsonException($"bad {name} '{Str(e, name)}'");

        private static ImmutableArray<int> Ints(JsonElement e, string name) => List(e, name, x => x.GetInt32());

        private static ImmutableArray<string> Strings(JsonElement e, string name) => List(e, name, x => x.GetString() ?? string.Empty);

        private static ResourceRef? Resource(JsonElement e, string name) => OptionalStr(e, name) is string s ? new ResourceRef(s) : null;

        private ExpressionDef Expr(JsonElement e, string name) => ExpressionParser.Parse(Str(e, name), _scope);

        private static ObjectInfo ObjectInfo(JsonElement e) => new(
            Str(e, "name"),
            Str(e, "title"),
            (ObjectFlags)Int(e, "flags"),
            OptionalInt(e, "health"),
            OptionalInt(e, "maxHealth"),
            OptionalInt(e, "attack"),
            OptionalInt(e, "defence"),
            OptionalInt(e, "mana"),
            OptionalInt(e, "level"),
            OptionalInt(e, "resistance"),
            Resource(e, "icon"),
            Resource(e, "bigIcon"));

        private static KingdomDef Kingdom(JsonElement e) => new(
            Int(e, "number"),
            Str(e, "name"),
            Str(e, "tzar"),
            Enum<Civilization>(e, "civilization"),
            Enum<PlayerType>(e, "playerType"),
            Bool(e, "active"),
            Int(e, "food"),
            Int(e, "wood"),
            Int(e, "gold"),
            Int(e, "stone"),
            Str(e, "aiScript"),
            Bool(e, "economyAi"),
            Bool(e, "warAi"),
            Bool(e, "sharedVictory"),
            Int(e, "startX"),
            Int(e, "startY"),
            Strings(e, "forbiddenBuildings"),
            Strings(e, "forbiddenUnits"),
            Strings(e, "forbiddenResearches"),
            Strings(e, "forbiddenUpgrades"),
            Strings(e, "initialResearches"));

        private static DiplomacyDef Diplomacy(JsonElement e) => new(
            Int(e, "from"), Int(e, "to"), Enum<Stance>(e, "stance"), Bool(e, "sharedVision"), Bool(e, "sharedResources"), Bool(e, "commandUnits"));

        private static ObjectSpec Objects(JsonElement parent, string name)
        {
            JsonElement e = parent.GetProperty(name);
            return Str(e, "kind") switch
            {
                "named" => new NamedObjects(Ints(e, "objects"), Enum<Quantifier>(e, "quantifier")),
                "typed" => new TypedObjects(Str(e, "type"), Mask(e, "players"), Enum<CountComparison>(e, "comparison"), Int(e, "count"), Enum<Quantifier>(e, "quantifier")),
                string k => throw new JsonException($"unknown object spec '{k}'"),
            };
        }

        private static AreaSpec Area(JsonElement parent, string name)
        {
            JsonElement e = parent.GetProperty(name);
            return Str(e, "kind") switch
            {
                "rect" => new RectArea(Int(e, "left"), Int(e, "top"), Int(e, "right"), Int(e, "bottom")),
                "object" => new ObjectArea(Int(e, "object"), OptionalInt(e, "radius")),
                string k => throw new JsonException($"unknown area '{k}'"),
            };
        }

        private EventDef Event(JsonElement e)
        {
            string n = Str(e, "name");
            return Str(e, "kind") switch
            {
                "objectInArea" => new ObjectInAreaEvent(n, Objects(e, "objects"), Area(e, "area")),
                "objectDestroyed" => new ObjectDestroyedEvent(n, Objects(e, "objects")),
                "enterHolder" => new ObjectsEnterHolderEvent(n, Objects(e, "objects"), Int(e, "holder")),
                "leaveHolder" => new ObjectLeaveHolderEvent(n, Objects(e, "objects"), Int(e, "holder")),
                "objectExist" => new ObjectExistEvent(n, Objects(e, "objects")),
                "playerDefeated" => new PlayerDefeatedEvent(n, Mask(e, "players")),
                "playerWon" => new PlayerWonEvent(n, Mask(e, "players")),
                "objectExpression" => new ObjectExpressionEvent(n, Objects(e, "objects"), Expr(e, "expression")),
                "unknown" => new UnknownEvent(n, Int(e, "code"), ImmutableArray.Create(e.GetProperty("raw").GetBytesFromBase64())),
                string k => throw new JsonException($"unknown event kind '{k}'"),
            };
        }

        private ActionDef Action(JsonElement e)
        {
            string n = Str(e, "name");
            return Str(e, "kind") switch
            {
                "addQuest" => new AddQuestAction(n, Str(e, "text"), Resource(e, "image")),
                "removeQuest" => new RemoveQuestAction(n, Int(e, "quest")),
                "appearObject" => new AppearObjectAction(n, Objects(e, "objects")),
                "changeDiplomacy" => new ChangeDiplomacyAction(n, Int(e, "kingdom1"), Int(e, "kingdom2"), Enum<Stance>(e, "stance"), Bool(e, "sharedVision"), Bool(e, "sharedResources"), Bool(e, "commandUnits")),
                "changeObjectOwner" => new ChangeObjectOwnerAction(n, Objects(e, "objects"), Int(e, "kingdom")),
                "changePlayerStatus" => new ChangePlayerStatusAction(n, Int(e, "kingdom"), Bool(e, "cannotWin"), Bool(e, "cannotLose")),
                "conversation" => new ConversationAction(n, Int(e, "conversation")),
                "destroyObject" => new DestroyObjectAction(n, Objects(e, "objects")),
                "eraseObject" => new EraseObjectAction(n, Objects(e, "objects")),
                "exploreArea" => new ExploreAreaAction(n, Int(e, "time"), Bool(e, "revealFog"), Area(e, "area")),
                "expression" => new ExpressionAction(n, Expr(e, "expression")),
                "keepRule" => new KeepRuleAction(n),
                "loadMap" => new LoadMapAction(n, Str(e, "map")),
                "lockView" => new LockViewAction(n, Int(e, "time"), Area(e, "area")),
                "lockUnits" => new LockUnitsAction(n, Objects(e, "objects"), Bool(e, "locked")),
                "objectOrder" => new ObjectOrderAction(n, Objects(e, "objects"), Area(e, "area"), Str(e, "order")),
                "playerLose" => new PlayerLoseAction(n, Mask(e, "players"), OptionalStr(e, "map")),
                "playerWin" => new PlayerWinAction(n, Mask(e, "players"), OptionalStr(e, "map")),
                "scrollView" => new ScrollViewAction(n, Area(e, "from"), Area(e, "to")),
                "sendChat" => new SendChatAction(n, Mask(e, "players"), Str(e, "text")),
                "setObjectProperties" => new SetObjectPropertiesAction(n, Objects(e, "objects"), Expr(e, "expression")),
                "setTimer" => new SetTimerAction(n, Expr(e, "initial"), Bool(e, "countDown")),
                "showMessage" => new ShowMessageAction(n, Str(e, "title"), Str(e, "text"), Resource(e, "image")),
                "showStoryScreen" => new ShowStoryScreenAction(n, Int(e, "scrollTime"), Bool(e, "autoClose"), Str(e, "text"), Resource(e, "image"), Resource(e, "sound"), Resource(e, "music")),
                "spawnObject" => new SpawnObjectAction(n, Str(e, "type"), Int(e, "owner"), Int(e, "count"), Area(e, "area")),
                "stopTimer" => new StopTimerAction(n, Int(e, "timer")),
                "unlockView" => new UnlockViewAction(n),
                "unknown" => new UnknownAction(n, Int(e, "code"), ImmutableArray.Create(e.GetProperty("raw").GetBytesFromBase64())),
                string k => throw new JsonException($"unknown action kind '{k}'"),
            };
        }

        private ConversationDef Conversation(JsonElement e) => new(
            Str(e, "name"),
            List(e, "phrases", p => new PhraseDef(
                Str(p, "name"),
                Bool(p, "initial"),
                OptionalInt(p, "source"),
                Expr(p, "condition"),
                Expr(p, "result"),
                Str(p, "text"),
                Resource(p, "icon"),
                Resource(p, "sound"),
                Ints(p, "followUps"))));

        private static CustomSoundDef Sound(JsonElement e) => new(
            Str(e, "name"),
            new ResourceRef(Str(e, "sound")),
            Enum<SoundRepeat>(e, "repeat"),
            Int(e, "interval"),
            Int(e, "intervalMax"),
            OptionalInt(e, "source"),
            Int(e, "x"),
            Int(e, "y"));
    }
}

/// <summary>
/// An <see cref="IExpressionScope"/> over fixed name tables (case-insensitive, first match wins; user variables are
/// never added). Used to re-resolve stored expressions and by tools that already know all names.
/// </summary>
public sealed class TableScope : IExpressionScope
{
    private readonly Dictionary<string, int> _variables;
    private readonly Dictionary<string, int> _events;
    private readonly Dictionary<string, int> _timers;

    /// <summary>Creates a scope.</summary>
    /// <param name="variables">User variable names.</param>
    /// <param name="events">Event names by event index.</param>
    /// <param name="timers">Timer names by action index (empty for actions that are not Set Timer).</param>
    public TableScope(ImmutableArray<string> variables, ImmutableArray<string> events, ImmutableArray<string> timers)
    {
        _variables = Index(variables);
        _events = Index(events);
        _timers = Index(timers);
    }

    /// <inheritdoc/>
    public int UserVariable(string name) => _variables.TryGetValue(name, out int i) ? i : -1;

    /// <inheritdoc/>
    public int Event(string name) => _events.TryGetValue(name, out int i) ? i : -1;

    /// <inheritdoc/>
    public int Timer(string name) => _timers.TryGetValue(name, out int i) ? i : -1;

    private static Dictionary<string, int> Index(ImmutableArray<string> names)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].Length > 0)
            {
                map.TryAdd(names[i], i);
            }
        }

        return map;
    }
}
