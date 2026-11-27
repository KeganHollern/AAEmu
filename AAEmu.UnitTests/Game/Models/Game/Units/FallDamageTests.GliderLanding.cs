using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Models.Game.Skills;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

public sealed partial class FallDamageTests
{
    [Test]
    [Arguments(1029u, true, 1.106f, 0.852f)]
    [Arguments(1371u, false, 0.600f, 0.410f)]
    [Arguments(1029u, true, 0.900f, 0.500f)]
    public async Task GliderLanding_UsesItsStockEffectsOnce(uint id, bool preparing, float reportZ, float nextZ)
    {
        var (player, flight, results) = CreateLandingFlight(id, preparing);
        player.Transform.Local.SetPosition(10, 10, reportZ);
        await Assert.That(player.TryCloseGliderForLanding(flight, 10)).IsTrue();
        await Assert.That(flight.IsEnded()).IsTrue();
        await Assert.That(results.Count(r => r == 4622)).IsEqualTo(preparing ? 1 : 0);
        await Assert.That(results.Count(r => r == 1238)).IsEqualTo(reportZ <= flight.Template.GlidingLandHeight ? 1 : 0);
        player.Transform.Local.SetPosition(10.61f, 10, nextZ);
        player.ObserveFallMovement(false, 10.055);
        player.Transform.Local.SetPosition(11, 10, 0);
        player.ObserveFallMovement(false, 10.269);
        player.ObserveFallMovement(false, 10.36);
        player.Transform.Local.SetPosition(11, 10, 1.0f);
        player.ObserveFallMovement(false, 11);
        await Assert.That(results.Count(r => r == 1238)).IsEqualTo(1);
        await Assert.That(results.Count(r => r == 4622)).IsEqualTo(preparing ? 1 : 0);
        await Assert.That(player.Buffs.GetEffectFromBuffId(1238)?.Duration).IsEqualTo(2000);
        await Assert.That(player.Buffs.CheckBuff(4622)).IsEqualTo(preparing);
        await Assert.That(player.Hp).IsEqualTo(9246);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GliderLanding_AWaitOrTurnBeforeTheThresholdKeepsOnlyTheOriginalDescent(bool rise)
    {
        var (player, flight, results) = CreateLandingFlight(1029, true);
        player.Transform.Local.SetPosition(10, 10, 1.20f);
        await Assert.That(player.TryCloseGliderForLanding(flight, 10)).IsTrue();
        await Assert.That(player.TryCloseGliderForLanding(flight, 10.001)).IsFalse();
        player.ObserveFallMovement(false, 10.02);
        player.Transform.Local.SetPosition(10.2f, 10, 1.10f);
        player.ObserveFallMovement(false, 10.06);
        if (rise)
        {
            player.Transform.Local.SetPosition(10.25f, 10, 1.15f);
            player.ObserveFallMovement(false, 10.10);
        }
        player.Transform.Local.SetPosition(10.6f, 10, 0.90f);
        player.ObserveFallMovement(false, 10.16);
        player.ObserveFallMovement(false, 10.19);
        await Assert.That(results.Count(id => id == 1238)).IsEqualTo(rise ? 0 : 1);
        await Assert.That(results.Count(id => id == 4622)).IsEqualTo(1);
    }

    [Test]
    public async Task GliderLanding_MorePositionsDoNotExtendTheOriginalDeadline()
    {
        var (player, flight, results) = CreateLandingFlight(1029, true);
        player.Transform.Local.SetPosition(10, 10, 1.20f);
        await Assert.That(player.TryCloseGliderForLanding(flight, 10)).IsTrue();
        player.Transform.Local.SetPosition(10.2f, 10, 1.10f);
        player.ObserveFallMovement(false, 10.16);
        player.Transform.Local.SetPosition(10.6f, 10, 0.90f);
        player.ObserveFallMovement(false, 10.26);
        await Assert.That(results).IsEquivalentTo(new uint[] { 4622 });
    }

    [Test]
    public async Task GliderLanding_MeasuresTravelFromTheOriginalClosure()
    {
        var (player, flight, results) = CreateLandingFlight(1029, true);
        player.Transform.Local.SetPosition(10, 10, 1.20f);
        await Assert.That(player.TryCloseGliderForLanding(flight, 10)).IsTrue();
        player.Transform.Local.SetPosition(10.6f, 10, 1.15f);
        player.ObserveFallMovement(false, 10.07);
        player.Transform.Local.SetPosition(11.2f, 10, 1.10f);
        player.ObserveFallMovement(false, 10.14);
        player.Transform.Local.SetPosition(11.8f, 10, 0.90f);
        player.ObserveFallMovement(false, 10.21);
        await Assert.That(results).IsEquivalentTo(new uint[] { 4622 });
    }

    [Test]
    [Arguments("late")]
    [Arguments("new flight")]
    [Arguments("rising")]
    [Arguments("relocated")]
    [Arguments("mounted")]
    [Arguments("position lock")]
    [Arguments("missing terrain")]
    [Arguments("water")]
    public async Task GliderLanding_DiscardsTheUnconfirmedGroundEffect(string change)
    {
        var (player, flight, results) = CreateLandingFlight(1029, true);
        player.Transform.Local.SetPosition(10, 10, 1.106f);
        await Assert.That(player.TryCloseGliderForLanding(flight, 10)).IsTrue();
        player.Transform.Local.SetPosition(change == "relocated" ? 210 : 10, 10, change == "rising" ? 1.2f : 0.852f);
        if (change == "position lock")
        {
            player.DisabledSetPosition = true;
            player.DisabledSetPosition = false;
        }
        if (change == "mounted")
            player.IsRiding = true;
        if (change == "missing terrain")
            player.ParentWorld = CreateWorld();
        if (change == "water")
            player.ParentWorld.Water.OceanLevel = 100;
        if (change == "new flight")
            _ = new Buff(player, player, new SkillCasterUnit(1), flight.Template, null, DateTime.UtcNow) { InUse = true };
        player.ObserveFallMovement(false, change == "late" ? 14 : 10.055);
        player.IsRiding = false;
        player.Transform.Local.SetPosition(10, 10, 0);
        player.ObserveFallMovement(true, 15);
        await Assert.That(results).IsEquivalentTo(new uint[] { 4622 });
    }

    [Test]
    public async Task GliderLanding_HighFoldKeepsTheOrdinaryExit()
    {
        var (player, flight, results) = CreateLandingFlight(1029, true);
        player.Transform.Local.SetPosition(10, 10, 11.506f);
        await Assert.That(player.TryCloseGliderForLanding(flight, 10)).IsFalse();
        var fold = new DispelEffect { Id = 428, CureCount = 1, BuffTagId = 294 };
        fold.Apply(player, new SkillCasterUnit(player.ObjId), player, new SkillCastUnitTarget(player.ObjId),
            null, new EffectSource((Skill)null), null, DateTime.UtcNow);
        await Assert.That(flight.IsEnded()).IsTrue();
        player.Transform.Local.SetPosition(11, 10, 0);
        player.ObserveFallMovement(true, 12);
        await Assert.That(results).DoesNotContain(1238u);
        await Assert.That(results).Contains(4622u);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GliderLanding_FearAndExpiryKeepTheirOrdinaryExit(bool fear)
    {
        var (player, flight, results) = CreateLandingFlight(1029, true);
        player.Transform.Local.SetPosition(10, 10, 0.85f);
        if (fear)
            player.Buffs.AddBuff(new Buff(player, player, new SkillCasterUnit(player.ObjId),
                SkillManager.Instance.GetBuffTemplate(156), null, DateTime.UtcNow));
        else
        {
            flight.StartTime = DateTime.UtcNow.AddSeconds(-181);
            flight.EndTime = DateTime.UtcNow.AddSeconds(-1);
            flight.ScheduleEffect(false);
        }
        await Assert.That(flight.IsEnded()).IsTrue();
        player.Transform.Local.SetPosition(10, 10, 0);
        player.ObserveFallMovement(true, 10);
        await Assert.That(results).DoesNotContain(1238u);
        await Assert.That(player.Buffs.CheckBuff(4622)).IsTrue();
    }

    [Test]
    [Arguments(1645u)]
    [Arguments(1958u)]
    [Arguments(8283u)]
    public async Task GliderLanding_LeavesSpecialTimeoutActionsOnTheirOriginalPath(uint id)
    {
        var (player, flight, _) = CreateLandingFlight(id, false);
        var triggers = SkillManager.Instance.GetBuffTriggerTemplates(id);
        triggers.Add(new BuffTriggerTemplate { Kind = BuffEventTriggerKind.Timeout, Effect = new KillNpcWithoutCorpseEffect() });
        player.Transform.Local.SetPosition(10, 10, 0.85f);
        await Assert.That(player.TryCloseGliderForLanding(flight, 10)).IsFalse();
        await Assert.That(flight.InUse).IsTrue();
    }

    private (LandingPlayer, Buff, List<uint>) CreateLandingFlight(uint id, bool preparing)
    {
        SetGliderManager(new PermissionManager(null));
        SetGliderManager(new TaskManager(null));
        SetGliderManager(new EffectTaskManager(Mock.Of<ITaskManager>().Object));
        using var database = BuffBreakerTestData.Connection();
        using var command = database.CreateCommand();
        command.CommandText = "INSERT INTO buff_breakers VALUES (1029,12)";
        command.ExecuteNonQuery();
        var breakers = new BuffGameData();
        breakers.Load(database);
        breakers.PostLoad();
        SetGliderManager(breakers);
        var manager = SkillManager.Instance;
        BuffBreakerTestData.Configure(manager);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var subscriptions = (Dictionary<uint, List<BuffTriggerTemplate>>)typeof(SkillManager)
            .GetField("_buffTriggers", flags)!.GetValue(manager)!;
        var tagged = (Dictionary<uint, List<uint>>)typeof(SkillManager).GetField("_taggedBuffs", flags)!.GetValue(manager)!;
        tagged[294] = [id];
        var templates = (Dictionary<uint, BuffTemplate>)typeof(SkillManager).GetField("_buffs", flags)!.GetValue(manager)!;
        templates[1238] = new BuffTemplate { Id = 1238, Kind = BuffKind.Hidden, Root = true, Fastened = true,
            Duration = 2000, StackRule = BuffStackRule.Refresh, MaxStack = 1 };
        templates[4622] = new BuffTemplate { Id = 4622, Kind = BuffKind.Bad,
            Duration = 5000, StackRule = BuffStackRule.Refresh, MaxStack = 1 };
        var results = new List<uint>();
        var landing = new List<BuffTriggerTemplate> { Trigger(BuffEventTriggerKind.Landing, 1238) };
        if (preparing)
        {
            landing.Add(Trigger(BuffEventTriggerKind.Landing, 4622));
            landing.Add(Trigger(BuffEventTriggerKind.Timeout, 4622));
            landing.Add(Trigger(BuffEventTriggerKind.Dispelled, 4622));
        }
        subscriptions.Add(id, landing);
        var template = new BuffTemplate
        {
            Id = id, Kind = BuffKind.Good, Gliding = true, RemoveOnLand = true, Duration = 180000,
            GlidingLandHeight = id == 1371 ? 0.5f : 1, GlidingMoveSpeedFast = id == 1371 ? 10 : 7,
            GlidingFallSpeedFast = id == 1371 ? 3 : 2.5f
        };
        templates[id] = template;
        var player = new LandingPlayer { ObjId = 1, Faction = new SystemFaction(), Hp = 9246, ParentWorld = CreateWorld(true) };
        var flight = new Buff(player, player, new SkillCasterUnit(1), template, null, DateTime.UtcNow);
        player.Buffs.AddBuff(flight);
        return (player, flight, results);

        BuffTriggerTemplate Trigger(BuffEventTriggerKind kind, uint result) =>
            new() { Kind = kind, Effect = new LandingReceipt(results) { Buff = templates[result], Chance = 100, Stack = 1 } };
    }

    private readonly Dictionary<FieldInfo, object> _gliderManagers = [];
    private void SetGliderManager<T>(T instance) where T : class
    {
        var field = typeof(Singleton<T>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        _gliderManagers[field] = field.GetValue(null);
        field.SetValue(null, instance);
    }

    [After(Test)]
    public void RestoreGliderManagers()
    {
        foreach (var (field, previous) in _gliderManagers)
            field.SetValue(null, previous);
    }

    private sealed class LandingPlayer() : Character(null)
    {
        public override int MaxHp => 9246;
        public override void BroadcastPacket(GamePacket packet, bool self) { }
    }

    private sealed class LandingReceipt(List<uint> results) : BuffEffect
    {
        public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
            CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time, CompressedGamePackets packetBuilder = null)
        {
            results.Add(Buff.Id);
            base.Apply(caster, casterObj, target, targetObj, castObj, source, skillObject, time, packetBuilder);
        }
    }
}
