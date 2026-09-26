using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.Skills.Plots;
using AAEmu.Game.Models.Game.Skills.Plots.Tree;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class SkillCooldownTests
{
    private readonly Dictionary<FieldInfo, object> _previousInstances = [];
    private static readonly DateTime Start = new(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

    [Before(Test)]
    public void SetUp()
    {
        SetInstance(new SkillManager(null, null));
        SetInstance(new ZoneManager(null, null));
        SetInstance(new WorldManager(null, null, null, null, null));
        SetInstance(new UnitRequirementsGameData());
        SetInstance(new SkillRequirementsGameData());
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (field, previous) in _previousInstances)
            field.SetValue(null, previous);
    }

    private void SetInstance<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        _previousInstances.Add(field, field.GetValue(null));
        field.SetValue(null, instance);
    }

    [Test]
    [Arguments(200, false)]
    [Arguments(949, false)]
    [Arguments(950, true)]
    [Arguments(1000, true)]
    public async Task GlobalCooldown_RejectsTheSecondCastUntilTheDeadline(int elapsed, bool accepted)
    {
        var unit = new Unit();
        var skill = new SkillTemplate { Id = 1, DefaultGcd = true };
        await Assert.That(SkillCooldowns.TryStartGlobalCooldown(unit, skill, false, Start)).IsTrue();
        await Assert.That(unit.GlobalCooldown).IsEqualTo(Start.AddMilliseconds(1000));
        await Assert.That(SkillCooldowns.Check(unit, skill, false, Start.AddMilliseconds(elapsed)))
            .IsEqualTo(accepted ? SkillResult.Success : SkillResult.CooldownTime);
        await Assert.That(SkillCooldowns.TryStartGlobalCooldown(unit, skill, false, Start.AddMilliseconds(elapsed)))
            .IsEqualTo(accepted);
    }

    [Test]
    public async Task ShortGlobalCooldown_UsesAtMostFivePercentTolerance()
    {
        var unit = new Unit();
        var skill = new SkillTemplate { CustomGcd = 200 };
        await Assert.That(SkillCooldowns.TryStartGlobalCooldown(unit, skill, false, Start)).IsTrue();
        await Assert.That(SkillCooldowns.Check(unit, skill, false, Start.AddMilliseconds(150)))
            .IsEqualTo(SkillResult.CooldownTime);
        await Assert.That(SkillCooldowns.Check(unit, skill, false, Start.AddMilliseconds(190)))
            .IsEqualTo(SkillResult.Success);
    }

    [Test]
    public async Task IgnoreAndInternalBypass_DoNotBypassASharedCooldown()
    {
        var unit = new Unit { GlobalCooldown = Start.AddSeconds(1) };
        var skill = new SkillTemplate { Id = 11718, CooldownTagId = 30, IgnoreGlobalCooldown = true };
        await Assert.That(SkillCooldowns.Check(unit, skill, false, Start)).IsEqualTo(SkillResult.Success);
        skill.IgnoreGlobalCooldown = false;
        await Assert.That(SkillCooldowns.Check(unit, skill, true, Start)).IsEqualTo(SkillResult.Success);
        unit.Cooldowns.AddCooldown(11715, 90000, 30);
        await Assert.That(SkillCooldowns.Check(unit, skill, true, Start)).IsEqualTo(SkillResult.CooldownTime);
    }

    [Test]
    public async Task NoCustomDuration_DoesNotClearAnotherSkillsGlobalCooldown()
    {
        var unit = new Unit { GlobalCooldown = Start.AddSeconds(1) };
        var skill = new SkillTemplate { Id = 11718, IgnoreGlobalCooldown = true };
        await Assert.That(SkillCooldowns.TryStartGlobalCooldown(unit, skill, false, Start)).IsTrue();
        await Assert.That(unit.GlobalCooldown).IsEqualTo(Start.AddSeconds(1));
    }

    [Test]
    public async Task ConcurrentGlobalAdmission_ReservesOnlyOneCast()
    {
        var unit = new Unit();
        var skill = new SkillTemplate { Id = 1, DefaultGcd = true };
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => SkillCooldowns.TryStartGlobalCooldown(unit, skill, false, Start))));
        await Assert.That(results.Count(result => result)).IsEqualTo(1);
    }

    [Test]
    [Arguments(-2000, 5000u)]
    [Arguments(-800, 5000u)]
    [Arguments(0, 1000u)]
    [Arguments(1000, 500u)]
    [Arguments(4000, 200u)]
    [Arguments(10000, 200u)]
    public async Task AuthoredLimit_ClampsRawBonusBeforeDurationConversion(int bonus, uint expected)
    {
        var data = new GlobalCooldownGameData();
        var unit = new Unit { GlobalCooldownMul = data.GetMultiplier(bonus) };
        await Assert.That(SkillCooldowns.ScaleGlobalCooldown(unit, 1000)).IsEqualTo(expected);
        var skill = new SkillTemplate { CustomGcd = 1500 };
        await Assert.That(SkillCooldowns.TryStartGlobalCooldown(unit, skill, false, Start)).IsTrue();
        await Assert.That(unit.GlobalCooldown).IsEqualTo(Start.AddMilliseconds(expected * 1.5));
    }

    [Test]
    public async Task LimitLoader_UsesTheAuthoredRow()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE unit_attribute_limits(unit_attribute_id INT, minimum INT, maximum INT); INSERT INTO unit_attribute_limits VALUES(74,-500,1000)";
        command.ExecuteNonQuery();
        var data = new GlobalCooldownGameData();
        data.Load(connection);
        await Assert.That(data.GetMultiplier(-900)).IsEqualTo(200f);
        await Assert.That(data.GetMultiplier(9000)).IsEqualTo(50f);
    }

    [Test]
    public async Task Use_ActiveGlobalCooldownRejectsBeforePlotAndMana()
    {
        var world = new WorldInstance(new WorldTemplate
        {
            Id = 1, CellX = 1, CellY = 1,
            ZoneKeyByRegions = new uint[WorldManager.SECTORS_PER_CELL, WorldManager.SECTORS_PER_CELL]
        }, 0, true, 0);
        var unit = new ProbeUnit { ObjId = 70, Hp = 100, Mp = 100,
            GlobalCooldown = DateTime.UtcNow.AddMilliseconds(800) };
        typeof(GameObject).GetField("_parentWorld", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(unit, world);
        world.AddObject(unit);
        var template = new SkillTemplate { Id = 50, SourceAlive = true, TargetType = SkillTargetType.Self,
            DefaultGcd = true, ManaCost = 20, Plot = new Plot(), PlotOnly = true };
        var skill = new Skill(template);
        var result = skill.Use(unit, new SkillCasterUnit(70), new SkillCastUnitTarget(70), new SkillObject(), false, out var detail);
        await Assert.That(result).IsEqualTo(SkillResult.CooldownTime);
        await Assert.That(detail).IsEqualTo(0u);
        await Assert.That(unit.Mp).IsEqualTo(100);
        await Assert.That(unit.Packets).IsEqualTo(0);
        await Assert.That(skill.TlId).IsEqualTo((ushort)0);
        await Assert.That(unit.ActivePlotState).IsNull();
    }

    [Test]
    [Arguments(false, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, false, true)]
    public async Task PlotCompletion_StartsTheModifiedSharedTimerOnlyAfterFiring(bool cancelled, bool casting, bool expected)
    {
        var unit = new ProbeUnit();
        var skill = new Skill(new SkillTemplate { Id = 11715, CooldownTagId = 30, CooldownTime = 90000 });
        var state = new PlotState(unit, null, unit, null, null, skill);
        if (casting)
            state.RegisterCastWait(new PlotNextEvent { Casting = true }, DateTime.UtcNow.AddSeconds(1));
        if (cancelled)
            state.RequestCancellation();
        typeof(PlotTree).GetMethod("DoPlotEnd", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [state]);
        await Assert.That(unit.Cooldowns.CheckTagCooldown(30)).IsEqualTo(expected);
        await Assert.That(unit.Cooldowns.GetActiveBuckets(150).Skills).IsEmpty();
        if (expected)
            await Assert.That(unit.Cooldowns.GetActiveBuckets(150).Tags.Single().Duration).IsEqualTo(45000u);
    }

    private sealed class ProbeUnit : Unit
    {
        public int Packets { get; private set; }
        public override void BroadcastPacket(GamePacket packet, bool self) => Packets++;
        public override double ApplySkillModifiers(Skill skill, SkillAttribute attribute, double baseValue)
            => attribute == SkillAttribute.Cooldown ? baseValue / 2 : baseValue;
    }

    [Test]
    [Arguments(0u)]
    [Arguments(30u)]
    public async Task CharacterReset_ClearsAndSendsOnlyTheExplicitSharedTag(uint tagId)
    {
        var session = Mock.Of<ISession>();
        var character = new Character(null)
        {
            ObjId = 70,
            Connection = new GameConnection(session.Object)
        };
        character.Cooldowns.AddCooldown(11715, 60000);
        character.Cooldowns.AddCooldown(11715, 90000, 30);
        character.Cooldowns.AddCooldown(60, 90000, 144);

        character.ResetSkillCooldown(11715, tagId, false);

        await Assert.That(character.Cooldowns.Contains(11715)).IsFalse();
        await Assert.That(character.Cooldowns.CheckTagCooldown(30)).IsEqualTo(tagId == 0);
        await Assert.That(character.Cooldowns.CheckTagCooldown(144)).IsTrue();
        session.SendPacket(Is<byte[]>(packet =>
            BitConverter.ToUInt32(packet, packet.Length - 9) == 11715 &&
            BitConverter.ToUInt32(packet, packet.Length - 5) == tagId && packet[^1] == 0))
            .WasCalled(Times.Once);
    }

    [Test]
    public async Task ResetEffect_ClearsOnlyTheRequestedNativeBucketsAndGlobalTimer()
    {
        var unit = new Unit { GlobalCooldown = DateTime.UtcNow.AddMinutes(1) };
        unit.Cooldowns.AddCooldown(50, 60000);
        unit.Cooldowns.AddCooldown(11715, 90000, 30);
        unit.Cooldowns.AddCooldown(60, 90000, 144);
        new ResetCooldown().Execute(unit, null, unit, null, null, new Skill(new SkillTemplate()), null,
            DateTime.UtcNow, 50, 30, 1, 0);
        await Assert.That(unit.Cooldowns.CheckCooldown(50)).IsFalse();
        await Assert.That(unit.Cooldowns.CheckTagCooldown(30)).IsFalse();
        await Assert.That(unit.Cooldowns.CheckTagCooldown(144)).IsTrue();
        await Assert.That(unit.GlobalCooldown).IsEqualTo(DateTime.MinValue);
    }
}
