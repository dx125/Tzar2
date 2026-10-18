using System;
using Tzar2.Content.Compiled.Scenario;
using Tzar2.Core.Ids;
using Tzar2.Sim.Events;

namespace Tzar2.Scripting.Legacy;

public sealed partial class LegacyTriggerSystem
{
    /// <summary>
    /// Conversation action (Appendix C.8; ADR 0131): the conversation starts and offers its initial phrases whose
    /// condition holds (an empty condition: always). While another conversation runs, it waits in a queue.
    /// </summary>
    private void StartConversation(int conversation)
    {
        if (_state.Conversation >= 0)
        {
            if (_state.QueuedConversations.Count < TriggerState.MaxQueuedConversations)
            {
                _state.QueuedConversations.Add(conversation);
            }
            else
            {
                Fault(ScenarioFault.ActionFailed);
            }

            return;
        }

        _state.Conversation = conversation;
        _state.ConversationSerial = _state.ConversationSerial == int.MaxValue ? 1 : _state.ConversationSerial + 1;
        _state.ConversationSteps = 0;
        _state.PendingReply = -1;
        _state.Candidates.Clear();
        EquatableArray<PhraseDef> phrases = _scenario.Conversations[conversation].Phrases;
        for (int p = 0; p < phrases.Length && _state.Candidates.Count < TriggerState.MaxCandidates; p++)
        {
            if (phrases[p].Initial && Offered(phrases[p]))
            {
                _state.Candidates.Add(p);
            }
        }

        Offer();
    }

    // A phrase is offered when its condition holds; an empty or unparsable condition always holds (ADR 0120).
    private bool Offered(PhraseDef phrase) => Evaluate(phrase.Condition, 1, SourceOf(phrase)) != 0;

    private EntityId SourceOf(PhraseDef phrase) => phrase.SourceObject is { } source ? MapEntity(source) : EntityId.None;

    private void Offer()
    {
        int conversation = _state.Conversation;
        if (_state.Candidates.Count == 0)
        {
            EndConversation(guard: false);
            return;
        }

        _world.Events.Emit(new SimEvent(SimEventKind.ConversationOffered, Everyone, EntityId.None, conversation, _state.ConversationSerial, _state.Candidates.Count));
    }

    // A reply received in this tick's command step: the phrase is said (its result runs), then its follow-ups whose
    // condition holds are offered.
    private void AnswerConversation()
    {
        if (_state.Conversation < 0 || _state.PendingReply < 0)
        {
            return;
        }

        int conversation = _state.Conversation;
        int phraseIndex = _state.Candidates[_state.PendingReply];
        _state.PendingReply = -1;
        EquatableArray<PhraseDef> phrases = _scenario.Conversations[conversation].Phrases;
        PhraseDef phrase = phrases[phraseIndex];
        _world.Events.Emit(new SimEvent(SimEventKind.PhraseSaid, Everyone, EntityId.None, conversation, phraseIndex, _state.ConversationSerial));
        _rule = -1;
        _action = -1;
        Evaluate(phrase.Result, 0, SourceOf(phrase));
        _state.ConversationSteps++;
        if (_state.ConversationSteps >= _options.MaxConversationSteps)
        {
            Fault(ScenarioFault.ConversationGuard);
            EndConversation(guard: true);
            return;
        }

        _state.Candidates.Clear();
        foreach (int next in phrase.FollowUps)
        {
            if ((uint)next < (uint)phrases.Length && _state.Candidates.Count < TriggerState.MaxCandidates && !_state.Candidates.Contains(next) && Offered(phrases[next]))
            {
                _state.Candidates.Add(next);
            }
        }

        Offer();
    }

    private void EndConversation(bool guard)
    {
        _world.Events.Emit(new SimEvent(SimEventKind.ConversationEnded, Everyone, EntityId.None, _state.Conversation, _state.ConversationSerial, guard ? 1 : 0));
        _state.Conversation = TriggerState.NoConversation;
        _state.Candidates.Clear();
        _state.PendingReply = -1;
        if (_state.QueuedConversations.Count > 0)
        {
            int next = _state.QueuedConversations[0];
            _state.QueuedConversations.RemoveAt(0);
            StartConversation(next);
        }
    }
}
