using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

[NotInParallel]
public sealed class SkillFeedbackTests
{
    private readonly Dictionary<FieldInfo, object> _previous = [];
    private WorldConfig _previousWorld;

    [Before(Test)]
    public void SetUp()
    {
        var experience = new ExperienceManager();
        experience.Load(new ExperienceLoader(), 5, 5);
        Install(experience);
        Install(new QuestManager(null, null));
        Install(new AchievementGameData());
        Install(new UnitRequirementsGameData());
        Install(new ExperienceModifierGameData());
        Install(new UnitAttributeLimitsGameData());
        _previousWorld = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { ExpRate = 1 };
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorld;
        foreach (var (field, previous) in _previous)
            field.SetValue(null, previous);
    }

    [Test]
    public async Task ExperienceReward_UpdatesLearnedRankAfterOneCombinedExperiencePacket()
    {
        var character = CreateCharacter();
        var learned = Learn(character, 101, AbilityType.Fight, 2);
        character.AddExp(200, true);

        await Assert.That(character.Abilities.Abilities[AbilityType.Fight].Exp).IsEqualTo(200);
        await Assert.That(character.Abilities.Abilities[AbilityType.Magic].Exp).IsEqualTo(200);
        await Assert.That(character.Abilities.Abilities[AbilityType.Love].Exp).IsEqualTo(200);
        await Assert.That(character.Abilities.Abilities[AbilityType.Wild].Exp).IsEqualTo(0);
        await Assert.That(learned.Level).IsEqualTo((byte)2);
        await Assert.That(new Skill(learned.Template, character).Level).IsEqualTo(learned.Level);
        await Assert.That(character.Session.Packets.Count).IsEqualTo(2);
        var experience = ReadBody(character.Session.Packets[0], SCOffsets.SCExpChangedPacket);
        await Assert.That(experience.ReadBc()).IsEqualTo(character.ObjId);
        await Assert.That(experience.ReadInt32()).IsEqualTo(200);
        await Assert.That(experience.ReadBoolean()).IsTrue();
        await Assert.That(experience.Pos).IsEqualTo(experience.Count);
        await CheckUpgrade(character.Session.Packets[1], learned.Id, 2);
    }

    [Test]
    public async Task RankRefresh_OnlyChangesLearnedActiveRanksInIdentifierOrder()
    {
        var character = CreateCharacter();
        var later = Learn(character, 200, AbilityType.Magic, 1);
        var earlier = Learn(character, 100, AbilityType.Fight, 2);
        var fixedRank = Learn(character, 300, AbilityType.Love, 0);
        var inactive = Learn(character, 400, AbilityType.Wild, 1);
        var general = Learn(character, 500, AbilityType.General, 1);
        foreach (var ability in character.Abilities.Values)
            ability.Exp = 400;

        character.Skills.RefreshLearnedSkillRanks();
        character.Skills.RefreshLearnedSkillRanks();
        await Assert.That(character.Session.Packets.Count).IsEqualTo(2);
        await CheckUpgrade(character.Session.Packets[0], earlier.Id, 3);
        await CheckUpgrade(character.Session.Packets[1], later.Id, 5);
        await Assert.That(fixedRank.Level).IsEqualTo((byte)1);
        await Assert.That(inactive.Level).IsEqualTo((byte)1);
        await Assert.That(general.Level).IsEqualTo((byte)1);
        await Assert.That(character.Skills.Skills.Count).IsEqualTo(5);
    }

    [Test]
    public async Task ExperienceWithoutAbilityGain_DoesNotPublishAnAbilityRankChange()
    {
        var character = CreateCharacter();
        var skill = Learn(character, 101, AbilityType.Fight, 1);
        character.AddExp(200, false);
        await Assert.That(character.Abilities.Abilities[AbilityType.Fight].Exp).IsEqualTo(0);
        await Assert.That(skill.Level).IsEqualTo((byte)1);
        await Assert.That(character.Session.Packets.Count).IsEqualTo(1);
        var packet = ReadBody(character.Session.Packets.Single(), SCOffsets.SCExpChangedPacket);
        await Assert.That(packet.ReadBc()).IsEqualTo(character.ObjId);
        await Assert.That(packet.ReadInt32()).IsEqualTo(200);
        await Assert.That(packet.ReadBoolean()).IsFalse();
        await Assert.That(packet.Pos).IsEqualTo(packet.Count);
    }

    [Test]
    public async Task PreparedReward_PublishesNoRankUntilItsCommitCallback()
    {
        var character = CreateCharacter();
        var skill = Learn(character, 101, AbilityType.Fight, 1);
        var committed = character.PrepareExperienceReward(200, true);
        await Assert.That(skill.Level).IsEqualTo((byte)1);
        await Assert.That(character.Session.Packets.Count).IsEqualTo(0);
        committed();
        await Assert.That(skill.Level).IsEqualTo((byte)3);
        await Assert.That(character.Session.Packets.Count).IsEqualTo(2);
        await CheckUpgrade(character.Session.Packets[1], skill.Id, 3);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(255)]
    public async Task SkillUpgradePacket_UsesOneByteRankWithNoTrailingData(int rank)
    {
        var packet = new SCSkillUpgradedPacket(new Skill { Id = 0x12345678, Level = (byte)rank });
        await Assert.That(packet.TypeId).IsEqualTo((ushort)0x106);
        await Assert.That(packet.Level).IsEqualTo((byte)1);
        var body = new PacketStream();
        packet.Write(body);
        await Assert.That(body.Count).IsEqualTo(5);
        await Assert.That(body.ReadUInt32()).IsEqualTo(0x12345678u);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)rank);
        await Assert.That(body.Pos).IsEqualTo(body.Count);
    }

    [Test]
    public async Task ExperienceLoss_DoesNotDowngradeOrRepeatAnUpgrade()
    {
        var character = CreateCharacter();
        var learned = Learn(character, 101, AbilityType.Fight, 1);
        character.AddExp(250, true);
        character.Session.Packets.Clear();
        character.AddExp(-10, false);
        await Assert.That(learned.Level).IsEqualTo((byte)3);
        await Assert.That(character.Session.Packets.Count).IsEqualTo(1);
        var packet = ReadBody(character.Session.Packets.Single(), SCOffsets.SCExpChangedPacket);
        await Assert.That(packet.ReadBc()).IsEqualTo(character.ObjId);
        await Assert.That(packet.ReadInt32()).IsEqualTo(-10);
        await Assert.That(packet.ReadBoolean()).IsFalse();
        await Assert.That(packet.Pos).IsEqualTo(packet.Count);
    }

    private static RecordingCharacter CreateCharacter()
    {
        var character = new RecordingCharacter
        {
            ObjId = 0x123456, Level = 1,
            Ability1 = AbilityType.Fight, Ability2 = AbilityType.Magic, Ability3 = AbilityType.Love
        };
        character.Abilities = new CharacterAbilities(character);
        character.Skills = new CharacterSkills(character);
        character.Connection = new GameConnection(character.Session) { ActiveChar = character };
        return character;
    }

    private static Skill Learn(RecordingCharacter character, uint id, AbilityType ability, int step)
    {
        var skill = new Skill(new SkillTemplate { Id = id, AbilityId = ability, AbilityLevel = 1, LevelStep = step });
        character.Skills.Skills.Add(id, skill);
        return skill;
    }

    private static PacketStream ReadBody(byte[] packet, ushort opcode)
    {
        if (BitConverter.ToUInt16(packet, 6) != opcode)
            throw new InvalidOperationException($"Unexpected opcode {BitConverter.ToUInt16(packet, 6):X}");
        return new PacketStream(packet[8..]);
    }

    private static async Task CheckUpgrade(byte[] packet, uint id, byte rank)
    {
        var body = ReadBody(packet, SCOffsets.SCSkillUpgradedPacket);
        await Assert.That(body.ReadUInt32()).IsEqualTo(id);
        await Assert.That(body.ReadByte()).IsEqualTo(rank);
        await Assert.That(body.Pos).IsEqualTo(body.Count);
    }

    private void Install<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        _previous.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    private sealed class RecordingCharacter : CharacterMock
    {
        internal RecordingSession Session { get; } = new();
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    private sealed class RecordingSession : ISession
    {
        internal List<byte[]> Packets { get; } = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => Packets.Add(packet.ToArray());
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }

    private sealed class ExperienceLoader : IExperienceLevelTemplateLoader
    {
        public IEnumerable<ExperienceLevelTemplate> Load() => Enumerable.Range(1, 5)
            .Select(level => new ExperienceLevelTemplate
            {
                Level = (byte)level, TotalExp = (level - 1) * 100, TotalMateExp = (level - 1) * 100,
                SkillPoints = level
            });
    }
}
