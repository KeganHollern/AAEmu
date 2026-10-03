using AAEmu.Game.Core.Packets;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    public async Task VehiclePlacement_DynamicScopeIncludesVisibleObjectsAndExcludesRemoteObjects()
    {
        var (world, current, _) = PrepareVehiclePlacementBoundary();
        world.Regions[1, 1] = new Region(world, 1, 1, 0);
        world.Regions[10, 10] = new Region(world, 10, 10, 0);
        var remote = new Slave { ObjId = 901, ModelId = uint.MaxValue };
        remote.Transform.Local.SetPosition(650, 650, 10);
        world.Regions[10, 10].AddObject(remote);
        var nearby = new Slave { ObjId = 902, ModelId = uint.MaxValue };
        nearby.Transform.Local.SetPosition(110, 110, 10);
        world.Regions[1, 1].AddObject(nearby);

        var objects = SlaveManager.GetPlacementObjects(world, _owner.Transform.World.Position);

        await Assert.That(objects.Contains(current)).IsTrue();
        await Assert.That(objects.Contains(nearby)).IsTrue();
        await Assert.That(objects.Contains(remote)).IsFalse();
    }

    [Test]
    public async Task ApplyEffects_SummonPositionReachesTheRealSpawnSlaveEffect()
    {
        var (world, current, scroll) = PrepareVehiclePlacementBoundary();
        scroll.Template.UseSkillId = 50;
        var skill = NewSkill();
        skill.Template.ConsumeLaborPower = 0;
        skill.Template.TargetType = SkillTargetType.SummonPos;
        skill.Template.Effects.Add(new SkillEffect
        {
            ApplicationMethod = SkillEffectApplicationMethod.SourceOnce, Chance = 100, EndLevel = byte.MaxValue,
            Template = new SpecialEffect { SpecialEffectTypeId = SpecialType.SpawnSlave }
        });
        var selected = ValidVehicleTarget();
        SlavePlacementRequest? observed = null;
        world.SlaveManager.PlacementGeometry = (_, _, request) =>
        {
            observed = request;
            return ErrorMessageType.SlaveSpawnErrorInvalidArea;
        };
        var removed = false;
        world.SlaveManager.SaveForRemoval = _ => { removed = true; return true; };

        skill.ApplyEffects(_owner, new SkillItem(_owner.ObjId, scroll.Id, scroll.TemplateId), _owner, selected, null);

        await Assert.That(observed.HasValue).IsTrue();
        await Assert.That(observed!.Value.Position).IsEqualTo(new System.Numerics.Vector3(selected.PosX, selected.PosY, selected.PosZ));
        await Assert.That(observed.Value.Yaw).IsEqualTo(selected.PosRot);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(removed).IsFalse();
        await Assert.That(current.AttachmentsRetired).IsFalse();
    }

    [Test]
    public async Task ApplyEffects_OtherSummonPositionEffectsKeepTheirUnitTarget()
    {
        PrepareVehiclePlacementBoundary();
        var skill = NewSkill();
        skill.Template.ConsumeLaborPower = 0;
        skill.Template.TargetType = SkillTargetType.SummonPos;
        SkillCastTarget observed = null;
        skill.Template.Effects.Add(new SkillEffect
        {
            ApplicationMethod = SkillEffectApplicationMethod.SourceOnce, Chance = 100, EndLevel = byte.MaxValue,
            Template = new TargetProbeEffect(target => observed = target)
        });
        skill.ApplyEffects(_owner, new SkillCasterUnit(_owner.ObjId), _owner, ValidVehicleTarget(), null);
        await Assert.That(observed is SkillCastUnitTarget).IsTrue();
        await Assert.That(observed!.ObjId).IsEqualTo(_owner.ObjId);
    }

    private sealed class TargetProbeEffect(Action<SkillCastTarget> observe) : EffectTemplate
    {
        public override bool OnActionTime => false;
        public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
            CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
            CompressedGamePackets packetBuilder = null) => observe(targetObj);
    }
}
