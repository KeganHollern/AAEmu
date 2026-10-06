using System.Collections.Concurrent;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Quests;

public partial class Quest
{
    private readonly HashSet<uint> _activeGuardTemplates = [];
    private readonly ConcurrentDictionary<uint, Npc> _guardNpcs = [];
    private bool _guardEventsAttached;
    private volatile bool _guardConstraintActivated;
    private const int GuardActive = 0;
    private const int GuardFailed = 1;
    private const int GuardCompleted = 2;
    private int _guardState;

    private bool HasGuardFailed => Volatile.Read(ref _guardState) == GuardFailed;

    internal void ActivateGuard(uint npcTemplateId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (npcTemplateId == 0 || Step is not (QuestComponentKind.Start or QuestComponentKind.Supply or QuestComponentKind.Progress))
                return;

            _guardConstraintActivated = true;
            _activeGuardTemplates.Add(npcTemplateId);
            if (!_guardEventsAttached)
            {
                UnitEvents.UpdateSubscription(ref Owner.Events.OnQuestStepChanged, OnGuardQuestStepChanged, true);
                if (Owner.Events is CharacterEvents events)
                    UnitEvents.UpdateSubscription(ref events.OnDisconnect, OnGuardOwnerDisconnected, true);
                _guardEventsAttached = true;
            }

            // The NPC acceptance path validates this exact object and makes it CurrentTarget
            // before StartQuest. Do not select another occurrence by template or proximity.
            if (!IsRestoringLoadedState && Step == QuestComponentKind.Start &&
                QuestAcceptorType == QuestAcceptorType.Npc && AcceptorId == npcTemplateId &&
                Owner is Character { CurrentTarget: Npc npc } && npc.TemplateId == npcTemplateId)
                BindGuardNpc(npc);
        }
    }

    internal void BindGuardNpc(Npc npc)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (npc == null || !_activeGuardTemplates.Contains(npc.TemplateId) ||
                _guardNpcs.ContainsKey(npc.TemplateId) || Volatile.Read(ref _guardState) != GuardActive ||
                Owner is not Character owner || npc.ParentWorld != owner.ParentWorld ||
                Step is not (QuestComponentKind.Start or QuestComponentKind.Supply or QuestComponentKind.Progress))
                return;

            _guardNpcs.TryAdd(npc.TemplateId, npc);
            npc.Events.AddDeathHandler(OnGuardDied);
            npc.Removing += OnGuardRemoved;
            CheckGuard(npc.TemplateId);
        }
    }

    internal bool CheckGuard(uint npcTemplateId)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (_guardNpcs.TryGetValue(npcTemplateId, out var npc) &&
                (npc.IsDead || npc.Hp <= 0 || npc.Despawned || npc.CombatRetired))
                Interlocked.CompareExchange(ref _guardState, GuardFailed, GuardActive);

            if (!HasGuardFailed)
                return true;

            FailGuard();
            return false;
        }
    }

    private void OnGuardDied(object sender, OnDeathArgs args)
    {
        if (args.Victim is Npc npc)
            OnGuardRemoved(npc);
    }

    private void OnGuardRemoved(Npc npc)
    {
        // NpcSpawner can hold its spawn lock here. Do not take the persistence lock
        // in this callback: quest side effects can acquire that spawn lock in reverse order.
        if (_guardNpcs.TryGetValue(npc.TemplateId, out var bound) && ReferenceEquals(bound, npc) &&
            Interlocked.CompareExchange(ref _guardState, GuardFailed, GuardActive) == GuardActive)
            _questManager.EnqueueEvaluation(this);
    }

    private bool TryCompleteGuard()
    {
        // The Ready transition and NPC removal compete for one decision. Once completion
        // wins, a later callback cannot fail the quest. Once removal wins, Ready is rejected.
        return Interlocked.CompareExchange(ref _guardState, GuardCompleted, GuardActive) != GuardFailed;
    }

    private void FailGuard()
    {
        // StartQuest activates Start acts before CharacterQuests owns the new runtime quest.
        // Retain an early failure for RunAct instead of failing a previous attempt by template ID.
        if (Owner.Quests.ActiveQuests.TryGetValue(TemplateId, out var active) && ReferenceEquals(active, this) &&
            Step is QuestComponentKind.Start or QuestComponentKind.Supply or QuestComponentKind.Progress)
            _questManager.FailQuest(Owner, TemplateId);
    }

    private void OnGuardQuestStepChanged(object sender, OnQuestStepChangedArgs args)
    {
        if (args.QuestId == TemplateId &&
            args.Step is QuestComponentKind.Fail or QuestComponentKind.Ready or QuestComponentKind.Drop or QuestComponentKind.Reward)
            ReleaseGuardBindings();
    }

    private void OnGuardOwnerDisconnected(object sender, OnDisconnectArgs args)
    {
        ReleaseGuardBindings();
    }

    internal void ReleaseGuardBindings()
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            Interlocked.CompareExchange(ref _guardState, GuardCompleted, GuardActive);
            foreach (var npc in _guardNpcs.Values)
            {
                npc.Events.RemoveDeathHandler(OnGuardDied);
                npc.Removing -= OnGuardRemoved;
            }
            _guardNpcs.Clear();
            _activeGuardTemplates.Clear();
            if (_guardEventsAttached)
            {
                UnitEvents.UpdateSubscription(ref Owner.Events.OnQuestStepChanged, OnGuardQuestStepChanged, false);
                if (Owner.Events is CharacterEvents events)
                    UnitEvents.UpdateSubscription(ref events.OnDisconnect, OnGuardOwnerDisconnected, false);
                _guardEventsAttached = false;
            }
        }
    }
}
