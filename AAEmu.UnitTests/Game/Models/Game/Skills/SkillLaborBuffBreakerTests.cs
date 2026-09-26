using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task BuffBreaker_ChangesLiveBuffsOnlyAfterSuccessfulLaborCommit(bool succeeds)
    {
        BuffBreakerTestData.Configure(SkillManager.Instance);
        SetInstance(BuffBreakerTestData.Load());
        SetInstance(new MateGameData());
        var glider = new Buff(_owner, _owner, new SkillCasterUnit(_owner.ObjId),
            SkillManager.Instance.GetBuffTemplate(2098), null, DateTime.UtcNow);
        _owner.Buffs.AddBuff(glider);
        var exits = 0;
        glider.Events.OnDispelled += (_, _) => exits++;
        var skill = NewSkill();
        var liveGliderAtCommit = false;
        var liveFearAtCommit = false;
        skill.CommitLaborBatch = (_, _) =>
        {
            liveGliderAtCommit = _owner.Buffs.CheckBuff(2098);
            liveFearAtCommit = _owner.Buffs.CheckBuff(156);
            return succeeds;
        };

        var result = SkillLaborBatch.Run(_owner, skill, true, () =>
            _owner.Buffs.AddBuff(new Buff(_owner, _owner, new SkillCasterUnit(_owner.ObjId),
                SkillManager.Instance.GetBuffTemplate(156), skill, DateTime.UtcNow)));

        await Assert.That(result).IsEqualTo(succeeds);
        await Assert.That(liveGliderAtCommit).IsTrue();
        await Assert.That(liveFearAtCommit).IsFalse();
        await Assert.That(_owner.Buffs.CheckBuff(2098)).IsEqualTo(!succeeds);
        await Assert.That(_owner.Buffs.CheckBuff(156)).IsEqualTo(succeeds);
        await Assert.That(exits).IsEqualTo(succeeds ? 1 : 0);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)(succeeds ? 10 : 20));
    }
}
