using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Formulas;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class CombatDamageFormulaTests
{
    private static readonly FieldInfo FormulaInstance = typeof(Singleton<FormulaManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo SkillInstance = typeof(Singleton<SkillManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly FieldInfo BuffInstance = typeof(Singleton<BuffGameData>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
    private object _previous;
    private object _previousSkills;
    private object _previousBuffs;
    private Dictionary<uint, Formula> _formulas;

    [Before(Test)]
    public void SetUp()
    {
        _previous = FormulaInstance.GetValue(null);
        _previousSkills = SkillInstance.GetValue(null);
        _previousBuffs = BuffInstance.GetValue(null);
        BuffInstance.SetValue(null, BuffBreakerTestData.Load());
        var skills = new SkillManager(null, null);
        BuffBreakerTestData.Configure(skills);
        SkillInstance.SetValue(null, skills);
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
    public void TearDown()
    {
        FormulaInstance.SetValue(null, _previous);
        SkillInstance.SetValue(null, _previousSkills);
        BuffInstance.SetValue(null, _previousBuffs);
    }

    [Test]
    [Arguments(39u, 106u, 1000, 30)]
    [Arguments(2250u, 1876u, 15000, 10)]
    public async Task AbandonedHouse_AuthoredTickNeedsConfirmedReturns(uint buffId, uint effectId, int interval, int damage)
    {
        var house = new House
        {
            ObjId = 7, Id = 19, OwnerId = 23, AccountId = 17, Template = new HousingTemplate { Hp = 1500 },
            Faction = new(), Hp = 1500, ProtectionEndDate = DateTime.UtcNow.AddDays(1)
        };
        var tick = HouseTick(house, buffId, effectId, interval, damage);
        house.AcknowledgeDemolitionReturns();
        tick.Template.TimeToTimeApply(house, house, tick);
        await Assert.That(house.Hp).IsEqualTo(1500);

        house.ProtectionEndDate = DateTime.UtcNow.AddSeconds(-1);
        house.AcknowledgeDemolitionReturns();
        tick.Template.TimeToTimeApply(house, house, tick);
        await Assert.That(house.Hp).IsEqualTo(1500);

        house.OwnerId = house.AccountId = 0;
        tick.Template.TimeToTimeApply(house, house, tick);
        await Assert.That(house.Hp).IsEqualTo(1500);

        house.AcknowledgeDemolitionReturns();
        tick.Template.TimeToTimeApply(house, house, tick);
        await Assert.That(house.Hp).IsEqualTo(1500 - damage);
        await Assert.That(house.Buffs.CheckBuff((uint)BuffConstants.Retribution)).IsFalse();

        house.OwnerId = 23;
        tick.Template.TimeToTimeApply(house, house, tick);
        await Assert.That(house.Hp).IsEqualTo(1500 - damage);
    }

    [Test]
    public async Task AbandonedHouse_CottageLeavesTheWorldAtZeroHealth()
    {
        var house = new House
        {
            ObjId = 7, Id = 19, TlId = 13, Template = new HousingTemplate { Id = 172, Hp = 1500 },
            Faction = new(), Hp = 1500, ProtectionEndDate = DateTime.UtcNow.AddSeconds(-1)
        };
        var world = new WorldInstance(new WorldTemplate { Id = 10 }, 0, true, 0);
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(house, world);
        world.AddObject(house);
        var mails = Mock.Of<IMailManager>();
        mails.GetMyHouseMails(house.Id).Returns([]);
        var manager = new HousingManager(null, null, null, null, null, SkillManager.Instance, Mock.Of<IHousingIdManager>().Object,
            Mock.Of<IHousingTldManager>().Object, null, mails.Object, null, null, null, null);
        var effectTasks = typeof(Singleton<EffectTaskManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldEffectTasks = effectTasks.GetValue(null);
        var previousWorldConfig = AppConfiguration.Instance.World;
        var instance = typeof(Singleton<HousingManager>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = instance.GetValue(null);
        try
        {
            AppConfiguration.Instance.World = new WorldConfig();
            instance.SetValue(null, manager);
            effectTasks.SetValue(null, new EffectTaskManager(Mock.Of<ITaskManager>().Object));
            ((Dictionary<uint, House>)typeof(HousingManager).GetField("_houses", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(manager)!).Add(house.Id, house);
            ((Dictionary<ushort, House>)typeof(HousingManager).GetField("_housesTl", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(manager)!).Add(house.TlId, house);
            var burnTemplate = HouseTick(house, 39, 106, 1000, 30).Template;
            var removalTemplate = HouseTick(house, 2250, 1876, 15000, 10).Template;
            typeof(SkillManager).GetField("_buffs", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
                SkillManager.Instance, new Dictionary<uint, BuffTemplate> { [39] = burnTemplate, [2250] = removalTemplate });
            manager.UpdateTaxInfo(house);
            await Assert.That(house.Buffs.CheckBuff(39)).IsFalse();
            house.AcknowledgeDemolitionReturns();
            manager.UpdateTaxInfo(house);
            var burn = house.Buffs.GetEffectFromBuffId(39);
            var removal = house.Buffs.GetEffectFromBuffId(2250);
            await Assert.That(burn).IsNotNull();
            await Assert.That(removal).IsNotNull();
            for (var ms = 1000; ms <= 48000; ms += 1000)
            {
                burn.Template.TimeToTimeApply(house, house, burn);
                if (ms % removal.Template.Tick == 0)
                    removal.Template.TimeToTimeApply(house, house, removal);
            }
            await Assert.That(house.Hp).IsEqualTo(30);
            await Assert.That(manager.GetHouseById(house.Id)).IsSameReferenceAs(house);
            burn.Template.TimeToTimeApply(house, house, burn);
            await Assert.That(house.Hp).IsEqualTo(0);
            await Assert.That(manager.GetHouseById(house.Id)).IsNull();
            await Assert.That(world.GetUnit(house.ObjId)).IsNull();
        }
        finally
        {
            instance.SetValue(null, previous);
            effectTasks.SetValue(null, oldEffectTasks);
            AppConfiguration.Instance.World = previousWorldConfig;
        }
    }

    private static Buff HouseTick(House house, uint buffId, uint effectId, int interval, int damage)
    {
        var globalId = buffId == 39 ? 286u : 12864u;
        var template = new BuffTemplate { Id = buffId, Tick = interval, Duration = buffId == 39 ? 99999999 : 0 };
        template.TickEffects.Add(new TickEffect { EffectId = globalId });
        var types = typeof(SkillManager).GetField("_types", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var typeMap = (Dictionary<uint, EffectType>)types.GetValue(SkillManager.Instance) ?? [];
        typeMap[globalId] = new EffectType { ActualId = effectId, Type = "DamageEffect" };
        types.SetValue(SkillManager.Instance, typeMap);
        var effects = typeof(SkillManager).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var effectMap = (Dictionary<string, Dictionary<uint, EffectTemplate>>)effects.GetValue(SkillManager.Instance) ?? [];
        if (!effectMap.TryGetValue("DamageEffect", out var damageEffects))
            effectMap["DamageEffect"] = damageEffects = [];
        damageEffects[effectId] = new DamageEffect
        {
            Id = effectId, DamageType = buffId == 39 ? DamageType.Magic : DamageType.Siege,
            UseFixedDamage = true, FixedMin = damage, FixedMax = damage, CheckCrime = true
        };
        effects.SetValue(SkillManager.Instance, effectMap);
        return new Buff(house, house, new SkillCasterUnit(house.ObjId), template, null, DateTime.UtcNow);
    }

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

    [Test]
    [Arguments(49, true)]
    [Arguments(50, true)]
    [Arguments(51, false)]
    public async Task DamageEffect_ThresholdImmunityIncludesBoundaryWithoutReducingLargerHits(int damage, bool immune)
    {
        var source = NewUnit(1);
        var target = NewUnit(2);
        AddImmunity(target, new BuffTemplate { Id = 275, MeleeImmune = true, ImmuneDamage = 50 });
        var damageEvents = 0;
        target.Events.OnDamaged += (_, _) => damageEvents++;

        Apply(source, target, damage: damage, healthSteal: 50, manaSteal: 25);

        await Assert.That(target.DamageReceived).IsEqualTo(immune ? 0 : damage);
        await Assert.That(source.SummarizeDamage).IsEqualTo(immune ? 0 : damage);
        await Assert.That(damageEvents).IsEqualTo(immune ? 0 : 1);
        await Assert.That(target.Packets.OfType<SCUnitDamagedPacket>().Single().HitType)
            .IsEqualTo(immune ? SkillHitType.Immune : SkillHitType.MeleeHit);
    }

    [Test]
    [Arguments(DamageType.Melee, true)]
    [Arguments(DamageType.Magic, true)]
    [Arguments(DamageType.Ranged, true)]
    [Arguments(DamageType.Siege, true)]
    [Arguments(DamageType.Heal, false)]
    public async Task CheckDamageImmune_RequiresTheMatchingTypeFlag(DamageType type, bool immune)
    {
        var target = NewUnit(2);
        AddImmunity(target, new BuffTemplate
        {
            Id = 2071, MeleeImmune = true, SpellImmune = true, RangedImmune = true, SiegeImmune = true,
            ImmuneDamage = 2000
        });

        await Assert.That(target.Buffs.CheckDamageImmune(type, 2000)).IsEqualTo(immune);
        await Assert.That(target.Buffs.CheckDamageImmune(type, 2001)).IsFalse();
        await Assert.That(target.Buffs.CheckDamageImmune(type)).IsFalse();
    }

    [Test]
    public async Task DamageEffect_MeleeThresholdDoesNotProtectAgainstMagic()
    {
        var source = NewUnit(1);
        var target = NewUnit(2);
        AddImmunity(target, new BuffTemplate { Id = 275, MeleeImmune = true, ImmuneDamage = 50 });

        Apply(source, target, DamageType.Magic, damage: 25);

        await Assert.That(target.DamageReceived).IsEqualTo(25);
    }

    [Test]
    public async Task DamageEffect_ZeroThresholdKeepsFullImmunity()
    {
        var source = NewUnit(1);
        var target = NewUnit(2);
        AddImmunity(target, new BuffTemplate { Id = 131, MeleeImmune = true });

        Apply(source, target, damage: 1000000);

        await Assert.That(target.DamageReceived).IsEqualTo(0);
        await Assert.That(target.Packets.OfType<SCUnitDamagedPacket>().Single().HitType).IsEqualTo(SkillHitType.Immune);
    }

    [Test]
    public async Task DamageEffect_ThresholdUsesTheFinalMitigatedHitAndDoesNotSteal()
    {
        var source = NewUnit(1);
        source.Hp = 100;
        source.Mp = 100;
        var target = NewUnit(2);
        target.Armor = 5300;
        AddImmunity(target, new BuffTemplate { Id = 275, MeleeImmune = true, ImmuneDamage = 50 });

        Apply(source, target, damage: 100, healthSteal: 50, manaSteal: 25);

        await Assert.That(target.DamageReceived).IsEqualTo(0);
        await Assert.That(source.Hp).IsEqualTo(100);
        await Assert.That(source.Mp).IsEqualTo(100);
    }

    [Test]
    public async Task CheckDamageImmune_CombinesThresholdsAndPreservesLargeAuthoredValues()
    {
        var target = NewUnit(2);
        AddImmunity(target, new BuffTemplate { Id = 1, MeleeImmune = true, ImmuneDamage = 50 });
        AddImmunity(target, new BuffTemplate { Id = 2, MeleeImmune = true, ImmuneDamage = 100 });
        AddImmunity(target, new BuffTemplate { Id = 3, SpellImmune = true, ImmuneDamage = 99999999999999L });
        AddImmunity(target, new BuffTemplate { Id = 3642, ImmuneDamage = 99999999999999L });

        await Assert.That(target.Buffs.CheckDamageImmune(DamageType.Melee, 100)).IsTrue();
        await Assert.That(target.Buffs.CheckDamageImmune(DamageType.Melee, 101)).IsFalse();
        await Assert.That(target.Buffs.CheckDamageImmune(DamageType.Magic, int.MaxValue)).IsTrue();
        await Assert.That(target.Buffs.CheckDamageImmune(DamageType.Ranged, 1)).IsFalse();
    }

    [Test]
    [Arguments(DamageType.Melee, 49, true)]
    [Arguments(DamageType.Melee, 51, false)]
    [Arguments(DamageType.Magic, 49, true)]
    [Arguments(DamageType.Magic, 51, false)]
    public async Task DamageEffect_ImmuneDotKeepsDamageBreakBuffsButStillTriggersAttackRemovals(
        DamageType type, int damage, bool immune)
    {
        var source = NewUnit(1);
        var target = NewUnit(2);
        AddImmunity(target, new BuffTemplate { Id = 2071, MeleeImmune = true, SpellImmune = true, ImmuneDamage = 50 });
        var sleep = AddImmunity(target, new BuffTemplate { Id = 1085, RemoveOnDamagedEtcDot = true });
        var spellSleep = AddImmunity(target, new BuffTemplate { Id = 1123, RemoveOnDamagedSpellDot = true });
        var damageBreak = AddImmunity(source, new BuffTemplate { Id = 20001, RemoveOnDamageEtcDot = true });
        var spellDamageBreak = AddImmunity(source, new BuffTemplate { Id = 20002, RemoveOnDamageSpellDot = true });
        var attackBreak = AddImmunity(source, new BuffTemplate { Id = 20003, RemoveOnAttackEtc = true });
        var attackedBreak = AddImmunity(target, new BuffTemplate { Id = 20004, RemoveOnAttackedEtc = true });
        var dot = new BuffTemplate { Id = 20005 };
        dot.TickEffects.Add(new TickEffect { EffectId = 1 });
        var damageEvents = 0;
        target.Events.OnDamaged += (_, _) => damageEvents++;

        Apply(source, target, type, damage: damage, buff: dot);

        await Assert.That(target.DamageReceived).IsEqualTo(immune ? 0 : damage);
        await Assert.That(damageEvents).IsEqualTo(immune ? 0 : 1);
        await Assert.That(sleep.State).IsEqualTo(immune ? EffectState.Acting : EffectState.Finished);
        await Assert.That(damageBreak.State).IsEqualTo(immune ? EffectState.Acting : EffectState.Finished);
        var spellBreaks = !immune && type == DamageType.Magic;
        await Assert.That(spellSleep.State).IsEqualTo(spellBreaks ? EffectState.Finished : EffectState.Acting);
        await Assert.That(spellDamageBreak.State).IsEqualTo(spellBreaks ? EffectState.Finished : EffectState.Acting);
        await Assert.That(attackBreak.State).IsEqualTo(EffectState.Finished);
        await Assert.That(attackedBreak.State).IsEqualTo(EffectState.Finished);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DamageEffect_DotWithoutMatchingThresholdKeepsModifierRemovalBeforeDamage(bool unrelatedThreshold)
    {
        var source = NewUnit(1);
        var target = NewUnit(2);
        target.MagicResistance = 5300;
        if (unrelatedThreshold)
            AddImmunity(target, new BuffTemplate { Id = 275, MeleeImmune = true, ImmuneDamage = 50 });
        // r208022 buff 4159 removes on spell DOT damage and reduces magic resistance by 100%.
        var modifier = new BonusTemplate
        {
            Attribute = UnitAttribute.MagicResist, ModifierType = UnitModifierType.Percent, Value = -100
        };
        var template = new BuffTemplate { Id = 4159, RemoveOnDamagedSpellDot = true };
        template.Bonuses.Add(modifier);
        var buff = AddImmunity(target, template);
        target.AddBonus(buff.Index, new Bonus { Template = modifier, Value = -100 });
        var dot = new BuffTemplate { Id = 20005 };
        dot.TickEffects.Add(new TickEffect { EffectId = 1 });
        await Assert.That(target.MagicResistance).IsEqualTo(0);

        Apply(source, target, DamageType.Magic, damage: 100, buff: dot);

        await Assert.That(buff.State).IsEqualTo(EffectState.Finished);
        await Assert.That(target.MagicResistance).IsEqualTo(5300);
        await Assert.That(target.DamageReceived).IsEqualTo(50);
    }

    private static Buff AddImmunity(DamageUnit target, BuffTemplate template)
    {
        var buffs = (List<Buff>)typeof(Buffs).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target.Buffs)!;
        var buff = new Buff(target, target, new SkillCasterUnit(target.ObjId), template, null, DateTime.UtcNow)
        {
            State = EffectState.Acting, Index = template.Id
        };
        buffs.Add(buff);
        return buff;
    }

    private static void Apply(DamageUnit source, DamageUnit target, DamageType type = DamageType.Melee,
        bool adjustByHeight = false, int damage = 10000, float aoeMultiplier = 1, int healthSteal = 0, int manaSteal = 0,
        BuffTemplate buff = null)
    {
        new DamageEffect
        {
            DamageType = type, UseFixedDamage = true, FixedMin = damage, FixedMax = damage,
            AdjustDamageByHeight = adjustByHeight, HealthStealRatio = healthSteal, ManaStealRatio = manaSteal
        }.Apply(source, new SkillCasterUnit(source.ObjId), target, new SkillCastUnitTarget(target.ObjId), new CastSkill(1, 1),
            new EffectSource { AoeDamageMultiplier = aoeMultiplier, Buff = buff }, null, DateTime.UtcNow);
    }

    private sealed class DamageUnit : Unit
    {
        public List<GamePacket> Packets { get; } = [];
        public int DamageReceived { get; private set; }
        public override int MagicResistance
        {
            get => (int)CalculateWithBonuses(base.MagicResistance, UnitAttribute.MagicResist);
            set => base.MagicResistance = value;
        }
        public override void ReduceCurrentHp(BaseUnit attacker, int value, KillReason killReason = KillReason.Damage)
        {
            DamageReceived += value;
            Hp -= value;
        }
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets.Add(packet);
    }
}
