using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Quests;

public partial class Quest
{
    private Npc _acceptedNpc;
    private readonly Dictionary<uint, Npc> _componentNpcSources = [];
    private readonly Dictionary<uint, (Npc Npc, NpcAi Ai)> _followNpcs = [];
    private bool _npcControlEventsAttached;

    internal void RecordAcceptedNpc(Npc npc)
    {
        _acceptedNpc = npc;
    }

    internal bool RememberNpcControlSource(QuestAct act, OnTalkMadeArgs args)
    {
        var component = act.QuestComponent;
        if (component.Template.NpcAiId != QuestNpcAiName.FollowUnit)
            return true;
        if (IsRestoringLoadedState || args.QuestId != TemplateId ||
            args.QuestComponentId != component.Template.Id || args.QuestActId != act.Id ||
            !ReferenceEquals(args.SourcePlayer, Owner) || args.Transform?.GameObject is not Npc npc ||
            npc.TemplateId != component.Template.NpcId || npc.TemplateId != args.NpcId ||
            !IsCurrentNpcControlComponent(component) || !IsAvailableControlNpc(npc))
            return false;

        if (_followNpcs.TryGetValue(component.Template.Id, out var bound))
        {
            if (bound.Ai.IsQuestFollowOwner(this))
                return ReferenceEquals(bound.Npc, npc);
            bound.Ai.StopQuestFollow(this);
            _followNpcs.Remove(component.Template.Id);
        }
        _componentNpcSources[component.Template.Id] = npc;

        // A new validated talk can restore a lost lease. Do not replay the component's skill or rewards.
        return !AppliedComponentEffectIds.Contains(component.Template.Id) || TryApplyNpcControl(component.Template);
    }

    internal bool TryApplyNpcControl(QuestComponentTemplate component)
    {
        if (component.NpcAiId != QuestNpcAiName.FollowUnit)
            return true;
        if (Step is not (QuestComponentKind.Start or QuestComponentKind.Supply or QuestComponentKind.Progress) ||
            IsRestoringLoadedState || Owner is not Character owner)
            return false;

        if (_followNpcs.TryGetValue(component.Id, out var bound))
            return bound.Ai.IsQuestFollowOwner(this);

        var npc = _componentNpcSources.GetValueOrDefault(component.Id);
        if (npc == null && QuestAcceptorType == QuestAcceptorType.Npc &&
            AcceptorId == component.NpcId && _acceptedNpc?.TemplateId == component.NpcId)
            npc = _acceptedNpc;
        if (!IsAvailableControlNpc(npc) || npc.Ai == null ||
            !npc.Ai.TryStartQuestFollow(this, owner, OnNpcControlLost))
            return false;

        _followNpcs.Add(component.Id, (npc, npc.Ai));
        if (!_npcControlEventsAttached)
        {
            UnitEvents.UpdateSubscription(ref owner.Events.OnDisconnect, OnNpcControlOwnerDisconnected, true);
            _npcControlEventsAttached = true;
        }
        return true;
    }

    private bool IsCurrentNpcControlComponent(QuestComponent component)
    {
        return Owner.Quests.ActiveQuests.TryGetValue(TemplateId, out var active) && ReferenceEquals(active, this) &&
            QuestSteps.TryGetValue(Step, out var step) &&
            step.Components.TryGetValue(component.Template.Id, out var current) && ReferenceEquals(current, component);
    }

    private bool IsAvailableControlNpc(Npc npc)
    {
        return Owner is Character owner && npc is { IsDead: false, Despawned: false, CombatRetired: false, Hp: > 0 } &&
            owner.ParentWorld != null && ReferenceEquals(npc.ParentWorld, owner.ParentWorld) &&
            owner.Transform != null && npc.Transform != null &&
            owner.Transform.WorldId == npc.Transform.WorldId && owner.Transform.InstanceId == npc.Transform.InstanceId;
    }

    internal bool HasFollowNpcTemplate(uint npcTemplateId)
    {
        return npcTemplateId != 0 && Template.Components.Values.Any(component =>
            component.NpcAiId == QuestNpcAiName.FollowUnit && component.NpcId == npcTemplateId);
    }

    internal bool TryGetFollowNpc(uint npcTemplateId, out Npc npc)
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            foreach (var bound in _followNpcs.Values)
            {
                if (bound.Npc.TemplateId == npcTemplateId && IsAvailableControlNpc(bound.Npc) &&
                    ReferenceEquals(bound.Npc.Ai, bound.Ai) && bound.Ai.IsQuestFollowOwner(this))
                {
                    npc = bound.Npc;
                    return true;
                }
            }
            npc = null;
            return false;
        }
    }

    internal bool IsFollowNpcInsideSphere(uint npcTemplateId, SphereQuest sphere)
    {
        return sphere != null && Owner?.Transform != null && sphere.Contains(Owner.Transform.World.Position) &&
            TryGetFollowNpc(npcTemplateId, out var npc) && sphere.Contains(npc.Transform.World.Position);
    }

    internal void ReleaseNpcControl()
    {
        lock (SaveManager.PersistenceSyncRoot)
        {
            foreach (var bound in _followNpcs.Values)
                bound.Ai.StopQuestFollow(this);
            _followNpcs.Clear();
            _componentNpcSources.Clear();
            _acceptedNpc = null;
            if (_npcControlEventsAttached && Owner is Character owner)
                UnitEvents.UpdateSubscription(ref owner.Events.OnDisconnect, OnNpcControlOwnerDisconnected, false);
            _npcControlEventsAttached = false;
        }
    }

    private void OnNpcControlLost()
    {
        // NPC removal can hold a spawner lock. Queue evaluation without the persistence lock.
        _questManager.EnqueueEvaluation(this);
    }

    private void OnNpcControlOwnerDisconnected(object sender, OnDisconnectArgs args)
    {
        ReleaseNpcControl();
    }
}
