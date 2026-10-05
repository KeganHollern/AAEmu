using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects.SpecialEffects;

public sealed partial class BuffSpecialEffectsTests
{
    [Test]
    public async Task Leech_TransferredItemProcBuffKeepsOriginAndItemLevelOnItsNextTick()
    {
        // Item 28426 (level 25) -> proc 93 -> skill 22707 -> Good buff 6286
        // -> tick effect 31731 (HealEffect 439). Capture the tick's real source.
        var now = DateTime.UtcNow;
        var template = new BuffTemplate { Id = 6286, Kind = BuffKind.Good, Duration = 5000, Tick = 1000 };
        template.TickEffects.Add(new TickEffect { EffectId = 31731 });
        _templates[6286] = template;
        var capture = new CaptureProcTickEffect();
        typeof(SkillManager).GetField("_types", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(SkillManager.Instance, new Dictionary<uint, EffectType>
            {
                [31731] = new() { ActualId = 439, Type = "HealEffect" }
            });
        typeof(SkillManager).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(SkillManager.Instance, new Dictionary<string, Dictionary<uint, EffectTemplate>>
            {
                ["HealEffect"] = new() { [439] = capture }
            });
        var proc = new Skill(new SkillTemplate { Id = 22707 }, _victim) { IsItemProc = true, Level = 25 };
        _victim.Buffs.AddBuff(new Buff(_victim, _victim, new SkillCasterUnit(_victim.ObjId), template, proc, now)
            { AbLevel = 25 });

        ApplyLeech(now);
        var stolen = _thief.Buffs.GetEffectFromBuffId(6286);
        await Assert.That(stolen).IsNotNull();
        template.TimeToTimeApply(_thief, _thief, stolen);

        await Assert.That(_victim.Buffs.CheckBuff(6286)).IsFalse();
        await Assert.That(stolen.Skill).IsNull();
        await Assert.That(stolen.IsItemProc).IsTrue();
        await Assert.That(stolen.ItemProcLevel).IsEqualTo((byte)25);
        await Assert.That(capture.Source.IsItemProc).IsTrue();
        await Assert.That(capture.Source.ItemProcLevel).IsEqualTo((byte)25);
        await Assert.That(capture.Source.Skill).IsNull();
        await Assert.That(capture.Source.Buff).IsSameReferenceAs(template);
        await Assert.That(Math.Abs(capture.Source.GetLevelModifier(0, 49) - 0.24f) < 0.00001f).IsTrue();
    }

    private sealed class CaptureProcTickEffect : EffectTemplate
    {
        public EffectSource Source { get; private set; }
        public override bool OnActionTime => true;

        public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
            CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
            CompressedGamePackets packetBuilder = null)
        {
            Source = source;
        }
    }
}
