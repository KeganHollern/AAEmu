using AAEmu.Game.Core.Packets;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects;

public class NpcSpawnerDespawnEffect : EffectTemplate
{
    public uint SpawnerId { get; set; }

    public override bool OnActionTime => false;

    public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
        CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
        CompressedGamePackets packetBuilder = null)
    {
        Logger.Info($"NpcSpawnerDespawnEffect: SpawnerId={SpawnerId}");

        // aaemu-cluster#92 (#99): this was a stub. Resolve every spawner bound to this compact
        // npc_spawners template id (normal and pinned/event) and remove its NPCs without
        // scheduling respawns. Deactivate first so an actively ticking spawner does not
        // immediately repopulate what we just removed.
        var spawners = caster.ParentWorld.SpawnManager.GetNpcSpawnersBySpawnerTemplateId(SpawnerId);
        if (spawners.Count == 0)
        {
            Logger.Info($"NpcSpawnerDespawnEffect: SpawnerId={SpawnerId} not found in spawners.");
            return;
        }

        foreach (var spawner in spawners)
        {
            spawner.Deactivate();
            spawner.DespawnAll();
            Logger.Debug($"NpcSpawnerDespawnEffect id={Id}, Npc unitId={spawner.UnitId} spawnerId={SpawnerId}");
        }
    }
}
