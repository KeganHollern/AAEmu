using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

[NotInParallel]
public sealed class PriestSkillAuthorizationTests
{
    private static readonly FieldInfo WorldManagerInstance = typeof(Singleton<WorldManager>)
        .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic);
    private object _previousWorldManager;
    private WorldInstance _world;
    private Character _character;
    private Npc _priest;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldManager = WorldManagerInstance.GetValue(null);
        var manager = new WorldManager(null, null, null, null, null);
        WorldManagerInstance.SetValue(null, manager);
        _world = new WorldInstance(new WorldTemplate { Id = 1 }, 0, true, 1);
        ((ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager))[1] = _world;
        var region = new Region(_world, 0, 0, 0);
        _character = new Character(null) { ObjId = 70, Hp = 100, ParentWorld = _world, Region = region };
        _character.Transform.Local.SetPosition(32, 32, 0);
        _priest = new Npc { ObjId = 80, Hp = 100, ParentWorld = _world, Template = new NpcTemplate { Priest = true } };
        _priest.Transform.Local.SetPosition(32, 32, 0);
        _world.AddObject(_priest);
        region.AddObject(_priest);
    }

    [After(Test)]
    public void TearDown()
    {
        WorldManagerInstance.SetValue(null, _previousWorldManager);
    }

    [Test]
    [Arguments(9.99f, true)]
    [Arguments(10f, true)]
    [Arguments(10.01f, false)]
    public async Task CanUse_ExactPrayerRequiresPriestWithinRecoveryRange(float distance, bool expected)
    {
        _priest.Transform.Local.SetPosition(32, 32, distance);

        await Assert.That(CanUse()).IsEqualTo(expected);
    }

    [Test]
    public async Task CanUse_MissingPriestDoesNotGrantPrayer()
    {
        _character.Region = null;

        await Assert.That(CanUse()).IsFalse();
    }

    [Test]
    public async Task CanUse_DeadOrNonPriestDoesNotGrantPrayer()
    {
        _priest.Hp = 0;
        await Assert.That(CanUse()).IsFalse();
        _priest.Hp = 100;
        _priest.Template.Priest = false;
        await Assert.That(CanUse()).IsFalse();
    }

    [Test]
    public async Task CanUse_StaleObjectInRegionDoesNotGrantPrayer()
    {
        _world.RemoveObject(_priest);
        _world.AddObject(new Npc { ObjId = 80, Hp = 100, ParentWorld = _world, Template = _priest.Template });

        await Assert.That(CanUse()).IsFalse();
    }

    [Test]
    public async Task CanUse_OtherWorldDoesNotGrantPrayer()
    {
        _priest.ParentWorld = new WorldInstance(new WorldTemplate { Id = 2 }, 0, true, 2);

        await Assert.That(CanUse()).IsFalse();
    }

    [Test]
    public async Task CanUse_ExactOwnUnitAndSelfTargetAreRequired()
    {
        var template = Prayer();
        await Assert.That(PriestSkillAuthorization.CanUse(_character, template,
            new SkillCasterUnit(71), new SkillCastUnitTarget(70))).IsFalse();
        await Assert.That(PriestSkillAuthorization.CanUse(_character, template,
            new SkillCasterUnit(70), new SkillCastUnitTarget(80))).IsFalse();
        await Assert.That(PriestSkillAuthorization.CanUse(_character, template,
            new SkillCasterUnk1(70), new SkillCastUnitTarget(70))).IsFalse();
        await Assert.That(PriestSkillAuthorization.CanUse(_character, template,
            new SkillCasterUnit(70), new SkillCastDoodadTarget { ObjId = 70 })).IsFalse();
    }

    [Test]
    public async Task CanUse_FullRecoveryScrollDoesNotGainAUnitGrant()
    {
        var template = Prayer();
        template.Id = 26611;

        await Assert.That(CanUse(template)).IsFalse();
    }

    [Test]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, true)]
    public async Task CanUse_RequiresTheAuthoredPriestAndLaborEffect(bool priest, bool labor, bool money)
    {
        var template = Prayer();
        template.Effects[0].Template = new RecoverExpEffect { NeedPriest = priest, NeedLaborPower = labor, NeedMoney = money };

        await Assert.That(CanUse(template)).IsFalse();
    }

    private bool CanUse(SkillTemplate template = null)
    {
        return PriestSkillAuthorization.CanUse(_character, template ?? Prayer(),
            new SkillCasterUnit(70), new SkillCastUnitTarget(70));
    }

    private static SkillTemplate Prayer()
    {
        return new SkillTemplate
        {
            Id = 17063,
            TargetType = SkillTargetType.Self,
            Effects = [new SkillEffect { Template = new RecoverExpEffect { NeedPriest = true, NeedLaborPower = true } }]
        };
    }
}
