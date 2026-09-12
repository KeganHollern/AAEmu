using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

public sealed partial class SkillLaborTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExperienceRecovery_FreeRecoveryRejectsUnitCastAndNonConsumableItem(bool useItem)
    {
        _owner.Level = 1;
        _owner.RecoverableExp = 400;
        _owner.LastExpLoss = 500;
        _owner.InitializeLaborCache(0, DateTime.UtcNow);
        _material.Count = 1;
        _material.Template.UseSkillAsReagent = false;
        var skill = NewSkill();
        skill.Template.ConsumeLaborPower = 0;
        skill.Template.Effects.Add(new SkillEffect
        {
            EffectId = 39213, Template = new RecoverExpEffect { NeedLaborPower = false },
            ApplicationMethod = SkillEffectApplicationMethod.SourceOnce,
            Friendly = true, NonFriendly = true, Front = true, Back = true,
            StartLevel = 1, EndLevel = 99, Chance = 100, ConsumeItemCount = 1
        });
        var saves = 0;
        skill.CommitLaborBatch = (_, _) => { saves++; return true; };
        SkillCaster source = useItem
            ? new SkillItem(_owner.ObjId, _material.Id, _material.TemplateId)
            : new SkillCasterUnit(_owner.ObjId);

        skill.ApplyEffects(_owner, source, _owner, new SkillCastUnitTarget(_owner.ObjId), null);

        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(saves).IsEqualTo(0);
        await Assert.That(_owner.Experience).IsEqualTo(0);
        await Assert.That(_owner.RecoverableExp).IsEqualTo(400);
        await Assert.That(_owner.LastExpLoss).IsEqualTo(500);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)0);
        await Assert.That(_material.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ExperienceRecovery_ZeroLaborSkillSettlesItsScrollAndExperienceTogether(bool commitSucceeds)
    {
        var experience = new ExperienceManager();
        experience.Load(new RecoveryExperienceLoader(), 2, 2);
        SetInstance(experience);
        _owner.Level = 1;
        _owner.Abilities = new CharacterAbilities(_owner);
        _owner.RecoverableExp = 400;
        _owner.LastExpLoss = 500;
        _owner.InitializeLaborCache(0, DateTime.UtcNow);
        _material.Count = 1;
        _material.Template.UseSkillAsReagent = true;
        var skill = NewSkill();
        skill.Template.ConsumeLaborPower = 0;
        skill.Template.Effects.Add(new SkillEffect
        {
            EffectId = 39213, Template = new RecoverExpEffect { NeedLaborPower = false },
            ApplicationMethod = SkillEffectApplicationMethod.SourceOnce,
            Friendly = true, NonFriendly = true, Front = true, Back = true,
            StartLevel = 1, EndLevel = 99, Chance = 100, ConsumeItemCount = 1
        });
        var saves = 0;
        var preparedTogether = false;
        skill.CommitLaborBatch = (_, _) =>
        {
            saves++;
            preparedTogether = _owner.Experience == 400 && _owner.RecoverableExp == 0 &&
                _owner.LaborPower == 0 && _material.Count == 0;
            return commitSucceeds;
        };

        skill.ApplyEffects(_owner, new SkillItem(_owner.ObjId, _material.Id, _material.TemplateId),
            _owner, new SkillCastUnitTarget(_owner.ObjId), null);

        await Assert.That(saves).IsEqualTo(1);
        await Assert.That(preparedTogether).IsTrue();
        await Assert.That(_owner.Experience).IsEqualTo(commitSucceeds ? 400 : 0);
        await Assert.That(_owner.RecoverableExp).IsEqualTo(commitSucceeds ? 0 : 400);
        await Assert.That(_owner.LastExpLoss).IsEqualTo(commitSucceeds ? 0 : 500);
        await Assert.That(_owner.LaborPower).IsEqualTo((short)0);
        await Assert.That(_material.Count).IsEqualTo(commitSucceeds ? 0 : 1);

        skill.ApplyEffects(_owner, new SkillItem(_owner.ObjId, _material.Id, _material.TemplateId),
            _owner, new SkillCastUnitTarget(_owner.ObjId), null);
        await Assert.That(saves).IsEqualTo(1);
        await Assert.That(_owner.Experience).IsEqualTo(commitSucceeds ? 400 : 0);
    }

    private sealed class RecoveryExperienceLoader : IExperienceLevelTemplateLoader
    {
        public IEnumerable<ExperienceLevelTemplate> Load() =>
        [
            new ExperienceLevelTemplate { Level = 1, TotalExp = 0 },
            new ExperienceLevelTemplate { Level = 2, TotalExp = 1000000 }
        ];
    }
}
