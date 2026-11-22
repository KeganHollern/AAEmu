using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Skills;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    public async Task HouseDemolition_StartSkillSchedulesTheTwentySecondCast()
    {
        var tasks = new TaskManager(null);
        SetInstance(tasks);
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        SetInstance(new SkillRequirementsGameData());
        SetInstance(new SkillGrantGameData());
        SetInstance(new ZoneManager(null, null));
        SetInstance(new WorldManager(null, null, null, null, null));

        var world = new WorldInstance(new WorldTemplate { Id = 10 }, 0, true, 0);
        var parentWorld = typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _owner.Level = 23;
        _owner.Skills = new CharacterSkills(_owner);
        parentWorld.SetValue(_owner, world);
        world.AddObject(_owner);

        var foundation = new HousingTemplate { Id = 172, Hp = 1500 };
        foundation.BuildSteps.Add(0, new HousingBuildStep { SkillId = 14575, NumActions = 1 });
        var house = new House
        {
            Id = 1, TlId = 1, ObjId = 82, OwnerId = _owner.Id, Hp = foundation.Hp,
            Template = foundation, IsBeingLoadedFromDb = true, CurrentStep = 0
        };
        parentWorld.SetValue(house, world);
        world.AddObject(house);

        const uint skillId = 12945;
        var template = new SkillTemplate
        {
            Id = skillId, TargetType = SkillTargetType.AnyUnit, TargetSelection = SkillTargetSelection.Target,
            CastingTime = 20000, CastingDelayable = true, MaxRange = 20, DefaultGcd = true, TargetAlive = true,
            Effects = [new SkillEffect
            {
                ApplicationMethod = SkillEffectApplicationMethod.Target, StartLevel = 1, EndLevel = 99,
                Friendly = true, NonFriendly = true, Front = true, Back = true, Chance = 100,
                Template = new InteractionEffect { WorldInteraction = WorldInteractionType.Demolish }
            }]
        };
        SetField(SkillManager.Instance, "_skills", new Dictionary<uint, SkillTemplate> { [skillId] = template });
        SetField(SkillManager.Instance, "_comboFollowupSkills", new HashSet<uint>());
        var connection = new GameConnection(Mock.Of<ISession>().Object) { ActiveChar = _owner };
        _owner.Connection = connection;
        var body = new PacketStream().Write(skillId).Write((byte)0).WriteBc(_owner.ObjId)
            .Write((byte)0).WriteBc(house.ObjId).Write((byte)0);
        await Assert.That(body.Count).IsEqualTo(13);

        var before = DateTime.UtcNow;
        new CSStartSkillPacket { Connection = connection }.Read(body);
        var after = DateTime.UtcNow;

        await Assert.That(body.LeftBytes).IsEqualTo(0);
        var started = _owner.Packets.OfType<SCSkillStartedPacket>().SingleOrDefault();
        await Assert.That(started).IsNotNull();
        await Assert.That(started!.RealCastTimeDiv10).IsEqualTo((ushort)2000);
        await Assert.That(started.BaseCastTimeDiv10).IsEqualTo((ushort)2000);
        await Assert.That(tasks.GetQueueCount()).IsEqualTo(1);
        await Assert.That(_owner.SkillTask is CastTask).IsTrue();
        var triggerTime = _owner.SkillTask.TriggerTime;
        await Assert.That(triggerTime >= before.AddSeconds(20) && triggerTime <= after.AddSeconds(20)).IsTrue();
        await Assert.That(_owner.SkillTask.Skill.InitialTarget).IsSameReferenceAs(house);
        await Assert.That(house.OwnerId).IsEqualTo(_owner.Id);
    }
}
