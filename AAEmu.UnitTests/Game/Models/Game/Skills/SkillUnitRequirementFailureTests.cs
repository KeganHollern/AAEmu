using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class SkillUnitRequirementFailureTests
{
    private const uint SkillId = 12049;
    private readonly Dictionary<FieldInfo, object> _previousInstances = [];
    private CharacterMock _caster;
    private SkillTemplate _template;

    [Before(Test)]
    public void SetUp()
    {
        SetInstance(new PermissionManager(Mock.Of<IAccountManager>().Object));
        using var data = new SqliteConnection("Data Source=:memory:");
        data.Open();
        using var command = data.CreateCommand();
        command.CommandText = """
            CREATE TABLE unit_reqs (
                id INTEGER, owner_id INTEGER, owner_type TEXT,
                kind_id INTEGER, value1 INTEGER, value2 INTEGER);
            INSERT INTO unit_reqs VALUES (45139, 12049, 'Skill', 68, 0, 0);
            """;
        command.ExecuteNonQuery();
        var requirements = new UnitRequirementsGameData();
        requirements.Load(data);
        SetInstance(requirements);

        _template = new SkillTemplate
        {
            Id = SkillId,
            TargetType = SkillTargetType.Self,
            ManaCost = 40,
            CooldownTime = 12000,
            CustomGcd = 300,
            CancelOngoingBuffs = true
        };
        var skills = new SkillManager(null, null);
        SetField(skills, "_skills", new Dictionary<uint, SkillTemplate> { [SkillId] = _template });
        SetField(skills, "_comboFollowupSkills", new HashSet<uint>());
        SetInstance(skills);
        SetInstance(new DuelManager());

        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>
        {
            [1] = new() { SubId = 1, SubType = "VehicleModel" }
        });
        SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>>
        {
            ["VehicleModel"] = new() { [1] = new VehicleModel { UseWheeledVehicleSimulation = true } }
        });
        SetInstance(models);

        _caster = new CharacterMock { Id = 7, ObjId = 70, Name = "Caster", Hp = 100, Mp = 100 };
        var world = new WorldInstance(new WorldTemplate { Id = 10 }, 0, true, 0);
        // This unregistered fixture has no runtime resources to clean up.
        GC.SuppressFinalize(world);
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_caster, world);
        _caster.Skills = new(_caster);
        _caster.Skills.Skills.Add(SkillId, new Skill(_template));
        var vehicle = new Slave { ModelId = 1, VehicleVelocity = new Vector3(6, 0, 0) };
        vehicle.AttachedCharacters.Add(AttachPointKind.Driver, _caster);
        _caster.AttachedPoint = AttachPointKind.Driver;
        _caster.Transform.Parent = vehicle.Transform;
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, instance) in _previousInstances)
            field.SetValue(null, instance);
    }

    [Test]
    public async Task Use_MovingVehicleFailurePreservesNativeDetailBeforeSkillCosts()
    {
        var buffs = Mock.Of<IBuffs>();
        buffs.GetEffectsByType(typeof(BuffTemplate)).Returns([]);
        _caster.Buffs = buffs.Object;
        var skill = new Skill(_template);
        var originalGcd = _caster.SkillLastUsed;

        var result = skill.Use(_caster, new SkillCasterUnit(70), new SkillCastUnitTarget(70),
            new SkillObject(), false, out var shortDetail, out var longDetail);

        await Assert.That((byte)result).IsEqualTo((byte)0x7f);
        await Assert.That(shortDetail).IsEqualTo((ushort)0x0308);
        await Assert.That(longDetail).IsEqualTo(0u);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(skill.TlId).IsEqualTo((ushort)0);
        await Assert.That(_caster.Mp).IsEqualTo(100);
        await Assert.That(_caster.SkillLastUsed).IsEqualTo(originalGcd);
        await Assert.That(_caster.Cooldowns.CheckCooldown(SkillId)).IsFalse();
        await Assert.That(Skill.IsExecuting(_caster)).IsFalse();
        buffs.TriggerRemoveOn(BuffRemoveOn.StartSkill, 0).WasCalled(Times.Never);
    }

    [Test]
    public void StartSkill_MovingVehicleFailureSendsExactNativePacket()
    {
        var session = Mock.Of<ISession>();
        var connection = new GameConnection(session.Object) { ActiveChar = _caster };
        _caster.Connection = connection;
        var request = new PacketStream().Write(SkillId).Write(new SkillCasterUnit(70))
            .Write(new SkillCastUnitTarget(70)).Write((byte)0);
        // Level-1 envelope, opcode 0x00a1, zero timeline/cast times, flags 3,
        // native error 0x7f and ushort message 0x0308. No uint field follows.
        byte[] expected =
        [
            0x1e, 0x00, 0xdd, 0x01, 0x00, 0x00, 0xa1, 0x00,
            0x11, 0x2f, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46,
            0x00, 0x00, 0x00, 0x46, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x03, 0x7f, 0x08, 0x03
        ];

        new CSStartSkillPacket { Connection = connection }.Read(request);

        session.SendPacket(Is<byte[]>(packet => packet.SequenceEqual(expected))).WasCalled(Times.Once);
        session.SendPacket(Any<byte[]>()).WasCalled(Times.Once);
    }

    [Test]
    public async Task Use_NonUnitFailureClearsBothDetails()
    {
        var result = new Skill(_template).Use(null, null, null, null, false,
            out var shortDetail, out var longDetail);

        await Assert.That(result).IsEqualTo(SkillResult.InvalidSource);
        await Assert.That(shortDetail).IsEqualTo((ushort)0);
        await Assert.That(longDetail).IsEqualTo(0u);
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
