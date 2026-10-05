using System.Net;
using System.Net.Sockets;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ExperienceRank_CommitsWithPaidSkill_AndFailureSendsNoUpgrade(bool succeeds)
    {
        var experience = new ExperienceManager();
        var loader = Mock.Of<IExperienceLevelTemplateLoader>();
        loader.Load().Returns(new[]
        {
            new ExperienceLevelTemplate { Level = 1, TotalExp = 0, SkillPoints = 1 },
            new ExperienceLevelTemplate { Level = 2, TotalExp = 100, SkillPoints = 2 },
            new ExperienceLevelTemplate { Level = 3, TotalExp = 200, SkillPoints = 3 },
            new ExperienceLevelTemplate { Level = 4, TotalExp = 300, SkillPoints = 4 }
        });
        experience.Load(loader.Object, 4, 4);
        SetInstance(experience);
        var previousWorld = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { ExpRate = 1 };
        try
        {
            _owner.Level = 1;
            _owner.Ability1 = AbilityType.Fight;
            _owner.Ability2 = AbilityType.None;
            _owner.Ability3 = AbilityType.None;
            _owner.Abilities = new CharacterAbilities(_owner);
            _owner.Skills = new CharacterSkills(_owner);
            var learned = new Skill(new SkillTemplate
            {
                Id = 101, AbilityId = AbilityType.Fight, AbilityLevel = 1, LevelStep = 1
            });
            _owner.Skills.Skills.Add(learned.Id, learned);
            var session = new RankFeedbackSession();
            _owner.Connection = new GameConnection(session) { ActiveChar = _owner };
            var paid = NewSkill();
            var rankAtCommit = (byte)0;
            paid.CommitLaborBatch = (_, _) => { rankAtCommit = learned.Level; return succeeds; };

            var result = SkillLaborBatch.Run(_owner, paid, true, () => _owner.AddExp(200, true));

            await Assert.That(result).IsEqualTo(succeeds);
            await Assert.That(rankAtCommit).IsEqualTo((byte)1);
            await Assert.That(learned.Level).IsEqualTo((byte)(succeeds ? 3 : 1));
            await Assert.That(_owner.Experience).IsEqualTo(succeeds ? 200 : 0);
            await Assert.That(_owner.Abilities.Abilities[AbilityType.Fight].Exp).IsEqualTo(succeeds ? 200 : 0);
            await Assert.That(session.Packets.Count(packet => BitConverter.ToUInt16(packet, 6) == SCOffsets.SCSkillUpgradedPacket))
                .IsEqualTo(succeeds ? 1 : 0);
            await Assert.That(session.Packets.Count(packet => BitConverter.ToUInt16(packet, 6) == SCOffsets.SCExpChangedPacket))
                .IsEqualTo(succeeds ? 1 : 0);
        }
        finally
        {
            AppConfiguration.Instance.World = previousWorld;
        }
    }

    private sealed class RankFeedbackSession : ISession
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
}
