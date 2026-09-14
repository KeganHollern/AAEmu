using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class PlayerSkillAuthorizationTests
{
    private readonly Dictionary<FieldInfo, object> _instances = [];
    private SkillManager _skills;
    private SkillGrantGameData _grants;
    private ProbeCharacter _character;
    private WorldInstance _world;

    [Before(Test)]
    public void SetUp()
    {
        _skills = new SkillManager(null, null);
        SetField(_skills, "_comboFollowupSkills", new HashSet<uint>());
        SetInstance(_skills);
        _grants = new SkillGrantGameData();
        using var data = CreateData();
        _grants.Load(data);
        SetInstance(_grants);
        SetInstance(new WorldManager(null, null, null, null, null));
        _world = new WorldInstance(new WorldTemplate
        {
            Id = 1, CellX = 1, CellY = 1,
            ZoneKeyByRegions = new uint[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL]
        }, 0, true, 0);
        _character = new ProbeCharacter
        {
            Id = 70, ObjId = 70, Hp = 100, Mp = 100,
            Ability1 = AbilityType.Fight, Ability2 = AbilityType.Magic, Ability3 = AbilityType.Love
        };
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
    [Arguments(16927u)]
    [Arguments(17663u)]
    [Arguments(23587u)]
    public async Task StartSkill_UnownedSkillFailsBeforeCostsAndCooldown(uint skillId)
    {
        var skill = new SkillTemplate
        {
            Id = skillId, AutoLearn = true, NeedLearn = false,
            AbilityId = AbilityType.General, ManaCost = 50, CooldownTime = 10000
        };
        SetField(_skills, "_skills", new Dictionary<uint, SkillTemplate> { [skillId] = skill });
        var session = Mock.Of<ISession>();
        var connection = new GameConnection(session.Object) { ActiveChar = _character };
        _character.Connection = connection;
        var body = new PacketStream().Write(skillId).Write(new SkillCasterUnit(_character.ObjId))
            .Write(new SkillCastUnitTarget(_character.ObjId)).Write((byte)0);
        var lastUsed = _character.SkillLastUsed;
        var globalCooldown = _character.GlobalCooldown;

        new CSStartSkillPacket { Connection = connection }.Read(body);

        await Assert.That(_character.Mp).IsEqualTo(100);
        await Assert.That(_character.SkillLastUsed).IsEqualTo(lastUsed);
        await Assert.That(_character.GlobalCooldown).IsEqualTo(globalCooldown);
        await Assert.That(_character.Cooldowns.CheckCooldown(skillId)).IsFalse();
        session.SendPacket(Is<byte[]>(packet => packet[^1] == (byte)SkillResult.InvalidSkill)).WasCalled(Times.Once);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    [Arguments(2u)]
    [Arguments(3u)]
    [Arguments(4u)]
    [Arguments(14152u)]
    public async Task DefaultSkill_RequiresTheAuthenticatedCaster(uint skillId)
    {
        var skill = new SkillTemplate { Id = skillId };
        SetField(_skills, "_defaultSkills", new Dictionary<uint, DefaultSkill> { [14152] = new() { Template = skill } });
        await Assert.That(CanUse(skill)).IsTrue();
        await Assert.That(SkillCastAuthorization.CanUseCharacterSkill(_character, skill,
            new SkillCasterUnit(71), new SkillCastUnitTarget(70))).IsFalse();
        await Assert.That(SkillCastAuthorization.CanUseCharacterSkill(_character, skill,
            new SkillCasterUnk1(70), new SkillCastUnitTarget(70))).IsFalse();
    }

    [Test]
    public async Task LearnedSkill_DoesNotGrantOtherSkillsAtTheSameAbilityLevel()
    {
        var learned = new SkillTemplate { Id = 11918, AbilityId = AbilityType.Fight, AbilityLevel = 3, NeedLearn = true };
        var child = new SkillTemplate { Id = 12028, AbilityId = AbilityType.Fight, AbilityLevel = 3, NeedLearn = false };
        _character.Skills.Skills.Add(learned.Id, new Skill(learned));
        await Assert.That(CanUse(learned)).IsTrue();
        await Assert.That(CanUse(child)).IsFalse();
    }

    [Test]
    [Arguments(1463u, 0, true)]
    [Arguments(2098u, 60000, true)]
    [Arguments(1463u, -1, false)]
    [Arguments(999u, 0, false)]
    public async Task BuffSkill_NeedsAnActiveGrantAndLosesItOnRemoval(uint buffId, int duration, bool expected)
    {
        var effects = GetEffects(_character.Buffs);
        effects.Add(new Buff(_character, _character, new SkillCasterUnit(70),
            new BuffTemplate { Id = buffId }, null, DateTime.UtcNow) { Duration = duration, InUse = true });
        var skill = new SkillTemplate { Id = 17663 };
        await Assert.That(CanUse(skill)).IsEqualTo(expected);
        effects.Clear();
        await Assert.That(CanUse(skill)).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task BuffSkill_InactiveOrFinishedBuffDoesNotGrantSkill(bool inUse, bool finishing)
    {
        GetEffects(_character.Buffs).Add(new Buff(_character, _character, new SkillCasterUnit(70),
            new BuffTemplate { Id = 1463 }, null, DateTime.UtcNow)
        {
            InUse = inUse, State = finishing ? EffectState.Finishing : EffectState.Created
        });
        await Assert.That(CanUse(new SkillTemplate { Id = 17663 })).IsFalse();
    }

    [Test]
    public async Task DoodadGrant_UsesTheTargetCurrentPhaseAndNeverTheZeroSkillFallback()
    {
        var doodad = new Doodad() { ObjId = 80 };
        AddToWorld(doodad);
        SetField(doodad, "_funcGroupId", 100u);
        var target = new SkillCastDoodadTarget { ObjId = 80 };
        await Assert.That(CanUse(new SkillTemplate { Id = 11111 }, target)).IsTrue();
        await Assert.That(CanUse(new SkillTemplate { Id = 13822 }, target)).IsTrue();
        await Assert.That(CanUse(new SkillTemplate { Id = 16927 }, target)).IsFalse();
        SetField(doodad, "_funcGroupId", 200u);
        await Assert.That(CanUse(new SkillTemplate { Id = 11111 }, target)).IsFalse();
        await Assert.That(CanUse(new SkillTemplate { Id = 13822 }, target)).IsFalse();
        target.ObjId = 999;
        await Assert.That(CanUse(new SkillTemplate { Id = 11111 }, target)).IsFalse();
    }

    [Test]
    public async Task NpcGrant_RequiresTheAuthoredInteractionSet()
    {
        var npc = new Npc { ObjId = 81, Template = new NpcTemplate { NpcInteractionSetId = 5 } };
        AddToWorld(npc);
        var target = new SkillCastUnitTarget(81);
        await Assert.That(CanUse(new SkillTemplate { Id = 21521 }, target)).IsTrue();
        await Assert.That(CanUse(new SkillTemplate { Id = 16927 }, target)).IsFalse();
        npc.Template.NpcInteractionSetId = 6;
        await Assert.That(CanUse(new SkillTemplate { Id = 21521 }, target)).IsFalse();
    }

    [Test]
    [Arguments(173u, 3590u, AbilityType.General)]
    [Arguments(188u, 4433u, AbilityType.General)]
    [Arguments(300u, 500u, AbilityType.Death)]
    [Arguments(999u, 0u, AbilityType.General)]
    public async Task LearnBuff_InvalidOrInactivePassiveDoesNotChangeState(uint passiveId, uint buffId, AbilityType ability)
    {
        var templates = new Dictionary<uint, PassiveBuffTemplate>();
        if (buffId != 0)
            templates.Add(passiveId, new() { Id = passiveId, BuffId = buffId, AbilityId = ability, Level = 1 });
        SetField(_skills, "_passiveBuffs", templates);
        var session = Mock.Of<ISession>();
        var connection = new GameConnection(session.Object) { ActiveChar = _character };
        _character.Connection = connection;
        var body = new PacketStream().Write(passiveId);

        new CSLearnBuffPacket { Connection = connection }.Read(body);

        await Assert.That(_character.Skills.PassiveBuffs.Count).IsEqualTo(0);
        await Assert.That(GetEffects(_character.Buffs).Count).IsEqualTo(0);
        session.SendPacket(Any<byte[]>()).WasCalled(Times.Once);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    [Arguments(3, 2, 1, true)]
    [Arguments(2, 2, 1, false)]
    [Arguments(3, 1, 1, false)]
    [Arguments(3, 2, 11, false)]
    public async Task LearnBuff_SelectedPassiveRequiresPointsAndAbilityLevel(int points, int invested, byte level, bool expected)
    {
        var loader = Mock.Of<IExperienceLevelTemplateLoader>();
        loader.Load().Returns([new ExperienceLevelTemplate { Level = 1, SkillPoints = points }]);
        var experience = new ExperienceManager();
        experience.Load(loader.Object, 55, 50);
        SetInstance(experience);
        _character.Level = 1;
        _character.Skills.Skills.Add(11918, new Skill(new SkillTemplate
        {
            Id = 11918, AbilityId = AbilityType.Fight, SkillPoints = invested
        }));
        SetField(_skills, "_passiveBuffs", new Dictionary<uint, PassiveBuffTemplate>
        {
            [1] = new() { Id = 1, AbilityId = AbilityType.Fight, BuffId = 3200, ReqPoints = 2, Level = level }
        });
        SetField(_skills, "_buffs", new Dictionary<uint, BuffTemplate> { [3200] = new() { Id = 3200 } });
        var buffs = Mock.Of<IBuffs>();
        _character.Buffs = buffs.Object;

        _character.Skills.AddBuff(1);
        _character.Skills.AddBuff(1);

        await Assert.That(_character.Skills.PassiveBuffs.ContainsKey(1)).IsEqualTo(expected);
        buffs.AddBuff(Is<Buff>(buff => buff.Template.BuffId == 3200), 0, 0)
            .WasCalled(expected ? Times.Once : Times.Never);
    }

    [Test]
    public async Task Load_ReviewedCompactPreservesRealBuffAndInteractionGrants()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_COMBAT_TEST_COMPACT");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_COMBAT_TEST_COMPACT to the read-only r208022 compact.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        _grants.Load(connection);
        var effects = GetEffects(_character.Buffs);
        effects.Add(new Buff(_character, _character, new SkillCasterUnit(70),
            new BuffTemplate { Id = 1463 }, null, DateTime.UtcNow) { InUse = true });
        await Assert.That(_grants.HasBuffGrant(17663, _character.Buffs)).IsTrue();
        await Assert.That(_grants.HasBuffGrant(16927, _character.Buffs)).IsFalse();
    }

    [Test]
    public async Task Reload_ReplacesOldGrants()
    {
        using var data = CreateData();
        using var command = data.CreateCommand();
        command.CommandText = "DELETE FROM buff_skills; DELETE FROM doodad_funcs; DELETE FROM npc_interactions;";
        command.ExecuteNonQuery();
        _grants.Load(data);
        await Assert.That(_grants.HasDoodadGrant(100, 11111)).IsFalse();
        await Assert.That(_grants.HasNpcGrant(5, 21521)).IsFalse();
    }

    private bool CanUse(SkillTemplate skill, SkillCastTarget target = null)
    {
        return SkillCastAuthorization.CanUseCharacterSkill(_character, skill, new SkillCasterUnit(70),
            target ?? new SkillCastUnitTarget(70));
    }

    private void AddToWorld(GameObject unit)
    {
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(unit, _world);
        _world.AddObject(unit);
    }

    private static List<Buff> GetEffects(IBuffs buffs)
    {
        return (List<Buff>)typeof(Buffs).GetField("_effects", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(buffs);
    }

    private static SqliteConnection CreateData()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE buff_skills(buff_id INTEGER, skill_id INTEGER);
            CREATE TABLE npc_interactions(npc_interaction_set_id INTEGER, skill_id INTEGER);
            CREATE TABLE doodad_funcs(doodad_func_group_id INTEGER, func_skill_id INTEGER, actual_func_type TEXT, actual_func_id INTEGER);
            CREATE TABLE doodad_func_fake_uses(id INTEGER, fake_skill_id INTEGER);
            CREATE TABLE doodad_func_uses(id INTEGER, skill_id INTEGER);
            INSERT INTO buff_skills VALUES(1463,17663),(2098,17663),(1463,17663);
            INSERT INTO npc_interactions VALUES(5,21521);
            INSERT INTO doodad_funcs VALUES(100,11111,'DoodadFuncUse',1),(100,0,'DoodadFuncFakeUse',2),(200,NULL,'DoodadFuncTimer',1);
            INSERT INTO doodad_func_fake_uses VALUES(2,13822);
            INSERT INTO doodad_func_uses VALUES(1,11301);
            """;
        command.ExecuteNonQuery();
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
        public override int GetAbLevel(AbilityType type) => 10;
    }
}
