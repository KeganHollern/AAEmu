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

    [Test]
    public async Task ExactCompact_GroundGustCreatesTwoTimedDoodadsAndTwoBuffTimeouts()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_COMBAT_TEST_COMPACT to the r208022 compact.");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT spawn.value1, spawn.value2, clout.duration, clout.tick, shape.value1, COALESCE(aoe.effect_id, 0)
            FROM skill_effects se
            JOIN effects e ON e.id = se.effect_id AND e.actual_type = 'SpecialEffect'
            JOIN special_effects spawn ON spawn.id = e.actual_id AND spawn.special_effect_type_id = 15
            JOIN doodad_func_groups g ON g.doodad_almighty_id = spawn.value1 AND g.doodad_func_group_kind_id = 1
            JOIN doodad_phase_funcs phase ON phase.doodad_func_group_id = g.id AND phase.actual_func_type = 'DoodadFuncClout'
            JOIN doodad_func_clouts clout ON clout.id = phase.actual_func_id
            JOIN aoe_shapes shape ON shape.id = clout.aoe_shape_id AND shape.kind_id = 1
            LEFT JOIN doodad_func_clout_effects aoe ON aoe.doodad_func_clout_id = clout.id
            WHERE se.skill_id = 11592 AND clout.buff_id IS NULL AND clout.use_origin_source = 't'
            ORDER BY spawn.value1
            """;
        using (var reader = command.ExecuteReader())
        {
            var rows = new List<(int Doodad, int Delay, int Lifetime, int Tick, float Radius, int Effect)>();
            while (reader.Read())
                rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetFloat(4), reader.GetInt32(5)));
            await Assert.That(rows.SequenceEqual([(679, 1000, 500, 300, 5f, 1935), (1203, 1000, 6500, 0, 10f, 0)])).IsTrue();
        }

        command.CommandText = """
            WITH RECURSIVE chain(n, effect_id) AS (
                SELECT 1, 1935
                UNION ALL
                SELECT n + 1, t.effect_id FROM chain
                JOIN effects e ON e.id = chain.effect_id AND e.actual_type = 'BuffEffect'
                JOIN buff_effects be ON be.id = e.actual_id
                JOIN buff_triggers t ON t.buff_id = be.buff_id AND t.event_id = 6 AND t.effect_on_source = 't'
                WHERE n < 4
            )
            SELECT b.id, b.duration, COALESCE(b.skill_controller_id, 0)
            FROM chain JOIN effects e ON e.id = chain.effect_id AND e.actual_type = 'BuffEffect'
            JOIN buff_effects be ON be.id = e.actual_id AND be.chance = 100
            JOIN buffs b ON b.id = be.buff_id ORDER BY chain.n
            """;
        using (var reader = command.ExecuteReader())
        {
            var rows = new List<(int Buff, int Duration, int Controller)>();
            while (reader.Read())
                rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
            await Assert.That(rows.SequenceEqual([(613, 300, 0), (413, 6000, 5815), (415, 15000, 0)])).IsTrue();
        }
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
