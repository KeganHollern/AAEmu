using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Duels;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects.SpecialEffects;

public sealed partial class BuffSpecialEffectsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AbsorbEffect_AuthoredTwoEffectSkillRequiresConsumptionBeforeInspired(bool hasBuff)
    {
        _thief.MaxMp = 10000;
        _thief.Mp = 100;
        if (hasBuff)
            _thief.Buffs.AddBuff(new Buff(_thief, _thief, new SkillCasterUnit(_thief.ObjId), _templates[11], null, DateTime.UtcNow));
        var skill = AbsorbSkill();

        skill.ApplyEffects(_thief, new SkillCasterUnit(_thief.ObjId), _thief,
            new SkillCastUnitTarget(_thief.ObjId), null);

        await Assert.That(_thief.Buffs.CheckBuff(127)).IsEqualTo(hasBuff);
        await Assert.That(skill.Cancelled).IsEqualTo(!hasBuff);
        await Assert.That(_thief.Buffs.CheckBuff(11)).IsFalse();
        if (hasBuff)
        {
            await Assert.That(_thief.Mp >= 1100 && _thief.Mp <= 1600).IsTrue();
            await Assert.That(_thief.Buffs.GetBuffCountById(127)).IsEqualTo(1);
        }
        else
            await Assert.That(_thief.Mp).IsEqualTo(100);
    }

    [Test]
    public async Task RedeemBuff_UsesAuthoredCapAndNormalManaFeedback()
    {
        _thief.MaxMp = 100000;
        _thief.Mp = 100;
        _thief.Buffs.AddBuff(new Buff(_thief, _thief, new SkillCasterUnit(_thief.ObjId), _templates[11], null, DateTime.UtcNow));
        var skill = AbsorbSkill();
        new RedeemBuff().Execute(_thief, new SkillCasterUnit(_thief.ObjId), _thief,
            new SkillCastUnitTarget(_thief.ObjId), new CastSkill(11988, 1), skill, null, DateTime.UtcNow, 4000, 10, 15, 0);

        await Assert.That(_thief.Mp).IsEqualTo(4100);
        await Assert.That(_thief.Buffs.CheckBuff(11)).IsFalse();
        var body = _thief.Packets.OfType<SCUnitHealedPacket>().Single().Write(new PacketStream());
        await Assert.That(body.ReadByte()).IsEqualTo((byte)CastType.Skill);
        await Assert.That(body.ReadUInt32()).IsEqualTo(11988u);
        await Assert.That(body.ReadUInt16()).IsEqualTo((ushort)1);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)SkillCasterType.Unit);
        await Assert.That(body.ReadBc()).IsEqualTo(_thief.ObjId);
        await Assert.That(body.ReadBc()).IsEqualTo(_thief.ObjId);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)HealType.Mana);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)HealHitType.HealHit);
        await Assert.That(body.ReadInt32()).IsEqualTo(4000);
        await Assert.That(body.ReadInt32()).IsEqualTo(0);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)1);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
        await Assert.That(_thief.Packets.OfType<SCUnitPointsPacket>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task RedeemBuff_NearFullManaDoesNotExceedMaximum()
    {
        _thief.MaxMp = 1000;
        _thief.Mp = 990;
        _thief.Buffs.AddBuff(new Buff(_thief, _thief, new SkillCasterUnit(_thief.ObjId), _templates[11], null, DateTime.UtcNow));
        new RedeemBuff().Execute(_thief, new SkillCasterUnit(_thief.ObjId), _thief,
            new SkillCastUnitTarget(_thief.ObjId), new CastSkill(11988, 1), null, null, DateTime.UtcNow, 4000, 10, 15, 0);

        await Assert.That(_thief.Mp).IsEqualTo(1000);
    }

    [Test]
    [Arguments(1000, 20, 1000, 5300, 100)]
    [Arguments(1000, 20, 20000, 0, 1000)]
    [Arguments(2000, 50, 1000, 0, 500)]
    [Arguments(2000, 50, 20000, 0, 2000)]
    public async Task ExplodeBuff_UsesAuthoredCapPercentAndNormalMagicMitigation(int cap, int percent,
        int maxHp, int resistance, int expected)
    {
        _victim.Hp = _victim.MaxHp = maxHp;
        _victim.MagicResistance = resistance;
        Add(11, DateTime.UtcNow);
        Add(69, DateTime.UtcNow);
        var sourceBuff = new Buff(_victim, _thief, new SkillCasterUnit(_thief.ObjId), _templates[449], null, DateTime.UtcNow)
            { Index = 77 };
        var effect = new SpecialEffect { SpecialEffectTypeId = SpecialType.ExplodeBuff, Value1 = cap, Value2 = percent, Value3 = percent };

        effect.Apply(_thief, new SkillCasterUnit(_thief.ObjId), _victim, new SkillCastUnitTarget(_victim.ObjId),
            new CastBuff(sourceBuff), new EffectSource(), null, DateTime.UtcNow);

        await Assert.That(_victim.Hp).IsEqualTo(maxHp - expected);
        await Assert.That(_victim.Buffs.CheckBuff(11)).IsFalse();
        await Assert.That(_victim.Buffs.CheckBuff(69)).IsTrue();
        var body = _victim.Packets.OfType<SCUnitDamagedPacket>().Single().Write(new PacketStream());
        await Assert.That(body.ReadByte()).IsEqualTo((byte)CastType.Buff);
        await Assert.That(body.ReadUInt32()).IsEqualTo(449u);
        await Assert.That(body.ReadBc()).IsEqualTo(_victim.ObjId);
        await Assert.That(body.ReadUInt32()).IsEqualTo(77u);
        await Assert.That(body.ReadBoolean()).IsTrue();
        await Assert.That(body.ReadBoolean()).IsFalse();
        await Assert.That(body.ReadByte()).IsEqualTo((byte)SkillCasterType.Unit);
        await Assert.That(body.ReadBc()).IsEqualTo(_thief.ObjId);
        await Assert.That(body.ReadBc()).IsEqualTo(_thief.ObjId);
        await Assert.That(body.ReadBc()).IsEqualTo(_victim.ObjId);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)0);
        var damage = body.ReadPiscW(3);
        await Assert.That(damage[0]).IsEqualTo((long)expected);
        await Assert.That(body.ReadPiscW(3).SequenceEqual([0L, 0L, 0L])).IsTrue();
        await Assert.That(body.ReadByte()).IsEqualTo((byte)0);
        await Assert.That(body.ReadUInt16()).IsEqualTo((ushort)(288 | (ushort)SkillHitType.SpellHit));
        await Assert.That(body.ReadByte()).IsEqualTo((byte)1);
        await Assert.That(body.ReadByte()).IsEqualTo((byte)1);
        await Assert.That(body.LeftBytes).IsEqualTo(0);
    }

    [Test]
    public async Task ExplodeBuff_NoEligibleBuffCausesNoDamageOrDamagePacket()
    {
        _victim.Hp = _victim.MaxHp = 1000;
        Add(9003, DateTime.UtcNow);
        Add(9002, DateTime.UtcNow);

        new ExplodeBuff().Execute(_thief, new SkillCasterUnit(_thief.ObjId), _victim,
            new SkillCastUnitTarget(_victim.ObjId), new CastSkill(16410, 1), null, null, DateTime.UtcNow, 1000, 20, 20, 0);

        await Assert.That(_victim.Hp).IsEqualTo(1000);
        await Assert.That(_victim.Packets.OfType<SCUnitDamagedPacket>().Count()).IsEqualTo(0);
    }

    [Test]
    public async Task ExplodeBuff_FinishedDuelContextFromCastBuffRejectsConsumptionAndDamage()
    {
        var first = new CharacterMock { ObjId = 301, Id = 31, Hp = 100 };
        var second = new CharacterMock { ObjId = 302, Id = 32, Hp = 100 };
        var duel = new Duel(first, second);
        var old = new Buff(second, first, new SkillCasterUnit(first.ObjId), _templates[449], null, DateTime.UtcNow);
        typeof(Buff).GetProperty("DuelContext", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(old, duel);
        second.Buffs.AddBuff(new Buff(second, second, new SkillCasterUnit(second.ObjId), _templates[11], null, DateTime.UtcNow));
        var effect = new SpecialEffect { SpecialEffectTypeId = SpecialType.ExplodeBuff, Value1 = 2000, Value2 = 50, Value3 = 50 };

        effect.Apply(first, new SkillCasterUnit(first.ObjId), second, new SkillCastUnitTarget(second.ObjId),
            new CastBuff(old), new EffectSource(), null, DateTime.UtcNow);

        await Assert.That(second.Buffs.CheckBuff(11)).IsTrue();
        await Assert.That(second.Hp).IsEqualTo(100);
    }

    [Test]
    public async Task ExplodeBuff_PreservesAreaMultiplierFromEffectSource()
    {
        _victim.Hp = _victim.MaxHp = 1000;
        Add(11, DateTime.UtcNow);
        var source = new EffectSource { AoeDamageMultiplier = 0.5f, IsItemProc = true, ItemProcLevel = 50 };
        var effect = new SpecialEffect { SpecialEffectTypeId = SpecialType.ExplodeBuff, Value1 = 1000, Value2 = 20, Value3 = 20 };

        effect.Apply(_thief, new SkillCasterUnit(_thief.ObjId), _victim, new SkillCastUnitTarget(_victim.ObjId),
            new CastSkill(16410, 1), source, null, DateTime.UtcNow);

        await Assert.That(_victim.Hp).IsEqualTo(900);
        await Assert.That(source.IsItemProc).IsTrue();
        await Assert.That(source.ItemProcLevel).IsEqualTo((byte)50);
    }

    private Skill AbsorbSkill()
    {
        var template = new SkillTemplate { Id = 11988, AbilityLevel = 1 };
        template.Effects.Add(new SkillEffect
        {
            EffectId = 3823, StartLevel = 1, EndLevel = 99, Friendly = true, NonFriendly = true,
            Front = true, Back = true, Chance = 100, ApplicationMethod = SkillEffectApplicationMethod.Source,
            Template = new SpecialEffect { SpecialEffectTypeId = SpecialType.RedeemBuff, Value1 = 4000, Value2 = 10, Value3 = 15 }
        });
        template.Effects.Add(new SkillEffect
        {
            EffectId = 3824, StartLevel = 1, EndLevel = 99, Friendly = true, NonFriendly = true,
            Front = true, Back = true, Chance = 100, ApplicationMethod = SkillEffectApplicationMethod.Target,
            Template = new BuffEffect { Buff = _templates[127], Chance = 100, Stack = 1, AbLevel = 1 }
        });
        return new Skill(template, _thief);
    }
}
