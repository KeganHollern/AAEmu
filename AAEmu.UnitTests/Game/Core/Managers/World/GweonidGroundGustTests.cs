using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Spheres;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public sealed partial class SphereQuestManagerTests
{
    [Test]
    public async Task Tick_GweonidGroundEntry_CastsStockWindOncePerCrossingOnTheEnteringCharacter()
    {
        var template = new WorldTemplate { Name = "main_world", Id = 1, CellX = 12, CellY = 15 };
        var world = new WorldInstance(template, 0, true, 1)
        {
            Regions = new Region[template.CellX * WorldManager.SECTORS_PER_CELL, template.CellY * WorldManager.SECTORS_PER_CELL]
        };
        SetInstance(new UnitRequirementsGameData());
        SeedGroundWindSphere();
        var manager = new SphereQuestManager(world);
        manager.Load();
        var owner = new WindCharacter { Id = 7, ObjId = 70, Transform = new Transform(null), ParentWorld = world };
        var center = new Vector3(11140.6283f, 14855.22668f, 185.87427f);
        var region = world.GetRegionByPos(center);
        SetField(region, "_neighbors", new[] { region });
        SetField(region, "_objects", new GameObject[] { owner });
        SetField(region, "_objectsSize", 1);
        SetField(region, "_playerCount", 1);

        owner.Transform.Local.Position = center + new Vector3(0, 7, 1);
        Tick(manager);
        owner.Transform.Local.Position = center + new Vector3(0, 0, 7);
        Tick(manager);
        await Assert.That(owner.Casts).IsEmpty();

        // Foot position from the ordinary walk on release 989.
        owner.Transform.Local.Position = new Vector3(11140.453125f, 14855.931641f, 185.945602f);
        Tick(manager);
        Tick(manager);
        await Assert.That(owner.Casts.Count).IsEqualTo(1);
        await Assert.That(owner.Casts[0]).IsEqualTo((11592u, (IUnit)owner));

        owner.Transform.Local.Position = center + new Vector3(4, 0, 0);
        Tick(manager);
        owner.Transform.InstanceId = 2;
        owner.Transform.Local.Position = center;
        Tick(manager);
        await Assert.That(owner.Casts.Count).IsEqualTo(1);
        owner.Transform.InstanceId = world.Id;
        Tick(manager);
        await Assert.That(owner.Casts.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ExactCompact_GroundGustUsesStockSkillAndRespectsTheWindRecoveryBuff()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_COMBAT_TEST_COMPACT to the r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        _sphereData.Load(connection);
        var wind = _sphereData.GetSphere(303);
        var skill = _sphereData.GetGuaranteedEntrySkill(wind);
        await Assert.That(skill).IsEqualTo(11592u);
        await Assert.That(_sphereData.GetGuaranteedEntrySkill(_sphereData.GetSphere(101))).IsEqualTo(0u);

        var requirements = new SkillRequirementsGameData();
        requirements.Load(connection);
        var buffs = Mock.Of<IBuffs>();
        var caster = new Unit { Buffs = buffs.Object };
        await Assert.That(requirements.GetFailedRequirement(skill, SkillTargetType.Self, [], caster, caster)).IsEqualTo(0u);
        buffs.CheckBuffTag(132).Returns(true);
        await Assert.That(requirements.GetFailedRequirement(skill, SkillTargetType.Self, [], caster, caster)).IsEqualTo(2u);
    }

    private void SeedGroundWindSphere()
    {
        SetField(_sphereData, "_spheres", new Dictionary<uint, Spheres>
        {
            [303] = new() { Id = 303, EnterOrLeave = true, SphereDetailType = "SphereSkill", SphereDetailId = 3 }
        });
        SetField(_sphereData, "_sphereSkills", new Dictionary<uint, SphereSkills>
        {
            [3] = new() { Id = 3, SkillId = 11592, MinRate = 100, MaxRate = 100 }
        });
    }

    private sealed class WindCharacter : CharacterMock
    {
        public List<(uint Skill, IUnit Target)> Casts { get; } = [];

        public override SkillResult UseSkill(uint skillId, IUnit target)
        {
            Casts.Add((skillId, target));
            return SkillResult.Success;
        }
    }
}
