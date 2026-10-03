using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Tasks.World;

namespace AAEmu.Game.Models.Game.Skills.Effects;

public class NpcSpawnerSpawnEffect : EffectTemplate
{
    public uint SpawnerId { get; set; }
    public float LifeTime { get; set; }
    public bool DespawnOnCreatorDeath { get; set; }
    public bool UseSummonerAggroTarget { get; set; }
    public bool ActivationState { get; set; }

    public override bool OnActionTime => false;

    public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
        CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
        CompressedGamePackets packetBuilder = null)
    {
        if (caster is Npc { Despawned: true } ||
            caster is Npc { TowerDefenseSpawnToken.Lifetime.IsCancelled: true })
            return;
        Logger.Info($"NpcSpawnerSpawnEffect: SpawnerId={SpawnerId}, LifeTime={LifeTime}, UseSummonerAggroTarget={UseSummonerAggroTarget}, ActivationState={ActivationState}");

        // aaemu-cluster#92 (#99): search both the normal and the pinned/event spawner dictionaries,
        // effects referencing pinned spawner template ids could not find them before.
        var eventToken = (caster as Npc)?.TowerDefenseSpawnToken;
        var spawners = eventToken == null
            ? caster.ParentWorld.SpawnManager.GetNpcSpawnersBySpawnerTemplateId(SpawnerId)
            : caster.ParentWorld.SpawnManager.GetEventSpawnersBySpawnerTemplateId(SpawnerId, eventToken.SiteKey);
        if (spawners is not { Count: not 0 })
            Logger.Info($"NpcSpawnerSpawnEffect: SpawnerId={SpawnerId} not found in spawners.");
        else
        {
            foreach (var spawner in spawners)
            {
                // aaemu-cluster#92 (#99): the effect's activation_state drives the runtime gate,
                // but only for event-managed spawners (authored activation_state=f). Permanently
                // toggling a normal always-active world spawner from a one-shot skill effect would
                // leave it dark (or force it hot) until restart. (review)
                if (spawner.Template?.ActivationState == false)
                {
                    if (ActivationState)
                        spawner.Activate();
                    else
                        spawner.Deactivate();
                }

                // spawn in the same world as for caster
                spawner.Position.WorldId = caster.Transform.WorldId;
                var childToken = eventToken == null
                    ? null
                    : eventToken with
                    {
                        ActionKey = $"{eventToken.ActionKey}/effect:{Id}/placement:{spawner.EventPlacementId}",
                        CreatorObjId = caster.ObjId,
                        DespawnOnCreatorDeath = DespawnOnCreatorDeath
                    };
                var npc = childToken == null ? spawner.ForceSpawn(0) : spawner.ForceSpawnOwned(childToken);
                if (npc == null)
                    continue;

                if (childToken != null)
                    spawner.Deactivate();

                ApplySpawnedOccurrence(npc, caster, target);
                Logger.Info($"NpcSpawnerSpawnEffect: Do Spawn effect id={Id}, Npc unitId={spawner.UnitId} spawnerId={SpawnerId} worldId={caster.Transform.WorldId}");
            }
        }
    }

    internal void ApplySpawnedOccurrence(Npc first, BaseUnit caster, BaseUnit target)
    {
        first.Spawner.SuppressAutomaticRespawn(first);
        var members = first.GroupInstance?.GetMembers() ?? [first];
        foreach (var npc in members)
        {
            if (UseSummonerAggroTarget && npc.Ai != null)
            {
                if (LifeTime == 0)
                {
                    // Preserve the ordinary effect's nearby hostile-NPC target rule.
                    var units = WorldManager.GetAround<Npc>(npc, npc.Template.SightRangeScale * 30f);
                    foreach (var enemy in units.Where(enemy => npc.CanAttack(enemy)))
                    {
                        npc.AddUnitAggro(AggroKind.Damage, enemy, 1);
                        npc.Ai?.OnAggroTargetChanged();
                        enemy.AddUnitAggro(AggroKind.Damage, npc, 1);
                    }
                }
                else
                {
                    npc.AddUnitAggro(AggroKind.Damage, target is Npc targetNpc ? targetNpc : (Unit)caster, 1);
                    npc.Ai?.OnAggroTargetChanged();
                }
            }

            if (LifeTime > 0)
                ScheduleDespawn(npc, TimeSpan.FromSeconds(LifeTime));
        }
    }

    protected virtual void ScheduleDespawn(Npc npc, TimeSpan delay)
    {
        TaskManager.Instance.Schedule(new NpcSpawnerDoDespawnTask(npc), delay);
    }
}
