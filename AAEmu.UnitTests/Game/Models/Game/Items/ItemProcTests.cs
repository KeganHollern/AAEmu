using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Items.Procs;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;

namespace AAEmu.UnitTests.Game.Models.Game.Items;

[NotInParallel]
public sealed class ItemProcTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    private static ItemProcTemplate Template(ProcChanceKind kind = ProcChanceKind.HitAny) => new()
    {
        Id = 1, ChanceKind = kind, ChanceRate = 100, CooldownSec = 10,
        SkillTemplate = new SkillTemplate { Id = 10, TargetType = SkillTargetType.Self }
    };

    [Test]
    public async Task Apply_FirstActivationAndCooldownBoundary_OnlySuccessfulCastsStartCooldown()
    {
        var proc = new ItemProc(Template());
        var unit = new Unit { Hp = 100 };
        var casts = 0;
        bool Cast(Unit _, Unit __, SkillTemplate ___, int level) { casts++; return true; }
        await Assert.That(proc.Apply(unit, unit, false, 100, 50, Now, () => 0, Cast)).IsTrue();
        await Assert.That(proc.Apply(unit, unit, false, 100, 50, Now.AddSeconds(9.999), () => 0, Cast)).IsFalse();
        await Assert.That(proc.Apply(unit, unit, false, 100, 50, Now.AddSeconds(10), () => 0, Cast)).IsTrue();
        await Assert.That(casts).IsEqualTo(2);
        await Assert.That(proc.LastProc).IsEqualTo(Now.AddSeconds(10));
    }

    [Test]
    [Arguments(0d, 0d, false)]
    [Arguments(3d, 0.029999d, true)]
    [Arguments(3d, 0.03d, false)]
    [Arguments(100d, 0.999999d, true)]
    public async Task Apply_ChanceBoundaries_UseHalfOpenProbability(double chance, double roll, bool expected)
    {
        var proc = new ItemProc(Template());
        var unit = new Unit { Hp = 100 };
        await Assert.That(proc.Apply(unit, unit, false, chance, 50, Now, () => roll, (_, _, _, _) => true)).IsEqualTo(expected);
    }

    [Test]
    public async Task Apply_FailedRollOrFailedCast_DoesNotConsumeCooldown()
    {
        var proc = new ItemProc(Template());
        var unit = new Unit { Hp = 100 };
        await Assert.That(proc.Apply(unit, unit, false, 50, 50, Now, () => 0.75, (_, _, _, _) => true)).IsFalse();
        await Assert.That(proc.LastProc).IsEqualTo(DateTime.MinValue);
        await Assert.That(proc.Apply(unit, unit, false, 100, 50, Now, () => 0, (_, _, _, _) => false)).IsFalse();
        await Assert.That(proc.LastProc).IsEqualTo(DateTime.MinValue);
        await Assert.That(proc.Apply(unit, unit, false, 100, 50, Now, () => 0, (_, _, _, _) => true)).IsTrue();
    }

    [Test]
    public async Task Apply_ForcedRoll_BypassesChanceButPreservesCooldownAndFinisher()
    {
        var template = Template();
        template.Finisher = true;
        var proc = new ItemProc(template);
        var unit = new Unit { Hp = 100 };
        double NoRoll() => throw new InvalidOperationException("Forced activation must not roll.");
        await Assert.That(proc.Apply(unit, unit, false, 0, 50, Now, NoRoll, (_, _, _, _) => true, true)).IsFalse();
        await Assert.That(proc.Apply(unit, unit, true, 0, 50, Now, NoRoll, (_, _, _, _) => true, true)).IsTrue();
        await Assert.That(proc.Apply(unit, unit, true, 0, 50, Now, NoRoll, (_, _, _, _) => true, true)).IsFalse();
    }

    [Test]
    [Arguments(SkillTargetType.Self)]
    [Arguments(SkillTargetType.Hostile)]
    [Arguments(SkillTargetType.AnyUnit)]
    public async Task Apply_TargetKinds_UseOwnerOrActualEventCounterpart(SkillTargetType type)
    {
        var template = Template();
        template.SkillTemplate.TargetType = type;
        var proc = new ItemProc(template);
        var owner = new Unit { ObjId = 1, Hp = 100 };
        var other = new Unit { ObjId = 2, Hp = 100 };
        Unit castTarget = null;
        var castLevel = 0;
        await Assert.That(proc.Apply(owner, other, false, 100, 45, Now, () => 0,
            (_, target, _, level) => { castTarget = target; castLevel = level; return true; })).IsTrue();
        await Assert.That(castTarget).IsSameReferenceAs(type == SkillTargetType.Self ? owner : other);
        await Assert.That(castLevel).IsEqualTo(45);
    }

    [Test]
    public async Task Apply_RecursiveZeroCooldownProc_CannotEnterItself()
    {
        var template = Template();
        template.CooldownSec = 0;
        var proc = new ItemProc(template);
        var unit = new Unit { Hp = 100 };
        var nested = true;
        await Assert.That(proc.Apply(unit, unit, false, 100, 50, Now, () => 0, (_, _, _, _) =>
        {
            nested = proc.Apply(unit, unit, false, 100, 50, Now, () => 0, (_, _, _, _) => true);
            return true;
        })).IsTrue();
        await Assert.That(nested).IsFalse();
    }

    [Test]
    [Arguments(3u, 6u, 50, 6d)]
    [Arguments(8u, 15u, 50, 15.5d)]
    [Arguments(99u, 30u, 50, 100d)]
    [Arguments(3u, 6u, -1, 3d)]
    public async Task GetChance_ApprovedServerRule_UsesItemLevelAndCapsAt100(uint rate, uint bonus, int level, double expected)
    {
        await Assert.That(UnitProcs.GetChance(new ItemProcTemplate
        {
            ChanceRate = rate, ItemLevelBasedChanceBonus = bonus
        }, level)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(DamageType.Melee, false, ProcChanceKind.HitMelee, ProcChanceKind.TakeDamageMelee)]
    [Arguments(DamageType.Melee, true, ProcChanceKind.HitMeleeCrit, ProcChanceKind.TakeDamageMeleeCrit)]
    [Arguments(DamageType.Magic, false, ProcChanceKind.HitSpell, ProcChanceKind.TakeDamageSpell)]
    [Arguments(DamageType.Magic, true, ProcChanceKind.HitSpellCrit, ProcChanceKind.TakeDamageSpellCrit)]
    [Arguments(DamageType.Ranged, false, ProcChanceKind.HitRanged, ProcChanceKind.TakeDamageRanged)]
    [Arguments(DamageType.Ranged, true, ProcChanceKind.HitRangedCrit, ProcChanceKind.TakeDamageRangedCrit)]
    [Arguments(DamageType.Siege, false, ProcChanceKind.HitSiege, ProcChanceKind.TakeDamageSiege)]
    public async Task OnDamage_DispatchesMatchingKindsToAttackerAndVictim(DamageType type, bool critical,
        ProcChanceKind attackKind, ProcChanceKind defenseKind)
    {
        var attacker = new ProcUnit { Hp = 100 };
        var target = new ProcUnit { Hp = 100 };
        var hits = new List<ProcChanceKind>();
        Equip(attacker, [Template(attackKind), WithId(Template(ProcChanceKind.HitAny), 2)], hits);
        Equip(target, [Template(defenseKind), WithId(Template(ProcChanceKind.TakeDamageAny), 2)], hits);
        UnitProcs.OnDamage(attacker, target, type, critical, false, true, false);
        await Assert.That(hits.Count).IsEqualTo(4);
        await Assert.That(hits.Contains(attackKind)).IsTrue();
        await Assert.That(hits.Contains(defenseKind)).IsTrue();
        await Assert.That(hits.Contains(ProcChanceKind.HitAny)).IsTrue();
        await Assert.That(hits.Contains(ProcChanceKind.TakeDamageAny)).IsTrue();
    }

    [Test]
    public async Task OnDamage_DisabledDamageEffectAndProcOrigin_DoNotRoll()
    {
        var attacker = new ProcUnit { Hp = 100 };
        var target = new ProcUnit { Hp = 100 };
        var hits = new List<ProcChanceKind>();
        Equip(attacker, [Template()], hits);
        UnitProcs.OnDamage(attacker, target, DamageType.Melee, false, false, false, false);
        UnitProcs.OnDamage(attacker, target, DamageType.Melee, false, false, true, true);
        await Assert.That(hits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OnHeal_CriticalHealing_DispatchesHealAndCriticalOnly()
    {
        var unit = new ProcUnit { Hp = 100 };
        var hits = new List<ProcChanceKind>();
        Equip(unit, [Template(ProcChanceKind.HitHeal), WithId(Template(ProcChanceKind.HitHealCrit), 2),
            WithId(Template(ProcChanceKind.HitAny), 3)], hits);
        unit.Procs.OnHeal(unit, true, false);
        await Assert.That(hits.Count).IsEqualTo(2);
        await Assert.That(hits.Contains(ProcChanceKind.HitAny)).IsFalse();
    }

    [Test]
    [Arguments(true, false, 1)]
    [Arguments(false, false, 0)]
    [Arguments(true, true, 0)]
    public async Task DamageEffect_RealEffectPath_RespectsAuthoredGateAndProcOrigin(bool fireProc, bool itemProc, int expected)
    {
        var owner = new ProcUnit { ObjId = 1, Hp = 100, MaxHp = 1000 };
        var target = new ProcUnit { ObjId = 2, Hp = 100, MaxHp = 1000 };
        var hits = new List<ProcChanceKind>();
        Equip(owner, [Template(ProcChanceKind.HitMelee)], hits);
        var effect = new DamageEffect
        {
            DamageType = DamageType.Melee, UseFixedDamage = true, FixedMin = 10, FixedMax = 10,
            FireProc = fireProc, CheckCrime = false
        };
        effect.Apply(owner, new SkillCasterUnit(1), target, new SkillCastUnitTarget(2), new CastSkill(10, 1),
            new EffectSource { IsItemProc = itemProc }, null, Now);
        await Assert.That(hits.Count).IsEqualTo(expected);
        await Assert.That(target.Hp).IsEqualTo(90);
    }

    [Test]
    [Arguments(false, 1)]
    [Arguments(true, 0)]
    public async Task HealEffect_RealEffectPath_RespectsProcOrigin(bool itemProc, int expected)
    {
        var owner = new ProcUnit { ObjId = 1, Hp = 100, MaxHp = 1000 };
        var target = new ProcUnit { ObjId = 2, Hp = 100, MaxHp = 1000 };
        var hits = new List<ProcChanceKind>();
        Equip(owner, [Template(ProcChanceKind.HitHeal)], hits);
        new HealEffect { UseFixedHeal = true, FixedMin = 10, FixedMax = 10 }.Apply(owner,
            new SkillCasterUnit(1), target, new SkillCastUnitTarget(2), new CastSkill(10, 1),
            new EffectSource { IsItemProc = itemProc }, null, Now);
        await Assert.That(hits.Count).IsEqualTo(expected);
        await Assert.That(target.Hp).IsEqualTo(110);
    }

    [Test]
    public async Task ProcTick_LevelDamageAndHealing_UseItemLevelWithoutOrdinarySkillContext()
    {
        var source = new EffectSource { IsItemProc = true, ItemProcLevel = 50 };
        await Assert.That(source.Skill).IsNull();
        await Assert.That(Math.Abs(source.GetLevelModifier(0, 49) - 0.49f) < 0.00001f).IsTrue();
        var intermediate = new EffectSource { IsItemProc = true, ItemProcLevel = 25 };
        await Assert.That(Math.Abs(intermediate.GetLevelModifier(0, 49) - 0.24f) < 0.00001f).IsTrue();
        await Assert.That(new EffectSource().GetLevelModifier(0, 49)).IsEqualTo(0f);
    }

    private static ItemProcTemplate WithId(ItemProcTemplate template, uint id)
    {
        template.Id = id;
        return template;
    }

    private static void Equip(ProcUnit owner, ItemProcTemplate[] templates, List<ProcChanceKind> hits)
    {
        var manager = Mock.Of<IItemManager>();
        foreach (var template in templates)
        {
            template.SkillTemplate.Id = template.Id;
            manager.GetItemProcTemplate(template.Id).Returns(template);
        }
        owner.SetProcs(new UnitProcs(owner, () => manager.Object, () => Now, () => 0,
            (_, _, skill, _) => { hits.Add(templates.Single(template => template.Id == skill.Id).ChanceKind); return true; }));
        owner.Procs.RefreshSources(templates.Select(template => new EquipmentProcSource(new EquipmentProcKey(1, 0, template.Id), 50)));
    }

    private sealed class ProcUnit : Unit
    {
        public void SetProcs(UnitProcs procs) => Procs = procs;
        public override void BroadcastPacket(GamePacket packet, bool self) { }
        public override void ReduceCurrentHp(BaseUnit attacker, int value, KillReason killReason = KillReason.Damage) => Hp -= value;
        public override void PostUpdateCurrentHp(BaseUnit attacker, int oldHp, int newHp, KillReason killReason = KillReason.Damage) { }
    }
}
