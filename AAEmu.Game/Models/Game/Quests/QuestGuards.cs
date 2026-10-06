using System.Collections.Concurrent;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Acts;
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

    internal bool BindGuardNpc(Npc npc)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (npc == null || !_activeGuardTemplates.Contains(npc.TemplateId) ||
                _guardNpcs.ContainsKey(npc.TemplateId) || Volatile.Read(ref _guardState) != GuardActive ||
                Owner is not Character owner || npc.ParentWorld != owner.ParentWorld ||
                Step is not (QuestComponentKind.Start or QuestComponentKind.Supply or QuestComponentKind.Progress))
                return false;

            _guardNpcs.TryAdd(npc.TemplateId, npc);
            npc.Events.AddDeathHandler(OnGuardDied);
            npc.Removing += OnGuardRemoved;
            CheckGuard(npc.TemplateId);
            return true;
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

    internal bool CanApplyGuardProgressEvent(QuestAct act)
    {
        if (!_guardConstraintActivated)
            return true;
        return Volatile.Read(ref _guardState) == GuardActive && Step == QuestComponentKind.Progress &&
            Owner.Quests.ActiveQuests.TryGetValue(TemplateId, out var active) && ReferenceEquals(active, this) &&
            QuestSteps.TryGetValue(Step, out var step) &&
            step.Components.TryGetValue(act.QuestComponent.Template.Id, out var component) &&
            ReferenceEquals(component, act.QuestComponent) && component.Acts.Contains(act);
    }

    private void CheckpointGuardCompletion()
    {
        if (_guardConstraintActivated && Volatile.Read(ref _guardState) == GuardCompleted &&
            Step is QuestComponentKind.Ready or QuestComponentKind.Reward && QuestSteps.ContainsKey(Step))
        {
            // Quests without a Ready component pass through it to Reward. Persist only
            // a real step, after its activation, so a reload can continue normally.
            Status = QuestStatus.Ready;
            Owner.Quests.PersistActiveQuest(this);
        }
    }

    internal void CheckpointGuardStart(bool newlyBound)
    {
        if (newlyBound)
            Owner.Quests.PersistActiveQuest(this);
    }

    internal void EvaluateGuardProgress()
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!_guardNpcs.IsEmpty && !IsRestoringLoadedState && Step == QuestComponentKind.Progress)
                RunCurrentStep();
        }
    }

    private bool HasStartedGuardInSavedProgress()
    {
        var currentOrder = GetActiveStepOrder(Step);
        if (currentOrder < 0)
            return false;

        var guards = QuestSteps.Values
            .Where(step => GetActiveStepOrder(step.ThisStep) is var order && order >= 0 && order <= currentOrder)
            .SelectMany(step => step.Components.Values)
            .SelectMany(component => component.Acts)
            .Where(act => act.Template is QuestActCheckGuard { NpcId: > 0 })
            .ToArray();
        foreach (var guard in guards)
        {
            var npcId = ((QuestActCheckGuard)guard.Template).NpcId;
            if (guard.QuestComponent.Template.KindId == QuestComponentKind.Start &&
                QuestAcceptorType == QuestAcceptorType.Npc && AcceptorId == npcId)
                return true;

            // Some quests put their guard and talk in separate Progress components.
            if (QuestSteps.TryGetValue(QuestComponentKind.Progress, out var progress) &&
                progress.Components.Values.SelectMany(component => component.Acts).Any(act =>
                    act.Template is QuestActObjTalk talk && talk.NpcId == npcId &&
                    talk.ThisComponentObjectiveIndex < Objectives.Length && Objectives[talk.ThisComponentObjectiveIndex] > 0))
                return true;
        }
        return false;
    }

    private bool FailInterruptedGuardOnRestore()
    {
        if (!HasStartedGuardInSavedProgress())
            return false;

        _guardConstraintActivated = true;
        Interlocked.CompareExchange(ref _guardState, GuardFailed, GuardActive);
        // No step was activated on this new runtime object. Do not finalize unstarted acts.
        _step = QuestComponentKind.Invalid;
        _questManager.FailQuest(Owner, TemplateId);
        Owner.Quests.PersistActiveQuest(this);
        return true;
    }

    internal void PrepareGuardForDisconnect()
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            if (!_guardNpcs.IsEmpty || HasStartedGuardInSavedProgress())
            {
                // Evaluate this quest only. Draining the manager queue would invert its lock order.
                EvaluateGuardProgress();
                if (Step is QuestComponentKind.Start or QuestComponentKind.Supply or QuestComponentKind.Progress &&
                    Interlocked.CompareExchange(ref _guardState, GuardFailed, GuardActive) != GuardCompleted)
                    FailGuard();
            }
            ReleaseGuardBindings();
        }
    }

    private void OnGuardQuestStepChanged(object sender, OnQuestStepChangedArgs args)
    {
        if (args.QuestId != TemplateId ||
            args.Step is not (QuestComponentKind.Fail or QuestComponentKind.Ready or QuestComponentKind.Drop or QuestComponentKind.Reward))
            return;

        ReleaseGuardBindings();
        if (args.Step == QuestComponentKind.Fail)
        {
            Status = QuestStatus.Failed;
            Owner.Quests.PersistActiveQuest(this);
        }
    }

    private void OnGuardOwnerDisconnected(object sender, OnDisconnectArgs args)
    {
        PrepareGuardForDisconnect();
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
