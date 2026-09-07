using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Indun.Events;

internal class IndunEventNpcKilleds : IndunEvent
{
    public uint NpcId { get; set; }

    protected override void SubscribeCore(WorldInstance worldInstance)
    {
        worldInstance.Events.OnUnitKilled += OnUnitKilled;
    }

    protected override void UnSubscribeCore(WorldInstance worldInstance)
    {
        worldInstance.Events.OnUnitKilled -= OnUnitKilled;
    }

    private void OnUnitKilled(object sender, OnUnitKilledArgs args)
    {
        if (args.Victim is not Npc npc || sender is not WorldInstance world) { return; }
        if (npc.TemplateId != NpcId) { return; }

        Logger.Warn($"IndunEventNpcKilleds - {NpcId}, {Id}");
        IndunManager.Instance.DoIndunActions(StartActionId, world);
    }
}
