using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class CombatDamageFormulaTests
{
    private static readonly FieldInfo FormulaInstance = typeof(Singleton<FormulaManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private object _previous;
    private Dictionary<uint, Formula> _formulas;

    [Before(Test)]
    public void SetUp()
    {
        _previous = FormulaInstance.GetValue(null);
        var manager = new FormulaManager();
        FormulaInstance.SetValue(null, manager);
        var engine = new Jace.CalculationEngine();
        engine.AddFunction("if_negative", (double value, double negative, double other) => value < 0 ? negative : other);
        typeof(FormulaManager).GetProperty("CalculationEngine")!.SetValue(manager, engine);
        // Exact r208022 expressions from the pinned server compact.
        _formulas = new()
        {
            [11] = new("if_negative(range - 1, 1, 1.05 + min(range, 100) / 100)"),
            [12] = new("if_negative(range - 8, 0.8, 1)"),
            [20] = new("if_negative(pc_level - npc_level, 1, 1 + (pc_level - npc_level) * 0.04)"),
            [21] = new("1"),
            [24] = new("bulls_eye * 105"),
            [25] = new("flexibility * 90"),
            [26] = new("flexibility / 10")
        };
        typeof(FormulaManager).GetField("_formulas", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, _formulas);
    }

    [After(Test)]
    public void TearDown() => FormulaInstance.SetValue(null, _previous);

    [Test]
    [Arguments(DamageType.Ranged, 7.999f, 8000)]
    [Arguments(DamageType.Ranged, 8f, 10000)]
    [Arguments(DamageType.Ranged, 8.001f, 10000)]
    [Arguments(DamageType.Melee, 1f, 10000)]
    [Arguments(DamageType.Magic, 1f, 10000)]
    [Arguments(DamageType.Siege, 1f, 10000)]
    public async Task DamageEffect_RangePenaltyOnlyChangesRangedHitsBelowEightMetres(DamageType type, float range, int expected)
    {
        var source = NewUnit(1);
        var target = NewUnit(2);
        target.Transform.Local.SetPosition(0, range, 0);

        Apply(source, target, type);

        await Assert.That(target.DamageReceived).IsEqualTo(expected);
    }

    [Test]
    public async Task DamageEffect_RangeUsesThreeDimensionsAndWorldCoordinates()
    {
        var source = NewUnit(1);
        var target = NewUnit(2);
        var carrier = new Unit();
        carrier.Transform.Local.SetPosition(0, 0, 8);
        target.Transform.Parent = carrier.Transform;
        target.Transform.Local.SetPosition(0, 0, 0);

        Apply(source, target, DamageType.Ranged);

        await Assert.That(target.DamageReceived).IsEqualTo(10000);
    }

    [Test]
    [Arguments(-10f, true, 10000)]
    [Arguments(0f, true, 10000)]
    [Arguments(0.999f, true, 10000)]
    // DamageEffect retains single-precision damage and truncates the final value.
    [Arguments(1f, true, 10599)]
    [Arguments(50f, true, 15500)]
    [Arguments(100f, true, 20500)]
    [Arguments(200f, true, 20500)]
    [Arguments(100f, false, 10000)]
    public async Task DamageEffect_HeightUsesSignedDifferenceAuthoredBoundaryAndCap(float height, bool adjust, int expected)
    {
        var source = NewUnit(1);
        var target = NewUnit(2);
        source.Transform.Local.SetPosition(0, 0, height);

        Apply(source, target, adjustByHeight: adjust);

        await Assert.That(target.DamageReceived).IsEqualTo(expected);
    }

    [Test]
    public async Task DamageEffect_HeightUsesTheCarriedActorsWorldHeight()
    {
        var source = NewUnit(1);
        var target = NewUnit(2);
        var carrier = new Unit();
        carrier.Transform.Local.SetPosition(0, 0, 90);
        source.Transform.Parent = carrier.Transform;
        source.Transform.Local.SetPosition(0, 0, 10);

        Apply(source, target, adjustByHeight: true);

        await Assert.That(target.DamageReceived).IsEqualTo(20500);
    }

    [Test]
    [Arguments(50, 40, 1.4f)]
    [Arguments(40, 50, 1f)]
    [Arguments(50, 50, 1f)]
    public async Task LevelBonus_UsesPlayerAndNpcLevelsInTheirAuthoredOrder(int playerLevel, int npcLevel, float expected)
    {
        var source = new CharacterMock { Level = (byte)playerLevel };
        var target = new Npc { Level = (byte)npcLevel };

        await Assert.That(CombatFormulas.GetDamageMultiplier(source, target, DamageType.Melee, false))
            .IsEqualTo(expected).Within(0.00001f);
    }

    [Test]
    public async Task LevelPenalty_UsesTheLoadedExpressionOnlyForNpcAttacksAgainstPlayers()
    {
        _formulas[21] = new Formula("1 - (npc_level - pc_level) * 0.01");
        var player = new CharacterMock { Level = 40 };
        var npc = new Npc { Level = 50 };

        await Assert.That(CombatFormulas.GetDamageMultiplier(npc, player, DamageType.Melee, false)).IsEqualTo(0.9f);
        await Assert.That(CombatFormulas.GetDamageMultiplier(player, new CharacterMock { Level = 1 }, DamageType.Melee, false)).IsEqualTo(1f);
        await Assert.That(CombatFormulas.GetDamageMultiplier(npc, new Npc { Level = 1 }, DamageType.Melee, false)).IsEqualTo(1f);
        await Assert.That(CombatFormulas.GetDamageMultiplier(new Unit { Level = 50 }, player, DamageType.Melee, false)).IsEqualTo(1f);
    }

    [Test]
    public async Task DamageEffect_ChangesDamageAbsorptionStealAndEventsTogether()
    {
        var source = NewUnit(1);
        source.Hp = 100;
        source.Mp = 100;
        source.MeleeCritical = 100;
        source.RangedCritical = 100;
        source.RangedCriticalBonus = 50;
        var target = NewUnit(2);
        target.Armor = 5300;
        target.Flexibility = 1000;
        target.IncomingRangedDamageMul = 0.75f;
        target.Transform.Local.SetPosition(0, 7, 0);
        var eventDamage = -1;
        target.Events.OnDamaged += (_, args) => eventDamage = args.Amount;

        Apply(source, target, DamageType.Ranged, damage: 1000, aoeMultiplier: 0.4f, healthSteal: 50, manaSteal: 25);

        await Assert.That(target.DamageReceived).IsEqualTo(168);
        await Assert.That(source.Hp).IsEqualTo(184);
        await Assert.That(source.Mp).IsEqualTo(142);
        await Assert.That(source.SummarizeDamage).IsEqualTo(168);
        await Assert.That(eventDamage).IsEqualTo(168);
        var packet = target.Packets.OfType<SCUnitDamagedPacket>().Single().Write(new PacketStream());
        packet.ReadBytes(18);
        await Assert.That(packet.ReadPiscW(3).SequenceEqual([168L, 168L, 0L])).IsTrue();
    }

    [Test]
    [Arguments(DamageType.Melee, 99, 50f, 14901)]
    [Arguments(DamageType.Magic, 99, 50f, 14901)]
    [Arguments(DamageType.Ranged, 99, 50f, 14901)]
    [Arguments(DamageType.Melee, 101, 50f, 14899)]
    [Arguments(DamageType.Melee, 1000, 5f, 10000)]
    public async Task DamageEffect_ResilienceKeepsPartialRatingsAndCannotReverseCriticalDamage(DamageType type, int resilience, float bonus, int expected)
    {
        var source = NewUnit(1);
        source.MeleeCritical = source.SpellCritical = source.RangedCritical = 100;
        source.MeleeCriticalBonus = source.SpellCriticalBonus = source.RangedCriticalBonus = bonus;
        var target = NewUnit(2);
        target.Flexibility = resilience;
        target.Transform.Local.SetPosition(0, 10, 0);

        Apply(source, target, type);

        await Assert.That(target.DamageReceived).IsEqualTo(expected);
    }

    [Test]
    public async Task DamageEffect_ResilienceUsesTheTargetsFacetsForCriticalChance()
    {
        var source = NewUnit(1);
        source.MeleeCritical = 100;
        source.MeleeCriticalBonus = 100;
        var target = NewUnit(2);
        target.Flexibility = 999;
        target.Facets = 89910;

        Apply(source, target);

        await Assert.That(target.DamageReceived).IsEqualTo(10000);
        await Assert.That(target.Packets.OfType<SCUnitDamagedPacket>().Single().HitType).IsEqualTo(SkillHitType.MeleeHit);
    }

    [Test]
    [Arguments("dodge", DamageType.Melee, SkillHitType.MeleeDodge)]
    [Arguments("dodge", DamageType.Ranged, SkillHitType.RangedDodge)]
    [Arguments("block", DamageType.Melee, SkillHitType.MeleeBlock)]
    [Arguments("block", DamageType.Ranged, SkillHitType.RangedBlock)]
    [Arguments("parry", DamageType.Melee, SkillHitType.MeleeParry)]
    [Arguments("rangedParry", DamageType.Ranged, SkillHitType.RangedParry)]
    public async Task CombatDice_FocusUsesTheAttackersFacetsAgainstEachAvoidanceKind(string avoidance, DamageType type, SkillHitType avoided)
    {
        var source = NewUnit(1);
        var target = NewUnit(2);
        target.Transform.Local.SetPosition(0, 5, 0);
        switch (avoidance)
        {
            case "dodge": target.DodgeRate = 100; break;
            case "block": target.BlockRate = 100; break;
            case "parry": target.MeleeParryRate = 100; break;
            case "rangedParry": target.RangedParryRate = 100; break;
        }
        var skill = new Skill(new SkillTemplate { DamageTypeId = (byte)type });
        await Assert.That(skill.RollCombatDice(source, target)).IsEqualTo(avoided);
        source.BullsEye = 999;
        source.Facets = 104895;

        await Assert.That(skill.RollCombatDice(source, target))
            .IsEqualTo(type == DamageType.Melee ? SkillHitType.MeleeHit : SkillHitType.RangedHit);
    }

    private static DamageUnit NewUnit(uint id) => new()
    {
        ObjId = id, Hp = 100000, MaxHp = 100000, MaxMp = 100000
    };

    private static void Apply(DamageUnit source, DamageUnit target, DamageType type = DamageType.Melee,
        bool adjustByHeight = false, int damage = 10000, float aoeMultiplier = 1, int healthSteal = 0, int manaSteal = 0)
    {
        new DamageEffect
        {
            DamageType = type, UseFixedDamage = true, FixedMin = damage, FixedMax = damage,
            AdjustDamageByHeight = adjustByHeight, HealthStealRatio = healthSteal, ManaStealRatio = manaSteal
        }.Apply(source, new SkillCasterUnit(source.ObjId), target, new SkillCastUnitTarget(target.ObjId), new CastSkill(1, 1),
            new EffectSource { AoeDamageMultiplier = aoeMultiplier }, null, DateTime.UtcNow);
    }

    private sealed class DamageUnit : Unit
    {
        public List<GamePacket> Packets { get; } = [];
        public int DamageReceived { get; private set; }
        public override void ReduceCurrentHp(BaseUnit attacker, int value, KillReason killReason = KillReason.Damage)
        {
            DamageReceived += value;
            Hp -= value;
        }
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add(packet);
    }
}
