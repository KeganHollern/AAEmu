using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Indun.Events;

internal class IndunEventNpcCombatEndeds : IndunEvent
{
    public uint NpcId { get; set; }

    protected override void SubscribeCore(WorldInstance worldInstance)
    {
        worldInstance.Events.OnUnitCombatEnd += OnUnitCombatEnd;
    }

    protected override void UnSubscribeCore(WorldInstance worldInstance)
    {
        worldInstance.Events.OnUnitCombatEnd -= OnUnitCombatEnd;
    }

    private void OnUnitCombatEnd(object sender, OnUnitCombatEndArgs args)
    {
        if (args.Npc is not Npc npc || sender is not WorldInstance world) { return; }
        if (npc.TemplateId != NpcId || npc.Transform.InstanceId != world.Id || !IsSubscribed(world)) { return; }

        Logger.Warn($"{npc.TemplateId} has left combat.");
        IndunManager.Instance.DoIndunActions(StartActionId, world);
    }
}
