using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Tzar2.Content.Compiled;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Content.Compiled.Scenario.Expressions;

namespace Tzar2.Content.Editing;

/// <summary>Options of a validation pass.</summary>
public sealed record ValidationOptions
{
    /// <summary>The ruleset the map plays with (type, owner and placement checks), or null to skip them.</summary>
    public Ruleset? Ruleset { get; init; }

    /// <summary>Resources that exist outside the map (campaign-level shared assets), or null.</summary>
    public Func<string, bool>? ExternalAsset { get; init; }

    /// <summary>Most single-tile findings of one kind listed before a summary line.</summary>
    public int MaxTileIssues { get; init; } = 32;
}

/// <summary>
/// The live validator behind the editor's validation panel (TDD §18.3, replacing the original's save-time check,
/// Appendix C.10): it never throws and never stops editing; only <see cref="ValidationSeverity.Blocking"/> findings
/// block playing. The checks are deterministic and their order is fixed (map, terrain, objects, kingdoms, triggers,
/// conversations, sounds, resources).
/// </summary>
public static class MapValidator
{
    /// <summary>Largest phrase text of the original (Appendix C.8).</summary>
    public const int MaxPhraseText = 1000;

    /// <summary>Most initial phrases and follow-ups per phrase of the original (Appendix C.8).</summary>
    public const int MaxPhraseLinks = 6;

    /// <summary>Validates <paramref name="document"/>.</summary>
    public static ValidationResult Validate(MapDocument document, ValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new ValidationOptions();
        var v = new Pass(document, options);
        v.Run();
        return new ValidationResult(v.Issues.ToImmutable());
    }

    /// <summary>Whether <paramref name="name"/> is a legacy-safe event name (letters, digits, underscore; Appendix C.3).</summary>
    public static bool IsLegacyEventName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="name"/> is a legacy-safe action name (letters, digits, spaces, underscores, apostrophes).</summary>
    public static bool IsLegacyActionName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or ' ' or '\''))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Pass(MapDocument doc, ValidationOptions options)
    {
        private readonly DocumentState _s = doc.State;
        private readonly Ruleset? _rules = options.Ruleset;

        public ImmutableArray<ValidationIssue>.Builder Issues { get; } = ImmutableArray.CreateBuilder<ValidationIssue>();

        private ScenarioModel M => _s.Scenario;

        public void Run()
        {
            Map();
            Terrain();
            Objects();
            Kingdoms();
            Triggers();
            Conversations();
            Sounds();
        }

        private void Add(ValidationSeverity severity, string code, string message, EditorLocation at) => Issues.Add(new ValidationIssue(severity, code, message, at));

        private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

        private void Map()
        {
            if (M.Properties.Title.Length == 0)
            {
                Add(ValidationSeverity.Info, "map.title", "The map has no title.", new EditorLocation(LocationKind.Properties));
            }

            try
            {
                doc.BuildScenario();
            }
            catch (ArgumentException e)
            {
                Add(ValidationSeverity.Blocking, "map.build", "The scenario does not build: " + e.Message, EditorLocation.Map);
            }
        }

        private void Terrain()
        {
            int undrawable = 0, cliffs = 0;
            for (int y = 0; y < doc.Height; y++)
            {
                for (int x = 0; x < doc.Width; x++)
                {
                    if (!doc.IsDrawableTile(x, y) && ++undrawable <= options.MaxTileIssues)
                    {
                        Add(ValidationSeverity.Warning, "terrain.transition", "The terrain set has no transition for this tile's corners (shown red).", EditorLocation.Tile(x, y));
                    }

                    if (doc.IsCliff(x, y) && !doc.IsLandTile(x, y) && ++cliffs <= options.MaxTileIssues)
                    {
                        Add(ValidationSeverity.Warning, "terrain.cliff", "A cliff stands on water.", EditorLocation.Tile(x, y));
                    }
                }
            }

            if (undrawable > options.MaxTileIssues)
            {
                Add(ValidationSeverity.Warning, "terrain.transition", F($"{undrawable} tiles in all need a transition the set lacks."), EditorLocation.Map);
            }

            if (cliffs > options.MaxTileIssues)
            {
                Add(ValidationSeverity.Warning, "terrain.cliff", F($"{cliffs} cliff tiles in all stand on water."), EditorLocation.Map);
            }
        }

        private void Objects()
        {
            var persistent = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (EditorObject o in _s.Objects)
            {
                EditorLocation at = EditorLocation.Object(o);
                if (o.Owner != MapObject.Nature && o.Owner >= 8)
                {
                    Add(ValidationSeverity.Blocking, "object.owner", F($"'{o.Label}' belongs to player slot {o.Owner}; slots are 0–7 or Nature."), at);
                }

                if ((o.Info.Flags & ObjectFlags.Persistent) != 0)
                {
                    if (o.Info.Name.Length == 0)
                    {
                        Add(ValidationSeverity.Warning, "object.persistent", "A Persistent object needs a name: later maps match it by name.", at);
                    }
                    else
                    {
                        persistent[o.Info.Name] = persistent.TryGetValue(o.Info.Name, out int n) ? n + 1 : 1;
                    }
                }

                if (_rules is null)
                {
                    continue;
                }

                int typeId = _rules.EntityTypeId(o.TypeKey);
                if (typeId < 0)
                {
                    Add(ValidationSeverity.Blocking, "object.type", $"The ruleset has no type '{o.TypeKey}'.", at);
                    continue;
                }

                EntityTypeDefinition type = _rules.EntityTypes[typeId];
                Placement(o, type, at);
            }

            foreach (KeyValuePair<string, int> p in persistent)
            {
                if (p.Value > 1)
                {
                    Add(ValidationSeverity.Warning, "object.persistent", F($"{p.Value} Persistent objects are named '{p.Key}'; carry-over takes one."), EditorLocation.Map);
                }
            }
        }

        private void Placement(EditorObject o, EntityTypeDefinition type, EditorLocation at)
        {
            if (type.HasFootprint)
            {
                int w = type.FootprintWidth;
                int h = type.FootprintHeight;
                if (!doc.ContainsTile(o.TileX, o.TileY) || !doc.ContainsTile(o.TileX + w - 1, o.TileY + h - 1))
                {
                    Add(ValidationSeverity.Blocking, "object.footprint", $"'{o.Label}' does not fit on the map.", at);
                    return;
                }

                if (type.Traits.Category == EntityCategory.Building && type.Traits.Placement != PlacementKind.Bridge && !AllLand(o.TileX, o.TileY, w, h))
                {
                    Add(ValidationSeverity.Warning, "object.terrain", $"Building '{o.Label}' stands on water.", at);
                }

                return;
            }

            bool water = doc.ContainsTile(o.TileX, o.TileY) && doc.IsWaterTile(o.TileX, o.TileY);
            if (type.Locomotor == Locomotor.Ship && type.IsMobile && !water)
            {
                Add(ValidationSeverity.Warning, "object.terrain", $"Ship '{o.Label}' stands on land.", at);
            }
            else if (type.Locomotor is Locomotor.Foot or Locomotor.Mounted or Locomotor.Large && type.IsMobile && water)
            {
                Add(ValidationSeverity.Warning, "object.terrain", $"Land unit '{o.Label}' stands in water.", at);
            }
        }

        private bool AllLand(int x0, int y0, int w, int h)
        {
            for (int y = y0; y < y0 + h; y++)
            {
                for (int x = x0; x < x0 + w; x++)
                {
                    if (!doc.IsLandTile(x, y))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private void Kingdoms()
        {
            bool human = false;
            var numbers = new HashSet<int>();
            foreach (KingdomDef k in M.Kingdoms)
            {
                var at = new EditorLocation(LocationKind.Kingdom, k.Number);
                if (k.Number is < 1 or > 8 || !numbers.Add(k.Number))
                {
                    Add(ValidationSeverity.Blocking, "kingdom.number", F($"Kingdom number {k.Number} is out of range or repeated."), at);
                    continue;
                }

                human |= k.Active && k.PlayerType == PlayerType.HumanOrAi;
                if (k.Active && k.PlayerType != PlayerType.Rescue && k.StartTileX < 0)
                {
                    Add(ValidationSeverity.Warning, "kingdom.start", F($"Kingdom {k.Number} has no starting location."), at);
                }
                else if (k.StartTileX >= 0 && !doc.ContainsTile(k.StartTileX, k.StartTileY))
                {
                    Add(ValidationSeverity.Error, "kingdom.start", F($"Kingdom {k.Number}'s starting location is off the map."), at);
                }

                if (_rules is not null)
                {
                    foreach (string key in Concat(k.ForbiddenBuildings, k.ForbiddenUnits))
                    {
                        if (_rules.EntityTypeId(key) < 0)
                        {
                            Add(ValidationSeverity.Warning, "kingdom.technology", $"Kingdom {k.Number} forbids '{key}', which the ruleset does not define.", at);
                        }
                    }
                }
            }

            if (M.Kingdoms.Length > 0 && !human)
            {
                Add(ValidationSeverity.Warning, "kingdom.human", "No active kingdom is Human or AI: the first kingdom becomes the player.", EditorLocation.Map);
            }

            foreach (DiplomacyDef d in M.Diplomacy)
            {
                if (d.From is < 1 or > 8 || d.To is < 1 or > 8 || d.From == d.To)
                {
                    Add(ValidationSeverity.Error, "kingdom.diplomacy", F($"Diplomacy pair {d.From} → {d.To} is invalid."), EditorLocation.Map);
                }
            }
        }

        private static List<string> Concat(EquatableArray<string> a, EquatableArray<string> b)
        {
            var list = new List<string>(a.Length + b.Length);
            list.AddRange(a.Items);
            list.AddRange(b.Items);
            return list;
        }

        private void Triggers()
        {
            var variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < M.Variables.Length; i++)
            {
                string v = M.Variables[i];
                var at = new EditorLocation(LocationKind.Variable, i, Name: v);
                if (ExpressionParser.IsReserved(v.TrimStart('#')))
                {
                    Add(ValidationSeverity.Error, "variable.reserved", $"Variable '{v}' collides with a reserved name.", at);
                }
                else if (!variables.Add(v))
                {
                    Add(ValidationSeverity.Error, "variable.duplicate", $"Variable '{v}' is declared twice.", at);
                }
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var used = new bool[M.Events.Length];
            for (int i = 0; i < M.Events.Length; i++)
            {
                EventDef e = M.Events[i];
                var at = new EditorLocation(LocationKind.Event, i, Name: e.Name);
                if (!names.Add(e.Name))
                {
                    Add(ValidationSeverity.Error, "event.name", $"Two events are named '{e.Name}'; conditions find the first.", at);
                }
                else if (!IsLegacyEventName(e.Name))
                {
                    Add(ValidationSeverity.Info, "event.name", $"Event name '{e.Name}' uses characters the original editor does not allow.", at);
                }

                if (e is UnknownEvent u)
                {
                    Add(ValidationSeverity.Warning, "event.unknown", F($"Event '{e.Name}' has a legacy type ({u.LegacyCode}) the engine does not understand; it is always false."), at);
                }

                if (e is ObjectExpressionEvent x)
                {
                    Expression(x.Expression, at, used);
                }
            }

            var actionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referenced = new bool[M.Actions.Length];
            foreach (RuleDef r in M.Rules)
            {
                foreach (int a in r.ActionIndices)
                {
                    if ((uint)a < (uint)referenced.Length)
                    {
                        referenced[a] = true;
                    }
                }
            }

            for (int i = 0; i < M.Actions.Length; i++)
            {
                ActionDef a = M.Actions[i];
                var at = new EditorLocation(LocationKind.Action, i, Name: a.Name);
                if (!actionNames.Add(a.Name) && a is SetTimerAction or AddQuestAction)
                {
                    Add(ValidationSeverity.Error, "action.name", $"Two actions are named '{a.Name}'; the timer or quest name is ambiguous.", at);
                }

                if (!referenced[i])
                {
                    Add(ValidationSeverity.Info, "action.unused", $"Action '{a.Name}' is in no rule.", at);
                }

                switch (a)
                {
                    case RemoveQuestAction { AddQuestActionIndex: < 0 }:
                        Add(ValidationSeverity.Warning, "action.quest", $"'{a.Name}' removes no quest.", at);
                        break;
                    case StopTimerAction { SetTimerActionIndex: < 0 }:
                        Add(ValidationSeverity.Warning, "action.timer", $"'{a.Name}' stops no timer.", at);
                        break;
                    case ConversationAction { ConversationIndex: < 0 }:
                        Add(ValidationSeverity.Warning, "action.conversation", $"'{a.Name}' starts no conversation.", at);
                        break;
                    case ExpressionAction x:
                        Expression(x.Expression, at, used);
                        break;
                    case SetObjectPropertiesAction x:
                        Expression(x.Expression, at, used);
                        break;
                    case SetTimerAction x:
                        Expression(x.Initial, at, used);
                        break;
                    case SpawnObjectAction x when _rules is not null && _rules.EntityTypeId(x.TypeKey) < 0:
                        Add(ValidationSeverity.Warning, "action.type", $"'{a.Name}' spawns '{x.TypeKey}', which the ruleset does not define.", at);
                        break;
                    case UnknownAction u:
                        Add(ValidationSeverity.Warning, "action.unknown", F($"Action '{a.Name}' has a legacy type ({u.LegacyCode}) the engine does not understand; it does nothing."), at);
                        break;
                }

                Resources(a, at);
            }

            for (int i = 0; i < M.Rules.Length; i++)
            {
                RuleDef r = M.Rules[i];
                var at = new EditorLocation(LocationKind.Rule, i, Name: r.Name);
                if (r.ActionIndices.Length == 0)
                {
                    Add(ValidationSeverity.Warning, "rule.empty", $"Rule '{r.Name}' has no actions.", at);
                }

                if (r.Condition.IsEmpty)
                {
                    Add(ValidationSeverity.Warning, "rule.condition", $"Rule '{r.Name}' has no condition and never fires.", at);
                }
                else
                {
                    Expression(r.Condition, at, used);
                }
            }

            for (int i = 0; i < used.Length; i++)
            {
                if (!used[i])
                {
                    Add(ValidationSeverity.Info, "event.unused", $"Event '{M.Events[i].Name}' is used by no expression.", new EditorLocation(LocationKind.Event, i, Name: M.Events[i].Name));
                }
            }
        }

        private void Expression(ExpressionDef e, EditorLocation at, bool[] usedEvents)
        {
            if (e.IsEmpty)
            {
                return;
            }

            ExpressionDef parsed = M.Parse(e.Source);
            if (parsed.Error is { } error)
            {
                Add(ValidationSeverity.Error, "expression.parse", $"'{e.Source}': {error}", at);
                return;
            }

            if (parsed.Root is not null)
            {
                Walk(parsed.Root, at, usedEvents);
            }
        }

        private void Walk(Expr e, EditorLocation at, bool[] usedEvents)
        {
            switch (e)
            {
                case UnknownRef u:
                    Add(ValidationSeverity.Warning, "expression.name", $"'{u.Name}' is neither an event, a timer nor a function; it evaluates to 0.", at);
                    break;
                case EventRef r when (uint)r.EventIndex < (uint)usedEvents.Length:
                    usedEvents[r.EventIndex] = true;
                    break;
                case UnaryExpr u:
                    Walk(u.Operand, at, usedEvents);
                    break;
                case BinaryExpr b:
                    Walk(b.Left, at, usedEvents);
                    Walk(b.Right, at, usedEvents);
                    break;
                case AssignExpr a:
                    Walk(a.Target, at, usedEvents);
                    Walk(a.Value, at, usedEvents);
                    break;
                case CallExpr c:
                    foreach (Expr arg in c.Arguments)
                    {
                        Walk(arg, at, usedEvents);
                    }

                    break;
                case SequenceExpr q:
                    foreach (Expr s in q.Statements)
                    {
                        Walk(s, at, usedEvents);
                    }

                    break;
            }
        }

        private void Conversations()
        {
            bool[] unused = Array.Empty<bool>();
            for (int c = 0; c < M.Conversations.Length; c++)
            {
                ConversationDef conv = M.Conversations[c];
                var at = new EditorLocation(LocationKind.Conversation, c, Name: conv.Name);
                int initial = 0;
                for (int p = 0; p < conv.Phrases.Length; p++)
                {
                    PhraseDef ph = conv.Phrases[p];
                    var pat = new EditorLocation(LocationKind.Phrase, c, p, Name: ph.Name);
                    initial += ph.Initial ? 1 : 0;
                    if (ph.Text.Length > MaxPhraseText)
                    {
                        Add(ValidationSeverity.Warning, "phrase.text", F($"Phrase '{ph.Name}' has {ph.Text.Length} characters; the original allows {MaxPhraseText}."), pat);
                    }

                    if (ph.FollowUps.Length > MaxPhraseLinks)
                    {
                        Add(ValidationSeverity.Warning, "phrase.followups", F($"Phrase '{ph.Name}' has {ph.FollowUps.Length} follow-ups; the original allows {MaxPhraseLinks}."), pat);
                    }

                    foreach (int f in ph.FollowUps)
                    {
                        if ((uint)f >= (uint)conv.Phrases.Length)
                        {
                            Add(ValidationSeverity.Blocking, "phrase.followups", F($"Phrase '{ph.Name}' follows up with phrase {f}, which does not exist."), pat);
                        }
                    }

                    Expression(ph.Condition, pat, unused);
                    Expression(ph.Result, pat, unused);
                    Resource(ph.Icon, pat);
                    Resource(ph.Sound, pat);
                }

                if (conv.Phrases.Length > 0 && initial == 0)
                {
                    Add(ValidationSeverity.Warning, "conversation.initial", $"Conversation '{conv.Name}' has no initial phrase and cannot start.", at);
                }
                else if (initial > MaxPhraseLinks)
                {
                    Add(ValidationSeverity.Warning, "conversation.initial", F($"Conversation '{conv.Name}' has {initial} initial phrases; the original allows {MaxPhraseLinks}."), at);
                }

                if (HasCycle(conv))
                {
                    Add(ValidationSeverity.Warning, "conversation.cycle", $"Conversation '{conv.Name}' loops (the original hangs; Tzar2 cuts it after 256 phrases).", at);
                }
            }
        }

        private static bool HasCycle(ConversationDef conv)
        {
            int n = conv.Phrases.Length;
            var state = new byte[n];
            for (int start = 0; start < n; start++)
            {
                if (state[start] != 0)
                {
                    continue;
                }

                var stack = new Stack<(int Node, int Next)>();
                stack.Push((start, 0));
                state[start] = 1;
                while (stack.Count > 0)
                {
                    (int node, int next) = stack.Pop();
                    EquatableArray<int> follow = conv.Phrases[node].FollowUps;
                    if (next < follow.Length)
                    {
                        stack.Push((node, next + 1));
                        int f = follow[next];
                        if ((uint)f >= (uint)n)
                        {
                            continue;
                        }

                        if (state[f] == 1)
                        {
                            return true;
                        }

                        if (state[f] == 0)
                        {
                            state[f] = 1;
                            stack.Push((f, 0));
                        }
                    }
                    else
                    {
                        state[node] = 2;
                    }
                }
            }

            return false;
        }

        private void Sounds()
        {
            for (int i = 0; i < M.Sounds.Length; i++)
            {
                CustomSoundDef s = M.Sounds[i];
                var at = new EditorLocation(LocationKind.Sound, i, Name: s.Name);
                Resource(s.Sound, at);
                if (s.Repeat == SoundRepeat.Random && s.IntervalMaxMs < s.IntervalMs)
                {
                    Add(ValidationSeverity.Warning, "sound.interval", $"Sound '{s.Name}' has a random interval whose maximum is below its minimum.", at);
                }

                if (s.SourceObject is null && !doc.ContainsTile(s.X, s.Y))
                {
                    Add(ValidationSeverity.Warning, "sound.source", $"Sound '{s.Name}' plays from a point off the map.", at);
                }
            }

            for (int i = 0; i < M.Music.Length; i++)
            {
                Resource(M.Music[i], new EditorLocation(LocationKind.Asset, i, Name: M.Music[i].Path));
            }

            foreach (EditorObject o in _s.Objects)
            {
                Resource(o.Info.Icon, EditorLocation.Object(o));
                Resource(o.Info.BigIcon, EditorLocation.Object(o));
                if (o.Info.Item is { } item)
                {
                    Resource(item.Image, EditorLocation.Object(o));
                    Resource(item.Sound, EditorLocation.Object(o));
                    Resource(item.Music, EditorLocation.Object(o));
                }
            }
        }

        private void Resources(ActionDef a, EditorLocation at)
        {
            switch (a)
            {
                case AddQuestAction x:
                    Resource(x.Image, at);
                    break;
                case ShowMessageAction x:
                    Resource(x.Image, at);
                    break;
                case ShowStoryScreenAction x:
                    Resource(x.Image, at);
                    Resource(x.Sound, at);
                    Resource(x.Music, at);
                    break;
            }
        }

        private void Resource(ResourceRef? r, EditorLocation at)
        {
            if (r is null || r.Path.Length == 0)
            {
                return;
            }

            string path;
            try
            {
                path = ContentPath.Normalize(r.Path);
            }
            catch (ArgumentException)
            {
                Add(ValidationSeverity.Warning, "resource.path", $"'{r.Path}' is not a valid resource path.", at);
                return;
            }

            if (!_s.Assets.ContainsKey(path) && options.ExternalAsset?.Invoke(path) != true)
            {
                Add(ValidationSeverity.Warning, "resource.missing", $"User resource '{r.Path}' is not in the map.", at);
            }
        }
    }
}
