using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class NpcInteractionGrantTests
{
    private readonly Dictionary<FieldInfo, object> _instances = [];
    private SkillGrantGameData _grants;
    private ProbeCharacter _character;
    private WorldInstance _world;

    [Before(Test)]
    public void SetUp()
    {
        SetInstance(new SkillManager(null, null));
        _grants = new SkillGrantGameData();
        using var data = CreateData();
        _grants.Load(data);
        SetInstance(_grants);
        SetInstance(new WorldManager(null, null, null, null, null));
        var models = new ModelManager();
        SetField(models, "_modelTypes", new Dictionary<uint, ModelType>());
        SetField(models, "_models", new Dictionary<string, Dictionary<uint, Model>>());
        SetInstance(models);
        _world = CreateWorld();
        _character = new ProbeCharacter { Id = 70, ObjId = 70, Hp = 100, Mp = 100 };
        _character.Skills = new CharacterSkills(_character);
        _character.Abilities = new CharacterAbilities(_character);
        AddToWorld(_character);
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _instances)
            field.SetValue(null, previous);
    }

    [Test]
    [Arguments(12, 13640u, 22780u)]
    [Arguments(1, 12474u, 21366u)]
    [Arguments(5, 12477u, 21521u)]
    [Arguments(2, 10894u, 21335u)]
    [Arguments(11, 13660u, 23146u)]
    public async Task NpcGrant_AuthoredSkillNeedsTheSelectedNpcSet(int setId, uint npcId, uint skillId)
    {
        var npc = AddNpc(setId, npcId);
        var skill = new SkillTemplate { Id = skillId, TargetType = SkillTargetType.AnyUnit };
        var target = new SkillCastUnitTarget(npc.ObjId);

        await Assert.That(CanUse(skill, target)).IsTrue();
        npc.Template.NpcInteractionSetId = 999;
        await Assert.That(CanUse(skill, target)).IsFalse();
        npc.Template.NpcInteractionSetId = setId;
        await Assert.That(CanUse(skill, new SkillCastUnitTarget(_character.ObjId))).IsFalse();
        await Assert.That(CanUse(skill, new SkillCastDoodadTarget { ObjId = npc.ObjId })).IsFalse();
    }

    [Test]
    [Arguments("missing")]
    [Arguments("despawned")]
    [Arguments("removed")]
    [Arguments("other-world")]
    [Arguments("other-instance")]
    [Arguments("reused-id")]
    public async Task NpcGrant_StaleOrUnrelatedTargetDoesNotKeepTheGrant(string state)
    {
        var npc = AddNpc(5, 12477);
        var skill = new SkillTemplate { Id = 21521, TargetType = SkillTargetType.AnyUnit };
        var target = new SkillCastUnitTarget(npc.ObjId);
        await Assert.That(CanUse(skill, target)).IsTrue();

        switch (state)
        {
            case "missing":
                target.ObjId = 999;
                break;
            case "despawned":
                npc.Despawned = true;
                break;
            case "removed":
                _world.RemoveObject(npc);
                break;
            case "other-world":
                SetParentWorld(npc, CreateWorld());
                break;
            case "other-instance":
                npc.Transform.InstanceId = 1;
                // Keep the stale registry entry so the explicit instance check is exercised.
                SetParentWorld(npc, _world);
                break;
            case "reused-id":
                _world.RemoveObject(npc);
                AddNpc(1, 12474);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        await Assert.That(CanUse(skill, target)).IsFalse();
    }

    [Test]
    [Arguments(55, 12, 13640u, 22780u, 15, 0)]
    [Arguments(14, 1, 12474u, 21366u, 10, 100)]
    [Arguments(10, 5, 12477u, 21521u, 10, 100)]
    [Arguments(2, 2, 10894u, 21335u, 10, 4000)]
    [Arguments(54, 11, 13660u, 23146u, 4, 2000)]
    public async Task ExactClient_NpcSkillRouteUsesTheAuthoredGrantAndRange(
        int interactionId, int setId, uint npcId, uint skillId, int rangeMetres, int castMilliseconds)
    {
        using var connection = OpenCompact();
        _grants.Load(connection);
        var skill = new SkillTemplate { Id = skillId };
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.id, n.npc_interaction_set_id, s.target_type_id, s.target_selection_id,
                       s.target_relation_id, s.min_range, s.max_range, s.casting_time
                FROM npcs n
                JOIN npc_interaction_sets sets ON sets.id = n.npc_interaction_set_id
                JOIN npc_interactions i ON i.npc_interaction_set_id = sets.id
                JOIN skills s ON s.id = i.skill_id
                WHERE n.id = $npc AND s.id = $skill
                """;
            command.Parameters.AddWithValue("$npc", npcId);
            command.Parameters.AddWithValue("$skill", skillId);
            using var reader = command.ExecuteReader();
            await Assert.That(reader.Read()).IsTrue();
            await Assert.That(reader.GetInt32(0)).IsEqualTo(interactionId);
            await Assert.That(reader.GetInt32(1)).IsEqualTo(setId);
            await Assert.That(reader.GetInt32(2)).IsEqualTo((int)SkillTargetType.AnyUnit);
            await Assert.That(reader.GetInt32(3)).IsEqualTo(2);
            await Assert.That(reader.GetInt32(4)).IsEqualTo((int)SkillTargetRelation.Any);
            await Assert.That(reader.GetInt32(5)).IsEqualTo(0);
            await Assert.That(reader.GetInt32(6)).IsEqualTo(rangeMetres);
            await Assert.That(reader.GetInt32(7)).IsEqualTo(castMilliseconds);
            skill.TargetType = (SkillTargetType)reader.GetInt32(2);
            skill.MinRange = reader.GetInt32(5);
            skill.MaxRange = reader.GetInt32(6);
            await Assert.That(reader.Read()).IsFalse();
        }

        var npc = AddNpc(setId, npcId);
        var target = new SkillCastUnitTarget(npc.ObjId);
        await Assert.That(CanUse(skill, target)).IsTrue();
        npc.Template.NpcInteractionSetId = 999;
        await Assert.That(CanUse(skill, target)).IsFalse();
        npc.Template.NpcInteractionSetId = setId;

        // Skill.Use applies this normal range check after the NPC grant check.
        npc.Transform.Local.Position = new Vector3(rangeMetres, 0, 0);
        await Assert.That(SkillRange.Check(new Skill(skill), _character, npc)).IsEqualTo(SkillResult.Success);
        npc.Transform.Local.Position = new Vector3(rangeMetres + 0.01f, 0, 0);
        await Assert.That(SkillRange.Check(new Skill(skill), _character, npc)).IsEqualTo(SkillResult.TooFarRange);
    }

    private bool CanUse(SkillTemplate skill, SkillCastTarget target)
    {
        return SkillCastAuthorization.CanUseCharacterSkill(_character, skill,
            new SkillCasterUnit(_character.ObjId), target);
    }

    private Npc AddNpc(int setId, uint npcId)
    {
        var npc = new Npc
        {
            ObjId = 81, Hp = 100,
            Template = new NpcTemplate { Id = npcId, NpcInteractionSetId = setId }
        };
        AddToWorld(npc);
        return npc;
    }

    private void AddToWorld(GameObject obj)
    {
        SetParentWorld(obj, _world);
        _world.AddObject(obj);
    }

    private static void SetParentWorld(GameObject obj, WorldInstance world)
    {
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(obj, world);
    }

    private static WorldInstance CreateWorld()
    {
        return new WorldInstance(new WorldTemplate
        {
            Id = 1, CellX = 1, CellY = 1,
            ZoneKeyByRegions = new uint[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL]
        }, 0, true, 0);
    }

    private static SqliteConnection CreateData()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        // These five relations are identical in the original r208022 and deployed server compacts.
        command.CommandText = """
            CREATE TABLE buff_skills(buff_id INTEGER, skill_id INTEGER);
            CREATE TABLE npc_interactions(id INTEGER, npc_interaction_set_id INTEGER, skill_id INTEGER);
            CREATE TABLE doodad_funcs(doodad_func_group_id INTEGER, func_skill_id INTEGER, actual_func_type TEXT, actual_func_id INTEGER);
            CREATE TABLE doodad_func_fake_uses(id INTEGER, fake_skill_id INTEGER);
            CREATE TABLE doodad_func_uses(id INTEGER, skill_id INTEGER);
            INSERT INTO npc_interactions VALUES(55,12,22780),(14,1,21366),(10,5,21521),(2,2,21335),(54,11,23146);
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private static SqliteConnection OpenCompact()
    {
        var compact = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(compact), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = compact, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        return connection;
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _instances.TryAdd(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    private sealed class ProbeCharacter() : Character(null)
    {
        public override void BroadcastPacket(GamePacket packet, bool self) { }
        public override void OnZoneChange(uint lastZoneKey, uint newZoneKey) { }
        public override double ApplySkillModifiers(Skill skill, SkillAttribute attribute, double baseValue) => baseValue;
    }
}
