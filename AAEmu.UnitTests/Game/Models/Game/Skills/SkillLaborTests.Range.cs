using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Use_RemoteObject_RejectsBeforeLaborManaCooldownAndCast(bool vehicle)
    {
        SetInstance(new SkillRequirementsGameData());
        SetInstance(new ZoneManager(null, null));
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>>());
        SetInstance(models);
        var world = new WorldInstance(new WorldTemplate { Id = 10 }, 0, true, 0);
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_owner, world);
        BaseUnit target = vehicle ? new Slave() : new Doodad();
        target.ObjId = 80;
        target.Transform.Local.Position = new Vector3(100, 0, 0);
        var units = (ConcurrentDictionary<uint, BaseUnit>)typeof(WorldInstance)
            .GetField("_baseUnits", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;
        units[target.ObjId] = target;
        var skill = NewSkill();
        skill.Template.TargetType = SkillTargetType.Doodad;
        skill.Template.MaxRange = 3;
        var mana = _owner.Mp;
        var result = skill.Use(_owner, new SkillCasterUnit(70), new SkillCastDoodadTarget { ObjId = 80 }, null, true, out _);
        await Assert.That(result).IsEqualTo(SkillResult.TooFarRange);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)20);
        await Assert.That(_owner.Mp).IsEqualTo(mana);
        await Assert.That(_material.Count).IsEqualTo(3);
        await Assert.That(_owner.SkillTask).IsNull();
        await Assert.That(_owner.Packets).IsEmpty();
        await Assert.That(skill.TlId).IsEqualTo((ushort)0);
    }
}
