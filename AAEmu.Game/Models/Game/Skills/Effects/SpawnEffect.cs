using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.Skills.Effects;

public class SpawnEffect : EffectTemplate
{
    public BaseUnitType OwnerTypeId { get; set; }
    public uint SubType { get; set; }
    public uint PosDirId { get; set; }
    public float PosAngle { get; set; }
    public float PosDistance { get; set; }
    public uint OriDirId { get; set; }
    public float OriAngle { get; set; }
    public bool UseSummonerFaction { get; set; }
    public float LifeTime { get; set; }
    public bool DespawnOnCreatorDeath { get; set; }
    public bool UseSummonerAggroTarget { get; set; }
    public MateState MateStateId { get; set; }

    public override bool OnActionTime => false;

    internal PositionAndRotation ResolveSlaveSpawnPosition(PositionAndRotation source)
    {
        var spawnPosition = source.Clone();
        spawnPosition.AddDistanceToFront(PosDistance);
        spawnPosition.Rotate(spawnPosition.Rotation with { Z = OriAngle.DegToRad() });
        return spawnPosition;
    }

    public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
        CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
        CompressedGamePackets packetBuilder = null)
    {
        Logger.Trace($"SpawnEffect: OwnerTypeId={OwnerTypeId}, SubType={SubType}, UseSummonerFaction={UseSummonerFaction}, LifeTime={LifeTime}");

        switch (OwnerTypeId)
        {
            case BaseUnitType.Npc:
                {
                    var spawner = caster?.ParentWorld.SpawnManager.GetNpcSpawner(SubType, target);
                    if (spawner == null)
                    {
                        Logger.Info($"SpawnEffect: SubType={SubType} not found in spawners.");
                        return;
                    }

                    // dir id 1 = relative to target/spawner.
                    // dir id 2 = relative to caster.
                    var positionRelativeToUnit = PosDirId switch
                    {
                        1 => target,
                        2 => caster,
                        _ => null
                    };
                    var orientationRelativeToUnit = OriDirId switch
                    {
                        1 => target,
                        2 => caster,
                        _ => null
                    };

                    if (positionRelativeToUnit == null || orientationRelativeToUnit == null)
                    {
                        Logger.Warn($"SpawnEffect: Unhandled PosDirId {PosDirId} or OriDirId {OriDirId}");
                        return;
                    }

                    var (xx, yy) = MathUtil.AddDistanceToFrontDeg(PosDistance, positionRelativeToUnit.Transform.World.Position.X, positionRelativeToUnit.Transform.World.Position.Y, PosAngle);

                    spawner.Position.X = xx;
                    spawner.Position.Y = yy;
                    spawner.Position.Z = positionRelativeToUnit.Transform.World.Position.Z;

                    spawner.Position.Yaw = orientationRelativeToUnit.Transform.World.Rotation.Z + OriAngle.DegToRad();

                    spawner.RespawnTime = 0; // don't respawn

                    spawner.DoSpawnEffect(spawner.Id, this, caster, target);
                    break;
                }
            case BaseUnitType.Slave:
                {
                    if (caster is Character player)
                    {
                        // TODO: Implement OriDirId, PosDirId and MateStateId
                        using var transform = player.Transform.CloneDetached();
                        var spawnPosition = ResolveSlaveSpawnPosition(transform.World);
                        transform.Local.SetPosition(spawnPosition.Position, spawnPosition.Rotation);

                        var slave = player.ParentWorld.SlaveManager.Create(SubType, true, transform);
                        if (slave is { Template: null })
                        {
                            Logger.Info($"SpawnEffect: SubType={SubType} not found...");
                            return;
                        }
                        player.ForceDismountAndDespawn(slave, 500000); // delete Slave after 8min 20s
                    }
                    break;
                }
            case BaseUnitType.Mate:
                {
                    if (caster is not Character player || player.ParentWorld is null)
                        break;

                    var template = NpcManager.Instance.GetTemplate(SubType);
                    if (template is null)
                    {
                        Logger.Warn($"SpawnEffect: Mate template {SubType} was not found.");
                        break;
                    }

                    var positionSource = PosDirId == 1 ? target ?? caster : caster;
                    var orientationSource = OriDirId == 1 ? target ?? caster : caster;
                    var actorModel = ModelManager.Instance.GetActorModel(template.ModelId);
                    if (!MateSpawnPositionResolver.TryResolve(
                            positionSource.Transform.World,
                            PosAngle.DegToRad(),
                            PosDistance,
                            actorModel,
                            player.ParentWorld.Template.GeoData,
                            out var spawnPosition))
                    {
                        player.SendErrorMessage(ErrorMessageType.MateCannotSpawnNoSpace);
                        break;
                    }

                    var objId = ObjectIdManager.Instance.GetNextId();
                    var tlId = (ushort)TlIdManager.Instance.GetNextId();
                    var mate = new AAEmu.Game.Models.Game.Units.Mate
                    {
                        ObjId = objId,
                        TlId = tlId,
                        Id = objId,
                        OwnerId = player.Id,
                        OwnerObjId = player.ObjId,
                        TemplateId = template.Id,
                        Template = template,
                        Name = LocalizationManager.Instance.Get("npcs", "name", template.Id, template.Name),
                        ModelId = template.ModelId,
                        Faction = UseSummonerFaction ? player.Faction : FactionManager.Instance.GetFaction(template.FactionId),
                        Level = template.Level,
                        ItemId = 0,
                        UserState = (byte)MateStateId,
                        Experience = ExperienceManager.Instance.GetExpForLevel(template.Level, true),
                        SpawnDelayTime = 0,
                        CurrentTarget = UseSummonerAggroTarget ? target : null,
                        DespawnOnCreatorDeath = DespawnOnCreatorDeath,
                        DbInfo = new MateDb { Id = objId, Owner = player.Id, Level = template.Level }
                    };

                    mate.Transform = positionSource.Transform.CloneDetached(mate);
                    mate.Transform.Local.SetPosition(spawnPosition.Position, spawnPosition.Rotation);
                    mate.Transform.Local.SetZRotation(orientationSource.Transform.World.Rotation.Z + OriAngle.DegToRad());
                    foreach (var mateSkill in MateGameData.Instance.GetMateSkills(template.Id))
                        mate.Skills.Add(mateSkill);
                    foreach (var buffId in template.Buffs)
                    {
                        var buff = SkillManager.Instance.GetBuffTemplate(buffId);
                        buff?.Apply(mate, new SkillCasterUnit(mate.ObjId), mate, null, null, new EffectSource(), null, DateTime.UtcNow);
                    }

                    mate.Hp = mate.MaxHp;
                    mate.Mp = mate.MaxMp;
                    player.ParentWorld.MateManager.AddTemporaryMateAndSpawn(player, mate);
                    if (LifeTime > 0)
                        TaskManager.Instance.Schedule(new AAEmu.Game.Models.Tasks.Mate.TemporaryMateDespawnTask(player.ParentWorld.MateManager, player, mate), TimeSpan.FromSeconds(LifeTime));
                    break;
                }
        }
    }
}
