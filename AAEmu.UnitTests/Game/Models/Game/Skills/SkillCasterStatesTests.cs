using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class SkillCasterStatesTests
{
    private static readonly FieldInfo SkillManagerInstance = typeof(Singleton<SkillManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic);
    private object _previousSkillManager;

    [Before(Test)]
    public void SetUp()
    {
        _previousSkillManager = SkillManagerInstance.GetValue(null);
        var manager = new SkillManager(null, null);
        typeof(SkillManager).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(manager, new Dictionary<string, Dictionary<uint, EffectTemplate>>
            {
                ["SkillController"] = new()
                {
                    [5683] = new SkillControllerTemplate { Id = 5683, KindId = 3 },
                    [100] = new SkillControllerTemplate { Id = 100, KindId = 2 }
                }
            });
        SkillManagerInstance.SetValue(null, manager);
    }

    [After(Test)]
    public void TearDown()
    {
        SkillManagerInstance.SetValue(null, _previousSkillManager);
    }

    [Test]
    [Arguments(0, true, false, SkillResult.SourceDied)]
    [Arguments(-1, true, false, SkillResult.SourceDied)]
    [Arguments(1, true, false, SkillResult.Success)]
    [Arguments(0, false, true, SkillResult.Success)]
    [Arguments(1, false, true, SkillResult.SourceAlive)]
    [Arguments(0, true, true, SkillResult.Success)]
    [Arguments(1, true, true, SkillResult.Success)]
    public async Task Check_AuthoredLifeStatesPreserveDeadUseSkills(int hp, bool alive, bool dead, SkillResult expected)
    {
        var caster = new Unit { Hp = hp };
        var template = new SkillTemplate { SourceAlive = alive, SourceDead = dead };

        var result = SkillCasterStates.Check(caster, template, out var detail);

        await Assert.That(result).IsEqualTo(expected);
        await Assert.That(detail).IsEqualTo(0U);
    }

    [Test]
    [Arguments(false, SkillResult.CannotCastInStun)]
    [Arguments(true, SkillResult.Success)]
    public async Task Check_CombatKnockdownUsesAuthoredStunException(bool sourceStun, SkillResult expected)
    {
        var caster = new Unit { Hp = 100 };
        // Buff 1318 uses both flags. Skill 11429 permits Stun in r208022.
        AddBuff(caster, new BuffTemplate { Id = 1318, Stun = true, Knockdown = true });
        var template = new SkillTemplate { Id = 11429, SourceStun = sourceStun, DamageTypeId = 2 };

        await Assert.That(SkillCasterStates.Check(caster, template, out _)).IsEqualTo(expected);
    }

    [Test]
    public async Task Check_CosmeticKnockdownDoesNotInventAStun()
    {
        var caster = new Unit { Hp = 100 };
        // Buff 3784 prevents fall damage and has no Stun flag.
        AddBuff(caster, new BuffTemplate { Id = 3784, Knockdown = true });

        await Assert.That(SkillCasterStates.Check(caster, new SkillTemplate(), out _)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    public async Task Check_StunExceptionDoesNotBypassSleep()
    {
        var caster = new Unit { Hp = 100 };
        AddBuff(caster, new BuffTemplate { Sleep = true });

        await Assert.That(SkillCasterStates.Check(caster, new SkillTemplate { SourceStun = true }, out _))
            .IsEqualTo(SkillResult.CannotCastInStun);
    }

    [Test]
    public async Task Check_BlankMindedReturnsBlockingBuffId()
    {
        var caster = new Unit { Hp = 100 };
        AddBuff(caster, new BuffTemplate { Id = 1923, BlankMinded = true });

        var result = SkillCasterStates.Check(caster, new SkillTemplate { SourceStun = true }, out var detail);

        await Assert.That(result).IsEqualTo(SkillResult.BlankMinded);
        await Assert.That(detail).IsEqualTo(1923U);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Check_FearUsesBuffControllerForClientDrivenCharacters(bool endSkillController)
    {
        var caster = new Unit { Hp = 100 };
        // Fear 1178 has no Stun, Sleep, or BlankMinded flag.
        AddBuff(caster, new BuffTemplate { Id = 1178, SkillControllerId = 5683 });
        var template = new SkillTemplate { SourceStun = true, EndSkillController = endSkillController };

        var result = SkillCasterStates.Check(caster, template, out var detail);

        await Assert.That(caster.ActiveSkillController).IsNull();
        await Assert.That(result).IsEqualTo(SkillResult.BlankMinded);
        await Assert.That(detail).IsEqualTo(1178U);
    }

    [Test]
    public async Task Check_LeapControllerDoesNotInventFear()
    {
        var caster = new Unit { Hp = 100 };
        AddBuff(caster, new BuffTemplate { SkillControllerId = 100 });

        await Assert.That(SkillCasterStates.Check(caster, new SkillTemplate(), out _)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    [Arguments(0U, SkillResult.Success)]
    [Arguments(1U, SkillResult.Success)]
    [Arguments(2U, SkillResult.Silence)]
    [Arguments(3U, SkillResult.Success)]
    [Arguments(4U, SkillResult.Success)]
    [Arguments(5U, SkillResult.Success)]
    public async Task Check_SilenceUsesMagicDamageType(uint damageType, SkillResult expected)
    {
        var caster = new Unit { Hp = 100 };
        AddBuff(caster, new BuffTemplate { Silence = true });

        await Assert.That(SkillCasterStates.Check(caster,
            new SkillTemplate { DamageTypeId = damageType, SourceStun = true }, out _)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(1U, SkillResult.Crippled)]
    [Arguments(2U, SkillResult.Success)]
    [Arguments(3U, SkillResult.Success)]
    [Arguments(4U, SkillResult.Crippled)]
    [Arguments(5U, SkillResult.Success)]
    public async Task Check_CrippleUsesMeleeAndRangedDamageTypes(uint damageType, SkillResult expected)
    {
        var caster = new Unit { Hp = 100 };
        AddBuff(caster, new BuffTemplate { Cripled = true });

        await Assert.That(SkillCasterStates.Check(caster,
            new SkillTemplate { DamageTypeId = damageType }, out _)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(SkillTargetType.Hostile, SkillTargetRelation.Any, SkillResult.NoPerm)]
    [Arguments(SkillTargetType.Pos, SkillTargetRelation.Hostile, SkillResult.NoPerm)]
    [Arguments(SkillTargetType.Self, SkillTargetRelation.Any, SkillResult.Success)]
    [Arguments(SkillTargetType.Friendly, SkillTargetRelation.Friendly, SkillResult.Success)]
    public async Task Check_PacifistUsesAuthoredHostileTargets(SkillTargetType target, SkillTargetRelation relation,
        SkillResult expected)
    {
        var caster = new Unit { Hp = 100 };
        AddBuff(caster, new BuffTemplate { Pacifist = true });

        await Assert.That(SkillCasterStates.Check(caster,
            new SkillTemplate { TargetType = target, TargetRelation = relation }, out _)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(SkillEffectApplicationMethod.Target, false, SkillResult.NoPerm)]
    [Arguments(SkillEffectApplicationMethod.SourceToPos, false, SkillResult.NoPerm)]
    [Arguments(SkillEffectApplicationMethod.Source, false, SkillResult.Success)]
    [Arguments(SkillEffectApplicationMethod.SourceOnce, false, SkillResult.Success)]
    [Arguments(SkillEffectApplicationMethod.Target, true, SkillResult.Success)]
    public async Task Check_PacifistFindsHostileAreaEffects(SkillEffectApplicationMethod method, bool friendly,
        SkillResult expected)
    {
        var caster = new Unit { Hp = 100 };
        AddBuff(caster, new BuffTemplate { Pacifist = true });
        var template = new SkillTemplate
        {
            TargetType = SkillTargetType.Pos,
            Effects = [new SkillEffect { NonFriendly = true, Friendly = friendly, ApplicationMethod = method }]
        };

        await Assert.That(SkillCasterStates.Check(caster, template, out _)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(EffectState.Finishing, true, false)]
    [Arguments(EffectState.Finished, true, false)]
    [Arguments(EffectState.Acting, false, false)]
    [Arguments(EffectState.Acting, true, true)]
    public async Task Check_InactiveOrExpiredBuffsDoNotBlock(EffectState state, bool inUse, bool expired)
    {
        var caster = new Unit { Hp = 100 };
        var buff = AddBuff(caster, new BuffTemplate { Stun = true });
        buff.State = state;
        buff.InUse = inUse;
        if (expired)
        {
            buff.Duration = 1000;
            buff.StartTime = DateTime.UtcNow.AddMinutes(-1);
        }

        await Assert.That(SkillCasterStates.Check(caster, new SkillTemplate(), out _)).IsEqualTo(SkillResult.Success);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Use_RejectsBeforeManaCooldownBuffRemovalOrWorldAccess(bool bypassGcd)
    {
        var cooldown = DateTime.UtcNow.AddSeconds(-10);
        var caster = new Unit { Hp = 100, Mp = 77, GlobalCooldown = cooldown, ConditionChance = false };
        var buff = AddBuff(caster, new BuffTemplate { Id = 1178, SkillControllerId = 5683 });
        var skill = new Skill(new SkillTemplate { Id = 100, ManaCost = 10, CancelOngoingBuffs = true }, null);

        var result = skill.Use(caster, new SkillCasterUnit(), null, null, bypassGcd, out var detail);

        await Assert.That(result).IsEqualTo(SkillResult.BlankMinded);
        await Assert.That(detail).IsEqualTo(1178U);
        await Assert.That(skill.Cancelled).IsTrue();
        await Assert.That(skill.TlId).IsEqualTo((ushort)0);
        await Assert.That(skill.OriginalCaster).IsNull();
        await Assert.That(caster.Mp).IsEqualTo(77);
        await Assert.That(caster.GlobalCooldown).IsEqualTo(cooldown);
        await Assert.That(caster.ConditionChance).IsFalse();
        await Assert.That(buff.InUse).IsTrue();
        await Assert.That(buff.State).IsEqualTo(EffectState.Acting);
    }

    private static Buff AddBuff(Unit caster, BuffTemplate template)
    {
        var buff = new Buff(caster, caster, new SkillCasterUnit(), template, null, DateTime.UtcNow)
        {
            InUse = true,
            State = EffectState.Acting
        };
        // Do not start timers or movement. These tests exercise admission against the live buff collection.
        var effects = (List<Buff>)typeof(AAEmu.Game.Models.Game.Units.Buffs)
            .GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(caster.Buffs);
        effects.Add(buff);
        return buff;
    }
}
