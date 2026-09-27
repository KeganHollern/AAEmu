using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Indun.Events;

internal class IndunEventNpcCombatStarteds : IndunEvent
{
    public uint NpcId { get; set; }

    protected override void SubscribeCore(WorldInstance worldInstance)
    {
        worldInstance.Events.OnUnitCombatStart += OnNpcCombatStarted;
    }

    protected override void UnSubscribeCore(WorldInstance worldInstance)
    {
        worldInstance.Events.OnUnitCombatStart -= OnNpcCombatStarted;
    }

    private void OnNpcCombatStarted(object sender, OnUnitCombatStartArgs args)
    {
        if (args.Npc is not Npc npc || sender is not WorldInstance world) { return; }
        if (npc.TemplateId != NpcId || npc.Transform.InstanceId != world.Id || !IsSubscribed(world)) { return; }

        Logger.Warn($"{npc.TemplateId} has entered combat.");

        IndunManager.Instance.DoIndunActions(StartActionId, world);
    }
}
