using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Scripting.Legacy;
using Tzar2.Sim;
using Tzar2.Sim.Events;

namespace Tzar2.Game.Scenario;

/// <summary>What a <see cref="ScenarioNotice"/> asks the client to show (REQ-SCR-03; ADR 0131, ADR 0132).</summary>
public enum ScenarioNoticeKind : byte
{
    /// <summary>A quest joined the quest list (a line in the message area; the log is in <see cref="ScenarioView"/>).</summary>
    QuestAdded,

    /// <summary>A quest left the quest list.</summary>
    QuestRemoved,

    /// <summary>Show Message: a pop-up with title, text and image.</summary>
    Message,

    /// <summary>Show Story Screen: scrolling text over an image, with sound and music; pauses single player.</summary>
    StoryScreen,

    /// <summary>Send Chat Message: a chat line.</summary>
    Chat,

    /// <summary>Lock View: hold the camera on (X, Y) for <see cref="ScenarioNotice.DurationMs"/>.</summary>
    LockView,

    /// <summary>Unlock View.</summary>
    UnlockView,

    /// <summary>Scroll View: move the camera to (X, Y).</summary>
    ScrollView,

    /// <summary>A phrase was said (the conversation dialog shows it; the choices are in <see cref="ScenarioView"/>).</summary>
    PhraseSaid,

    /// <summary>The conversation ended.</summary>
    ConversationEnded,

    /// <summary>The scenario asked for the next campaign map (the campaign layer acts on it).</summary>
    MapRequested,
}

/// <summary>
/// One scenario presentation item with its texts resolved (UTF-8, from the scenario) for the client: it never sees
/// the sim's indices.
/// </summary>
/// <param name="Kind">What to show.</param>
/// <param name="Title">Title (quest name, message title, speaker), or empty.</param>
/// <param name="Text">Text, or empty.</param>
/// <param name="Image">User image path in the map package, or null.</param>
/// <param name="Sound">User sound path, or null.</param>
/// <param name="Music">User music path, or null.</param>
/// <param name="DurationMs">Lock time or story scroll time (ms), else 0.</param>
/// <param name="AutoClose">Story screens: close by themselves at the end.</param>
/// <param name="X">Camera target X in world units (view notices), else 0.</param>
/// <param name="Y">Camera target Y in world units.</param>
/// <param name="ForLocalPlayer">Whether the local kingdom is among the targets.</param>
public sealed record ScenarioNotice(ScenarioNoticeKind Kind, string Title, string Text, string? Image, string? Sound, string? Music, int DurationMs, bool AutoClose, int X, int Y, bool ForLocalPlayer);

/// <summary>An entry of the quest log (F8).</summary>
/// <param name="Title">The quest's name.</param>
/// <param name="Text">The quest's text.</param>
/// <param name="Image">Optional image.</param>
public sealed record QuestEntry(string Title, string Text, string? Image);

/// <summary>A phrase the player can choose.</summary>
/// <param name="Position">The position to answer with (<see cref="ScenarioPresenter.Reply"/>).</param>
/// <param name="Speaker">The source object's name or title, or empty.</param>
/// <param name="Text">The phrase.</param>
/// <param name="Icon">Custom portrait, or null.</param>
/// <param name="Sound">Custom sound, or null.</param>
public sealed record PhraseChoice(int Position, string Speaker, string Text, string? Icon, string? Sound);

/// <summary>A conversation waiting for the player.</summary>
/// <param name="Serial">The session serial (replies name it).</param>
/// <param name="Name">The conversation's name.</param>
/// <param name="Choices">The candidate phrases in order.</param>
public sealed record ConversationView(int Serial, string Name, ImmutableArray<PhraseChoice> Choices);

/// <summary>The scenario state the HUD shows: the quest log and the conversation waiting for a choice.</summary>
/// <param name="Quests">Active quests in the order they were added.</param>
/// <param name="Conversation">The conversation waiting for a choice, or null.</param>
public sealed record ScenarioView(ImmutableArray<QuestEntry> Quests, ConversationView? Conversation)
{
    /// <summary>Nothing to show.</summary>
    public static ScenarioView Empty { get; } = new(ImmutableArray<QuestEntry>.Empty, null);
}

/// <summary>
/// The game layer between the trigger engine and the client (ADR 0132): turns the tick's scenario events into
/// <see cref="ScenarioNotice"/>s with the scenario's texts, keeps a <see cref="ScenarioView"/> of the quest log and the
/// offered phrases, and builds the reply commands. Runs on the sim thread (after each tick); the views it hands out
/// are immutable.
/// </summary>
public sealed class ScenarioPresenter
{
    private readonly LegacyTriggerSystem _triggers;
    private readonly ScenarioData _scenario;
    private readonly World _world;
    private readonly uint _localMask;
    private int _questVersion = -1;
    private int _conversationKey = int.MinValue;
    private ImmutableArray<QuestEntry> _quests = ImmutableArray<QuestEntry>.Empty;
    private ConversationView? _conversation;

    /// <summary>Creates a presenter for the local kingdom <paramref name="localPlayer"/>.</summary>
    public ScenarioPresenter(LegacyTriggerSystem triggers, World world, byte localPlayer)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(world);
        _triggers = triggers;
        _scenario = triggers.Scenario;
        _world = world;
        _localMask = 1u << localPlayer;
        View = Build();
    }

    /// <summary>The current view (replaced, never mutated).</summary>
    public ScenarioView View { get; private set; }

    /// <summary>Whether a conversation waits for a reply (single player pauses on it).</summary>
    public bool AwaitingReply => _triggers.State.Conversation >= 0 && _triggers.State.PendingReply < 0;

    /// <summary>The trigger engine (diagnostics, the headless runner).</summary>
    public LegacyTriggerSystem Triggers => _triggers;

    /// <summary>Adds the reply "say candidate <paramref name="position"/> of session <paramref name="serial"/>" to a batch.</summary>
    public static void Reply(Sim.Commands.CommandList batch, int serial, int position)
    {
        ArgumentNullException.ThrowIfNull(batch);
        batch.Add(Sim.Commands.CommandKind.ScenarioReply, [], 0, 0, default, serial, position);
    }

    /// <summary>Translates the tick's events into notices and refreshes <see cref="View"/> (sim thread, after a tick).</summary>
    public void Update(ReadOnlySpan<SimEvent> events, List<ScenarioNotice> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        foreach (SimEvent e in events)
        {
            if (Translate(e) is { } notice)
            {
                into.Add(notice);
            }
        }

        View = Build();
    }

    private ScenarioNotice? Translate(SimEvent e)
    {
        bool local = (e.Player == 0xFF && (e.Kind is SimEventKind.ViewLocked or SimEventKind.ViewUnlocked or SimEventKind.ViewScrolled or SimEventKind.PhraseSaid or SimEventKind.ConversationEnded or SimEventKind.MapRequested))
            || ((uint)e.X & _localMask) != 0;
        switch (e.Kind)
        {
            case SimEventKind.QuestAdded when Action(e.Arg) is AddQuestAction q:
                return new ScenarioNotice(ScenarioNoticeKind.QuestAdded, q.Name, q.Text, q.Image?.Path, null, null, 0, false, 0, 0, local);
            case SimEventKind.QuestRemoved when Action(e.Arg) is AddQuestAction q:
                return new ScenarioNotice(ScenarioNoticeKind.QuestRemoved, q.Name, q.Text, null, null, null, 0, false, 0, 0, local);
            case SimEventKind.ScenarioMessage when Action(e.Arg) is ShowMessageAction m:
                return new ScenarioNotice(ScenarioNoticeKind.Message, m.Title, m.Text, m.Image?.Path, null, null, 0, false, 0, 0, local);
            case SimEventKind.StoryScreen when Action(e.Arg) is ShowStoryScreenAction s:
                return new ScenarioNotice(ScenarioNoticeKind.StoryScreen, s.Name, s.Text, s.Image?.Path, s.Sound?.Path, s.Music?.Path, Math.Max(0, s.ScrollTimeMs), s.AutoClose, 0, 0, local);
            case SimEventKind.ScenarioChat when Action(e.Arg) is SendChatAction c:
                return new ScenarioNotice(ScenarioNoticeKind.Chat, string.Empty, c.Text, null, null, null, 0, false, 0, 0, local);
            case SimEventKind.ViewLocked:
                return new ScenarioNotice(ScenarioNoticeKind.LockView, string.Empty, string.Empty, null, null, null, e.Arg, false, e.X, e.Y, true);
            case SimEventKind.ViewUnlocked:
                return new ScenarioNotice(ScenarioNoticeKind.UnlockView, string.Empty, string.Empty, null, null, null, 0, false, 0, 0, true);
            case SimEventKind.ViewScrolled:
                return new ScenarioNotice(ScenarioNoticeKind.ScrollView, string.Empty, string.Empty, null, null, null, 0, false, e.X, e.Y, true);
            case SimEventKind.PhraseSaid when Phrase(e.X, e.Y) is { } p:
                return new ScenarioNotice(ScenarioNoticeKind.PhraseSaid, Speaker(p), p.Text, p.Icon?.Path, p.Sound?.Path, null, 0, false, 0, 0, true);
            case SimEventKind.ConversationEnded:
                return new ScenarioNotice(ScenarioNoticeKind.ConversationEnded, string.Empty, string.Empty, null, null, null, 0, false, 0, 0, true);
            case SimEventKind.MapRequested:
                return new ScenarioNotice(ScenarioNoticeKind.MapRequested, string.Empty, NextMap(e.Arg) ?? string.Empty, null, null, null, 0, false, e.Y, 0, true);
            default:
                return null;
        }
    }

    /// <summary>The next-map name of a Load Map / Player Win / Player Lose action, or null.</summary>
    public string? NextMap(int actionIndex) => Action(actionIndex) switch
    {
        LoadMapAction l => l.Map,
        PlayerWinAction w => w.Map,
        PlayerLoseAction l => l.Map,
        _ => null,
    };

    private ActionDef? Action(int index) => (uint)index < (uint)_scenario.Actions.Length ? _scenario.Actions[index] : null;

    private PhraseDef? Phrase(int conversation, int phrase)
        => (uint)conversation < (uint)_scenario.Conversations.Length && (uint)phrase < (uint)_scenario.Conversations[conversation].Phrases.Length
            ? _scenario.Conversations[conversation].Phrases[phrase]
            : null;

    private string Speaker(PhraseDef phrase)
    {
        if (phrase.SourceObject is not { } source || (uint)source >= (uint)_scenario.ObjectCount)
        {
            return string.Empty;
        }

        ObjectInfo info = _scenario.ObjectAt(source);
        return info.Title.Length > 0 ? info.Title : info.Name;
    }

    // Rebuilt only when the quests or the offered phrases change (the view is immutable and shared with the frames).
    private ScenarioView Build()
    {
        TriggerState s = _triggers.State;
        int questKey = s.Quests.Count == 0 ? 0 : HashCode.Combine(s.Quests.Count, s.Quests[0], s.Quests[^1]);
        bool rebuilt = false;
        if (questKey != _questVersion || _quests.Length != s.Quests.Count)
        {
            var quests = ImmutableArray.CreateBuilder<QuestEntry>(s.Quests.Count);
            foreach (int q in s.Quests)
            {
                if (Action(q) is AddQuestAction add)
                {
                    quests.Add(new QuestEntry(add.Name, add.Text, add.Image?.Path));
                }
            }

            _quests = quests.ToImmutable();
            _questVersion = questKey;
            rebuilt = true;
        }

        int conversationKey = AwaitingReply ? s.ConversationSerial * 1024 + s.ConversationSteps : -1;
        if (conversationKey != _conversationKey)
        {
            _conversation = null;
            if (AwaitingReply)
            {
                ConversationDef c = _scenario.Conversations[s.Conversation];
                var choices = ImmutableArray.CreateBuilder<PhraseChoice>(s.Candidates.Count);
                for (int i = 0; i < s.Candidates.Count; i++)
                {
                    PhraseDef p = c.Phrases[s.Candidates[i]];
                    choices.Add(new PhraseChoice(i, Speaker(p), p.Text, p.Icon?.Path, p.Sound?.Path));
                }

                _conversation = new ConversationView(s.ConversationSerial, c.Name, choices.ToImmutable());
            }

            _conversationKey = conversationKey;
            rebuilt = true;
        }

        return rebuilt || View is null ? new ScenarioView(_quests, _conversation) : View;
    }
}
