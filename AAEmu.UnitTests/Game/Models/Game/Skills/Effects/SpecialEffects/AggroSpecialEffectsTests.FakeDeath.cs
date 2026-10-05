using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Skills.Effects.SpecialEffects;

public sealed partial class AggroSpecialEffectsTests
{
    [Test]
    public async Task FakeDeath_PlayDeadTickRemovesIncomingThreatWithoutDeath()
    {
        var survivor = CreateNpc();
        var attacker = CreateNpc();
        var bystander = CreateCharacter();
        var template = new BuffTemplate { Id = 1010, Ragdoll = true };
        template.TickEffects.Add(new TickEffect { EffectId = 5408 });
        var effect = new SpecialEffect { Id = 1036, SpecialEffectTypeId = SpecialType.FakeDeath };
        typeof(SkillManager).GetField("_types", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(SkillManager.Instance, new Dictionary<uint, EffectType>
            {
                [5408] = new() { Id = 5408, ActualId = 1036, Type = "SpecialEffect" }
            });
        typeof(SkillManager).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(SkillManager.Instance, new Dictionary<string, Dictionary<uint, EffectTemplate>>
            {
                ["SpecialEffect"] = new() { [1036] = effect }
            });
        var buff = new Buff(survivor, survivor, null, template, null, DateTime.UtcNow)
        {
            State = EffectState.Acting, InUse = true, Duration = 7000, Tick = 1000
        };
        var activeBuffs = (List<Buff>)typeof(Buffs)
            .GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(survivor.Buffs)!;
        activeBuffs.Add(buff);
        var deaths = 0;
        survivor.Events.OnDeath += (_, _) => deaths++;
        attacker.AddUnitAggro(AggroKind.Heal, survivor, 100);
        attacker.AddUnitAggro(AggroKind.Heal, bystander, 50);
        attacker.CurrentTarget = survivor;
        attacker.CurrentAggroTarget = survivor;

        template.TimeToTimeApply(survivor, survivor, buff);
        // A later authored tick must also remove newly acquired threat.
        attacker.AddUnitAggro(AggroKind.Heal, survivor, 100);
        template.TimeToTimeApply(survivor, survivor, buff);

        await Assert.That(attacker.AggroTable.ContainsKey(survivor.ObjId)).IsFalse();
        await Assert.That(attacker.AggroTable.ContainsKey(bystander.ObjId)).IsTrue();
        await Assert.That(attacker.CurrentTarget).IsNull();
        await Assert.That(attacker.CurrentAggroTarget).IsNull();
        await Assert.That(survivor.Hp).IsEqualTo(100);
        await Assert.That(survivor.IsDead).IsFalse();
        await Assert.That(survivor.DeadTime).IsEqualTo(DateTime.MinValue);
        await Assert.That(survivor.Despawned).IsFalse();
        await Assert.That(deaths).IsEqualTo(0);
        await Assert.That(buff.State).IsEqualTo(EffectState.Acting);
        await Assert.That(buff.InUse).IsTrue();
        await Assert.That(survivor.Buffs.CheckBuff(1010)).IsTrue();
        await Assert.That(survivor.Sent.OfType<SCUnitDeathPacket>()).IsEmpty();
        await Assert.That(survivor.ParentWorld.GetUnit(survivor.ObjId)).IsSameReferenceAs(survivor);
    }

    [Test]
    public async Task FakeDeath_CombatEscapeSkillDropsNpcThreatButKeepsPlayerTarget()
    {
        var survivor = CreateCharacter();
        var attacker = CreateNpc();
        var otherPlayer = CreateCharacter();
        otherPlayer.CurrentTarget = survivor;
        attacker.AddUnitAggro(AggroKind.Heal, survivor, 50);
        var skill = new Skill(new SkillTemplate { Id = 26966 }, survivor);
        var effect = new SpecialEffect { Id = 16829, SpecialEffectTypeId = SpecialType.FakeDeath };

        effect.Apply(survivor, null, survivor, null, null, new EffectSource(skill), null, DateTime.UtcNow);

        await Assert.That(attacker.AggroTable).IsEmpty();
        await Assert.That(survivor.IsInAggroListOf).IsEmpty();
        await Assert.That(otherPlayer.CurrentTarget).IsSameReferenceAs(survivor);
        await Assert.That(survivor.Hp).IsEqualTo(100);
    }

    [Test]
    public async Task FakeDeath_DeferredDownfallImpactDoesNotApplyCombatEscape()
    {
        var attacker = CreateNpc();
        var victim = CreateCharacter();
        attacker.AddUnitAggro(AggroKind.Heal, victim, 50);
        attacker.CurrentTarget = victim;
        attacker.CurrentAggroTarget = victim;
        var effect = new SpecialEffect
        {
            Id = 925, SpecialEffectTypeId = SpecialType.FakeDeath,
            Value1 = 700, Value2 = 70
        };
        var skill = new Skill(new SkillTemplate { Id = 12279 }, attacker);

        effect.Apply(attacker, null, victim, null, null, new EffectSource(skill), null, DateTime.UtcNow);

        await Assert.That(attacker.AggroTable.ContainsKey(victim.ObjId)).IsTrue();
        await Assert.That(attacker.CurrentTarget).IsSameReferenceAs(victim);
        await Assert.That(attacker.CurrentAggroTarget).IsSameReferenceAs(victim);
        await Assert.That(victim.Hp).IsEqualTo(100);
    }

    [Test]
    public async Task FakeDeath_DeadTargetDoesNotChangeThreatOrRevive()
    {
        var survivor = CreateNpc();
        var attacker = CreateNpc();
        attacker.AddUnitAggro(AggroKind.Heal, survivor, 50);
        survivor.Hp = 0;

        Execute(new FakeDeath(), survivor, survivor);

        await Assert.That(attacker.AggroTable.ContainsKey(survivor.ObjId)).IsTrue();
        await Assert.That(survivor.Hp).IsEqualTo(0);
    }
}
